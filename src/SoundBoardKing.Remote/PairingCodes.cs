using System.Security.Cryptography;
using System.Text;

namespace SoundBoardKing.Remote;

/// <summary>
/// The 6-digit code shown on the PC while the pairing window is open. Single use, expires,
/// and is thrown away after a few wrong guesses.
/// </summary>
public sealed class PairingCodes(TimeProvider? time = null)
{
    public const int MaxFailures = 5;
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly object _gate = new();
    private string? _code;
    private DateTimeOffset _expires;
    private int _failures;

    public string Start()
    {
        lock (_gate)
        {
            _code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
            _expires = _time.GetUtcNow() + Lifetime;
            _failures = 0;
            return _code;
        }
    }

    public void Stop()
    {
        lock (_gate)
            _code = null;
    }

    public string? Current
    {
        get
        {
            lock (_gate)
                return _code is not null && _time.GetUtcNow() < _expires ? _code : null;
        }
    }

    public bool TryConsume(string? code)
    {
        lock (_gate)
        {
            if (_code is null || _time.GetUtcNow() >= _expires || code is null)
                return false;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(_code), Encoding.ASCII.GetBytes(code.Trim())))
            {
                _code = null;
                return true;
            }
            if (++_failures >= MaxFailures)
                _code = null;
            return false;
        }
    }
}
