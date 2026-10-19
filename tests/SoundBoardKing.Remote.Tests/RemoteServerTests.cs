using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Remote.Tests;

public sealed class RemoteServerTests : IAsyncLifetime
{
    private static readonly Guid Airhorn = Guid.NewGuid();

    private readonly FakeController _controller = new();
    private readonly List<IReadOnlyList<PairedDevice>> _persisted = [];
    private DeviceRegistry _devices = null!;
    private RemoteServer _server = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _devices = new DeviceRegistry([], _persisted.Add);
        _server = new RemoteServer(_controller, _devices, new RemoteServerOptions
        {
            Port = 0,
            Name = "PC-TEST",
            ServerId = "server-1",
            EnableDiscovery = false,
        });
        await _server.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_server.Port}") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.DisposeAsync();
    }

    private async Task<string> PairAsync(string deviceName = "Pixel")
    {
        var code = _server.Pairing.Start();
        var response = await _http.PostAsJsonAsync("/api/pair", new { code, deviceName });
        response.EnsureSuccessStatusCode();
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return body.RootElement.GetProperty("token").GetString()!;
    }

    private HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    [Fact]
    public async Task Info_is_public()
    {
        using var body = JsonDocument.Parse(await _http.GetStringAsync("/api/info"));
        Assert.Equal("PC-TEST", body.RootElement.GetProperty("name").GetString());
        Assert.Equal(1, body.RootElement.GetProperty("protocol").GetInt32());
    }

    [Fact]
    public async Task Everything_else_needs_a_token()
    {
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.GetAsync("/api/state")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _http.PostAsync($"/api/play/{Airhorn}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _http.SendAsync(Authorized(HttpMethod.Get, "/api/state", "inventato"))).StatusCode);
        Assert.Empty(_controller.Played);
    }

    [Fact]
    public async Task Pairing_returns_a_token_and_stores_only_its_hash()
    {
        var token = await PairAsync("Pixel 8");

        var device = Assert.Single(_devices.Devices);
        Assert.Equal("Pixel 8", device.Name);
        Assert.NotEqual(token, device.TokenHash);
        Assert.Single(_persisted);

        // The code is single use.
        var reused = await _http.PostAsJsonAsync("/api/pair", new { code = "000000", deviceName = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, reused.StatusCode);
    }

    [Fact]
    public async Task Wrong_codes_burn_the_pairing_session()
    {
        var code = _server.Pairing.Start();
        var wrong = code == "111111" ? "222222" : "111111";
        for (int i = 0; i < PairingCodes.MaxFailures; i++)
            await _http.PostAsJsonAsync("/api/pair", new { code = wrong, deviceName = "x" });

        var response = await _http.PostAsJsonAsync("/api/pair", new { code, deviceName = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Empty(_devices.Devices);
    }

    [Fact]
    public async Task State_and_rest_commands_work_with_a_token()
    {
        var token = await PairAsync();

        var stateResponse = await _http.SendAsync(Authorized(HttpMethod.Get, "/api/state", token));
        stateResponse.EnsureSuccessStatusCode();
        using var state = JsonDocument.Parse(await stateResponse.Content.ReadAsStringAsync());
        var root = state.RootElement;
        Assert.Equal("server-1", root.GetProperty("serverId").GetString());
        Assert.Equal("airhorn", root.GetProperty("sounds")[0].GetProperty("title").GetString());
        Assert.Equal(Airhorn, root.GetProperty("hotbar").GetProperty("slots")[0].GetProperty("soundId").GetGuid());
        var playing = root.GetProperty("playback").GetProperty("playing");
        Assert.Equal(2, playing.GetArrayLength());
        Assert.Equal(FakeController.First, playing[0].GetProperty("id").GetGuid());
        Assert.Equal(1500, playing[0].GetProperty("positionMs").GetInt64());

        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/play/{Airhorn}", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/play/{Guid.NewGuid()}", token))).StatusCode);
        var broken = await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/play/{FakeController.Broken}", token));
        Assert.Equal(HttpStatusCode.Conflict, broken.StatusCode);
        Assert.Contains("File non trovato", await broken.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/play/{Airhorn}?target=mic", token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/play/{Airhorn}?target=altrove", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(Authorized(HttpMethod.Post, "/api/stop", token))).StatusCode);
        Assert.Equal([Airhorn, Airhorn], _controller.Played);
        Assert.Equal([PlayTarget.Default, PlayTarget.Mic], _controller.Targets);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/stop/{FakeController.First}", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/stop/{Guid.NewGuid()}", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/pause/{FakeController.Second}", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound,
            (await _http.SendAsync(Authorized(HttpMethod.Post, $"/api/pause/{Guid.NewGuid()}", token))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent,
            (await _http.SendAsync(Authorized(HttpMethod.Post, "/api/effect/robot", token))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await _http.SendAsync(Authorized(HttpMethod.Post, "/api/effect/banana", token))).StatusCode);
        Assert.Equal([SoundEffect.Robot], _controller.Effects);
        Assert.Equal(1, _controller.Stops);
    }

    [Fact]
    public async Task Websocket_carries_commands_and_events()
    {
        var token = await PairAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/ws?token={token}"), CancellationToken.None);

        using (var hello = await ReceiveAsync(socket))
        {
            Assert.Equal("hello", hello.RootElement.GetProperty("type").GetString());
            Assert.Equal(42, hello.RootElement.GetProperty("libraryVersion").GetInt64());
        }

        await SendAsync(socket, new { type = "play", soundId = Airhorn });
        await SendAsync(socket, new { type = "play", soundId = Airhorn, target = "speakers" });
        await SendAsync(socket, new { type = "togglePause" });
        await SendAsync(socket, new { type = "setMic", enabled = false });
        await SendAsync(socket, new { type = "setEffect", effect = "echo" });
        await SendAsync(socket, new { type = "play", soundId = Guid.NewGuid() });
        using (var error = await ReceiveAsync(socket))
            Assert.Equal("Suono non trovato.", error.RootElement.GetProperty("message").GetString());
        await SendAsync(socket, new { type = "play", soundId = FakeController.Broken });
        using (var error = await ReceiveAsync(socket))
            Assert.Equal("File non trovato", error.RootElement.GetProperty("message").GetString());

        await SendAsync(socket, new { type = "stopOne", playbackId = FakeController.Second });
        await SendAsync(socket, new { type = "togglePauseOne", playbackId = FakeController.First });
        // The connection survives errors.
        await SendAsync(socket, new { type = "stop" });
        Assert.Equal(WebSocketState.Open, socket.State);

        Assert.Equal([PlayTarget.Default, PlayTarget.Speakers], _controller.Targets);
        Assert.Equal(1, _controller.Pauses);
        Assert.Equal([FakeController.Second], _controller.StoppedOnes);
        Assert.Equal([FakeController.First], _controller.PausedOnes);
        Assert.False(_controller.MicEnabled);
        Assert.Equal([SoundEffect.Echo], _controller.Effects);

        _server.Broadcast(new { type = "libraryChanged", libraryVersion = 43 });
        using (var changed = await ReceiveAsync(socket))
            Assert.Equal(43, changed.RootElement.GetProperty("libraryVersion").GetInt64());
    }

    [Fact]
    public async Task Websocket_rejects_unknown_tokens_and_revoked_devices_are_dropped()
    {
        using var stranger = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(() =>
            stranger.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/ws?token=nope"), CancellationToken.None));

        var token = await PairAsync();
        using var socket = new ClientWebSocket();
        await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{_server.Port}/ws?token={token}"), CancellationToken.None);
        (await ReceiveAsync(socket)).Dispose();
        Assert.Equal(1, _server.ConnectedClients);

        var device = Assert.Single(_devices.Devices);
        _devices.Remove(device.Id);
        _server.Disconnect(device.Id);

        Assert.Equal(0, _server.ConnectedClients);
        Assert.Equal(HttpStatusCode.Unauthorized,
            (await _http.SendAsync(Authorized(HttpMethod.Get, "/api/state", token))).StatusCode);
    }

    [Fact]
    public async Task Discovery_answers_broadcast_probes()
    {
        await using var server = new RemoteServer(_controller, _devices, new RemoteServerOptions
        {
            Port = 0,
            Name = "PC-DISCOVERY",
            ServerId = "server-2",
        });
        await server.StartAsync();

        using var udp = new UdpClient(AddressFamily.InterNetwork);
        var probe = Encoding.ASCII.GetBytes("SBK_DISCOVER_V1");
        await udp.SendAsync(probe, new IPEndPoint(IPAddress.Loopback, server.Port));
        var receive = udp.ReceiveAsync();
        Assert.Same(receive, await Task.WhenAny(receive, Task.Delay(5000)));

        using var reply = JsonDocument.Parse((await receive).Buffer);
        Assert.Equal("SoundBoardKing", reply.RootElement.GetProperty("service").GetString());
        Assert.Equal("PC-DISCOVERY", reply.RootElement.GetProperty("name").GetString());
        Assert.Equal(server.Port, reply.RootElement.GetProperty("port").GetInt32());
    }

    private static async Task SendAsync(ClientWebSocket socket, object message) =>
        await socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(message, new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            WebSocketMessageType.Text, true, CancellationToken.None);

    private static async Task<JsonDocument> ReceiveAsync(ClientWebSocket socket)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var buffer = new byte[16 * 1024];
        var result = await socket.ReceiveAsync(buffer, timeout.Token);
        return JsonDocument.Parse(buffer.AsMemory(0, result.Count));
    }

    private sealed class FakeController : IRemoteController
    {
        public List<Guid> Played { get; } = [];
        public List<PlayTarget> Targets { get; } = [];
        public int Stops { get; private set; }
        public int Pauses { get; private set; }
        public bool MicEnabled { get; private set; } = true;
        public long LibraryVersion => 42;

        public Task<StateDto> GetStateAsync() => Task.FromResult(new StateDto(
            "", "", 0, LibraryVersion,
            [new SoundDto(Airhorn, "airhorn", "", 3000, false, null)],
            [new CategoryDto(Guid.NewGuid(), "Voice", [Airhorn], [])],
            new HotbarDto(5, 20, [new HotbarSlotDto(0, 0, Airhorn)]),
            new PlaybackDto("playing", Airhorn, 500, 3000,
                [new PlayingDto(First, Airhorn, 1500, 3000), new PlayingDto(Second, Airhorn, 500, 3000)]),
            new MicDto("idle", MicEnabled)));

        public static readonly Guid First = Guid.NewGuid();
        public static readonly Guid Second = Guid.NewGuid();
        public List<Guid> StoppedOnes { get; } = [];

        public Task<bool> StopOneAsync(Guid playbackId)
        {
            StoppedOnes.Add(playbackId);
            return Task.FromResult(playbackId == First || playbackId == Second);
        }

        public List<Guid> PausedOnes { get; } = [];

        public Task<bool> TogglePauseOneAsync(Guid playbackId)
        {
            PausedOnes.Add(playbackId);
            return Task.FromResult(playbackId == First || playbackId == Second);
        }

        public static readonly Guid Broken = Guid.NewGuid();

        public Task<PlayResult> PlayAsync(Guid soundId, PlayTarget target = PlayTarget.Default)
        {
            if (soundId == Broken)
                return Task.FromResult(PlayResult.Failed("File non trovato"));
            if (soundId != Airhorn)
                return Task.FromResult(PlayResult.NotFound);
            Played.Add(soundId);
            Targets.Add(target);
            return Task.FromResult(PlayResult.Started);
        }

        public Task StopAsync()
        {
            Stops++;
            return Task.CompletedTask;
        }

        public Task TogglePauseAsync()
        {
            Pauses++;
            return Task.CompletedTask;
        }

        public Task SetMicEnabledAsync(bool enabled)
        {
            MicEnabled = enabled;
            return Task.CompletedTask;
        }

        public List<SoundEffect> Effects { get; } = [];

        public Task SetEffectAsync(SoundEffect effect)
        {
            Effects.Add(effect);
            return Task.CompletedTask;
        }
    }
}
