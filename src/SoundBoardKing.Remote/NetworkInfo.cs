using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SoundBoardKing.Remote;

public static class NetworkInfo
{
    private static readonly string[] VirtualAdapterHints = ["virtual", "hyper-v", "vmware", "virtualbox", "vethernet", "wsl", "vpn"];

    /// <summary>
    /// IPv4 addresses a phone on the same network can reach, most likely first: real adapters
    /// with a default gateway before virtual and VPN adapters.
    /// </summary>
    public static IReadOnlyList<string> LanAddresses()
    {
        return NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .Select(n => (Adapter: n, Properties: n.GetIPProperties()))
            .OrderByDescending(x => x.Properties.GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any)))
            .ThenBy(x => IsVirtual(x.Adapter))
            .SelectMany(x => x.Properties.UnicastAddresses
                .Select(a => a.Address)
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                            !a.ToString().StartsWith("169.254.", StringComparison.Ordinal)))
            .Select(a => a.ToString())
            .Distinct()
            .ToList();
    }

    private static bool IsVirtual(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return VirtualAdapterHints.Any(text.Contains);
    }

    /// <summary>Content of the pairing QR code: sbk://pair?h=ip1,ip2&amp;p=port&amp;c=code&amp;n=name</summary>
    public static string PairingLink(IEnumerable<string> addresses, int port, string code, string name) =>
        $"sbk://pair?h={string.Join(",", addresses.Take(3))}&p={port}&c={code}&n={Uri.EscapeDataString(name)}";
}
