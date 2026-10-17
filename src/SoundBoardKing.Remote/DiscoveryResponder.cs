using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SoundBoardKing.Remote;

/// <summary>Answers "SBK_DISCOVER_V1" UDP broadcasts so phones find the PC without typing an address.</summary>
internal sealed class DiscoveryResponder(int port, Func<byte[]> reply) : IDisposable
{
    public const string Request = "SBK_DISCOVER_V1";

    // Without this, an ICMP "port unreachable" from a previous reply makes the next receive fail.
    private const int SioUdpConnReset = -1744830452;

    private readonly CancellationTokenSource _stop = new();
    private UdpClient? _udp;

    public void Start()
    {
        var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.IOControl(SioUdpConnReset, [0, 0, 0, 0], null);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        _udp = udp;
        _ = Task.Run(ListenAsync);
    }

    private async Task ListenAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            UdpReceiveResult message;
            try
            {
                message = await _udp!.ReceiveAsync(_stop.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is SocketException or ObjectDisposedException)
            {
                if (_stop.IsCancellationRequested)
                    break;
                continue;
            }

            if (Encoding.ASCII.GetString(message.Buffer).Trim() != Request)
                continue;
            try
            {
                await _udp.SendAsync(reply(), message.RemoteEndPoint, _stop.Token);
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _udp?.Dispose();
        _stop.Dispose();
    }
}
