using System.Security.Cryptography;
using System.Text;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.Remote;

/// <summary>Paired phones and their tokens. Thread-safe; <c>persist</c> runs after every change.</summary>
public sealed class DeviceRegistry(IEnumerable<PairedDevice> devices, Action<IReadOnlyList<PairedDevice>> persist)
{
    private const int MaxNameLength = 60;

    private readonly object _gate = new();
    private readonly List<PairedDevice> _devices = [.. devices];

    public event EventHandler? Changed;

    public IReadOnlyList<PairedDevice> Devices
    {
        get { lock (_gate) return [.. _devices]; }
    }

    public PairedDevice? Authenticate(string? token)
    {
        if (string.IsNullOrEmpty(token))
            return null;
        var hash = Hash(token);
        lock (_gate)
        {
            return _devices.FirstOrDefault(d =>
                CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(d.TokenHash), Encoding.ASCII.GetBytes(hash)));
        }
    }

    /// <summary>Registers a phone and returns the token it must present from now on.</summary>
    public (PairedDevice Device, string Token) Add(string? name)
    {
        var token = Base64Url(RandomNumberGenerator.GetBytes(32));
        var cleanName = string.IsNullOrWhiteSpace(name) ? "Telefono" : name.Trim();
        var device = new PairedDevice
        {
            Name = cleanName.Length > MaxNameLength ? cleanName[..MaxNameLength] : cleanName,
            TokenHash = Hash(token),
        };
        lock (_gate)
            _devices.Add(device);
        OnChanged();
        return (device, token);
    }

    public bool Remove(Guid deviceId)
    {
        bool removed;
        lock (_gate)
            removed = _devices.RemoveAll(d => d.Id == deviceId) > 0;
        if (removed)
            OnChanged();
        return removed;
    }

    private void OnChanged()
    {
        persist(Devices);
        Changed?.Invoke(this, EventArgs.Empty);
    }

    internal static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
