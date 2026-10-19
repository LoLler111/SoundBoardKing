using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Remote;

public sealed class RemoteServerOptions
{
    /// <summary>TCP port for HTTP/WebSocket and UDP port for discovery. 0 picks a free port (tests).</summary>
    public int Port { get; init; } = 8765;

    public string Name { get; init; } = Environment.MachineName;
    public string ServerId { get; init; } = "";
    public bool EnableDiscovery { get; init; } = true;
}

/// <summary>
/// HTTP + WebSocket server for the phone remote (docs/PROTOCOLLO_REMOTO.md), hosted inside the app.
/// </summary>
public sealed class RemoteServer(IRemoteController controller, DeviceRegistry devices, RemoteServerOptions options)
    : IAsyncDisposable
{
    public const int Protocol = 1;
    private const int MaxMessageBytes = 64 * 1024;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<Guid, Client> _clients = new();
    private WebApplication? _app;
    private DiscoveryResponder? _discovery;

    public PairingCodes Pairing { get; } = new();
    public int Port { get; private set; }
    public bool IsRunning => _app is not null;
    public int ConnectedClients => _clients.Count;

    /// <summary>Raised on a request thread when a phone completes pairing.</summary>
    public event EventHandler<PairedDevice>? DevicePaired;

    public async Task StartAsync()
    {
        if (_app is not null)
            return;

        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(options.Port));

        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });
        MapEndpoints(app);
        await app.StartAsync();

        _app = app;
        Port = ResolvePort(app);
        if (options.EnableDiscovery)
        {
            _discovery = new DiscoveryResponder(Port, DiscoveryReply);
            _discovery.Start();
        }
    }

    public async Task StopAsync()
    {
        _discovery?.Dispose();
        _discovery = null;
        foreach (var client in _clients.Values)
            client.Abort();
        _clients.Clear();
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    public async ValueTask DisposeAsync() => await StopAsync();

    /// <summary>Sends an event (an object with a "type" property) to every connected phone.</summary>
    public void Broadcast(object message)
    {
        if (_clients.IsEmpty)
            return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, Json);
        foreach (var client in _clients.Values)
            _ = client.SendAsync(bytes);
    }

    /// <summary>Drops the live connections of a device whose pairing was revoked.</summary>
    public void Disconnect(Guid deviceId)
    {
        foreach (var client in _clients.Values.Where(c => c.DeviceId == deviceId))
        {
            client.Abort();
            _clients.TryRemove(client.Id, out _);
        }
    }

    private void MapEndpoints(WebApplication app)
    {
        app.MapGet("/api/info", () => Results.Json(new { name = options.Name, protocol = Protocol, serverId = options.ServerId }, Json));

        app.MapPost("/api/pair", async (HttpContext context) =>
        {
            PairRequest? request;
            try
            {
                request = await context.Request.ReadFromJsonAsync<PairRequest>(Json);
            }
            catch (JsonException)
            {
                return Results.BadRequest();
            }
            if (request is null || !Pairing.TryConsume(request.Code))
                return Results.Json(new { error = "Codice non valido o scaduto." }, Json, statusCode: StatusCodes.Status403Forbidden);

            var (device, token) = devices.Add(request.DeviceName);
            DevicePaired?.Invoke(this, device);
            return Results.Json(new { token, deviceId = device.Id }, Json);
        });

        app.MapGet("/api/state", async (HttpContext context) =>
            Authenticate(context) is null ? Results.Unauthorized() : Results.Json(await GetStateAsync(), Json));

        app.MapPost("/api/play/{soundId:guid}", async (HttpContext context, Guid soundId) =>
        {
            if (Authenticate(context) is null)
                return Results.Unauthorized();
            if (!TryParseTarget(context.Request.Query["target"], out var target))
                return Results.Json(new { error = "target non valido: usa speakers o mic." }, Json, statusCode: StatusCodes.Status400BadRequest);
            var result = await controller.PlayAsync(soundId, target);
            if (!result.Found)
                return Results.NotFound();
            return result.Error is null
                ? Results.NoContent()
                : Results.Json(new { error = result.Error }, Json, statusCode: StatusCodes.Status409Conflict);
        });

        app.MapPost("/api/stop", async (HttpContext context) =>
        {
            if (Authenticate(context) is null)
                return Results.Unauthorized();
            await controller.StopAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/stop/{playbackId:guid}", async (HttpContext context, Guid playbackId) =>
        {
            if (Authenticate(context) is null)
                return Results.Unauthorized();
            return await controller.StopOneAsync(playbackId) ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/pause", async (HttpContext context) =>
        {
            if (Authenticate(context) is null)
                return Results.Unauthorized();
            await controller.TogglePauseAsync();
            return Results.NoContent();
        });

        app.MapPost("/api/pause/{playbackId:guid}", async (HttpContext context, Guid playbackId) =>
        {
            if (Authenticate(context) is null)
                return Results.Unauthorized();
            return await controller.TogglePauseOneAsync(playbackId) ? Results.NoContent() : Results.NotFound();
        });

        app.MapPost("/api/effect/{name}", async (HttpContext context, string name) =>
        {
            if (Authenticate(context) is null)
                return Results.Unauthorized();
            if (!EffectNames.TryParse(name, out var effect))
                return Results.BadRequest(new { error = $"Effetto sconosciuto: {name}" });
            await controller.SetEffectAsync(effect);
            return Results.NoContent();
        });

        app.Map("/ws", HandleWebSocketAsync);
    }

    private async Task<StateDto> GetStateAsync() =>
        await controller.GetStateAsync() with { ServerId = options.ServerId, Name = options.Name, Protocol = Protocol };

    private PairedDevice? Authenticate(HttpContext context)
    {
        string? token = null;
        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            token = header["Bearer ".Length..].Trim();
        else if (context.Request.Query.TryGetValue("token", out var query))
            token = query.ToString();
        return devices.Authenticate(token);
    }

    private async Task HandleWebSocketAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var device = Authenticate(context);
        if (device is null)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var client = new Client(socket, device.Id);
        _clients[client.Id] = client;
        try
        {
            await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(
                new { type = "hello", libraryVersion = controller.LibraryVersion }, Json));

            var buffer = new byte[4096];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open)
            {
                var result = await socket.ReceiveAsync(buffer, context.RequestAborted);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                    break;
                if (!result.EndOfMessage)
                    continue;

                await HandleCommandAsync(client, Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length));
                message.SetLength(0);
            }
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException)
        {
        }
        finally
        {
            _clients.TryRemove(client.Id, out _);
        }
    }

    private async Task HandleCommandAsync(Client client, string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "play":
                    if (!root.TryGetProperty("soundId", out var idElement) || !idElement.TryGetGuid(out var soundId))
                    {
                        await SendErrorAsync(client, "soundId mancante o non valido.");
                    }
                    else if (!TryParseTarget(root.TryGetProperty("target", out var targetElement) ? targetElement.GetString() : null, out var target))
                    {
                        await SendErrorAsync(client, "target non valido: usa speakers o mic.");
                    }
                    else
                    {
                        var result = await controller.PlayAsync(soundId, target);
                        if (result.Error is not null)
                            await SendErrorAsync(client, result.Error);
                    }
                    break;
                case "stop":
                    await controller.StopAsync();
                    break;
                case "stopOne":
                    // A sound that already ended is not an error: the phone just has not heard yet.
                    if (root.TryGetProperty("playbackId", out var playbackElement) && playbackElement.TryGetGuid(out var playbackId))
                        await controller.StopOneAsync(playbackId);
                    else
                        await SendErrorAsync(client, "playbackId mancante o non valido.");
                    break;
                case "togglePause":
                    await controller.TogglePauseAsync();
                    break;
                case "togglePauseOne":
                    if (root.TryGetProperty("playbackId", out var pauseElement) && pauseElement.TryGetGuid(out var pauseId))
                        await controller.TogglePauseOneAsync(pauseId);
                    else
                        await SendErrorAsync(client, "playbackId mancante o non valido.");
                    break;
                case "setMic":
                    if (root.TryGetProperty("enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False)
                        await controller.SetMicEnabledAsync(enabled.GetBoolean());
                    else
                        await SendErrorAsync(client, "enabled mancante.");
                    break;
                case "setEffect":
                    if (root.TryGetProperty("effect", out var effectElement) && effectElement.ValueKind == JsonValueKind.String &&
                        EffectNames.TryParse(effectElement.GetString(), out var effect))
                        await controller.SetEffectAsync(effect);
                    else
                        await SendErrorAsync(client, "effect mancante o non valido.");
                    break;
                default:
                    await SendErrorAsync(client, $"Comando sconosciuto: {type}");
                    break;
            }
        }
        catch (JsonException)
        {
            await SendErrorAsync(client, "Messaggio non valido.");
        }
    }

    /// <summary>"speakers" (preview, headphones only) or "mic" (microphone only); absent means the usual.</summary>
    private static bool TryParseTarget(string? text, out PlayTarget target)
    {
        target = text switch
        {
            null or "" or "default" => PlayTarget.Default,
            "speakers" => PlayTarget.Speakers,
            "mic" => PlayTarget.Mic,
            _ => (PlayTarget)(-1),
        };
        return Enum.IsDefined(target);
    }

    private static Task SendErrorAsync(Client client, string message) =>
        client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "error", message }, Json));

    private byte[] DiscoveryReply() => JsonSerializer.SerializeToUtf8Bytes(new
    {
        service = "SoundBoardKing",
        protocol = Protocol,
        name = options.Name,
        port = Port,
        serverId = options.ServerId,
    }, Json);

    private static int ResolvePort(WebApplication app)
    {
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new Uri(address.Replace("://+", "://localhost").Replace("://*", "://localhost").Replace("[::]", "localhost")).Port;
    }

    private sealed class Client(WebSocket socket, Guid deviceId)
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public Guid Id { get; } = Guid.NewGuid();
        public Guid DeviceId { get; } = deviceId;

        // WebSocket allows one send at a time; broadcasts and replies may overlap.
        public async Task SendAsync(byte[] data)
        {
            await _sendLock.WaitAsync();
            try
            {
                if (socket.State == WebSocketState.Open)
                    await socket.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, CancellationToken.None);
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
            {
            }
            finally
            {
                _sendLock.Release();
            }
        }

        public void Abort() => socket.Abort();
    }
}
