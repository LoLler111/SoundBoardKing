using System.IO;
using System.Net.Sockets;
using System.Windows.Threading;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Remote;

namespace SoundBoardKing.App.Remote;

/// <summary>Owns the phone server for the app: starts and stops it and forwards app events to phones.</summary>
public sealed class RemoteHost : IAsyncDisposable
{
    private readonly MainViewModel _viewModel;
    private readonly Dispatcher _dispatcher;
    private RemoteServer? _server;

    public RemoteHost(MainViewModel viewModel, Dispatcher dispatcher)
    {
        _viewModel = viewModel;
        _dispatcher = dispatcher;
        Devices = new DeviceRegistry(viewModel.RemoteDevices,
            devices => dispatcher.InvokeAsync(() => viewModel.SaveRemoteDevices(devices)));

        viewModel.LibraryChanged += (_, _) =>
            Broadcast(new { type = "libraryChanged", libraryVersion = viewModel.LibraryVersion });
        viewModel.PlaybackChanged += (_, p) =>
            Broadcast(new { type = "playback", p.State, p.SoundId, p.PositionMs, p.DurationMs });
        viewModel.MicChanged += (_, m) =>
            Broadcast(new { type = "mic", m.State, m.Enabled });
    }

    public DeviceRegistry Devices { get; }
    public RemoteServer? Server => _server;
    public string Status { get; private set; } = "Spento";
    public event EventHandler? StatusChanged;

    public async Task StartAsync()
    {
        if (_server is not null)
            return;

        var server = new RemoteServer(new RemoteController(_viewModel, _dispatcher), Devices, new RemoteServerOptions
        {
            Port = _viewModel.RemotePort,
            Name = Environment.MachineName,
            ServerId = _viewModel.ServerId,
        });
        try
        {
            await server.StartAsync();
            _server = server;
            SetStatus($"In ascolto sulla porta {server.Port}");
        }
        catch (Exception ex) when (ex is IOException or SocketException)
        {
            await server.DisposeAsync();
            SetStatus($"Porta {_viewModel.RemotePort} non disponibile: {ex.Message}");
        }
    }

    public async Task StopAsync()
    {
        if (_server is null)
            return;
        await _server.DisposeAsync();
        _server = null;
        SetStatus("Spento");
    }

    public async Task SetEnabledAsync(bool enabled)
    {
        _viewModel.RemoteEnabled = enabled;
        if (enabled)
            await StartAsync();
        else
            await StopAsync();
    }

    public void RemoveDevice(Guid deviceId)
    {
        Devices.Remove(deviceId);
        _server?.Disconnect(deviceId);
    }

    private void Broadcast(object message) => _server?.Broadcast(message);

    private void SetStatus(string status)
    {
        Status = status;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
