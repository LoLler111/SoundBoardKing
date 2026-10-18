namespace SoundBoardKing.Core.Storage;

/// <summary>
/// Settings that travel with the profile. Anything tied to this PC's audio devices belongs in
/// <see cref="LocalSettings"/> instead.
/// </summary>
public sealed class AppSettings
{
    public float SpeakerVolume { get; set; } = 0.8f;
    public float MicVolume { get; set; } = 1f;

    /// <summary>Also send sounds into the microphone when the mic module is available (phase 2).</summary>
    public bool PlayOnMic { get; set; } = true;

    /// <summary>When false, starting a sound stops the one already playing.</summary>
    public bool AllowOverlap { get; set; }

    public string? StopHotkey { get; set; }
    public string? PauseHotkey { get; set; }

    public int HotbarPage { get; set; }

    /// <summary>Lets paired phones control the app over the local network.</summary>
    public bool RemoteEnabled { get; set; } = true;

    public int RemotePort { get; set; } = 8765;

    /// <summary>Stable id so a phone recognises this PC even when its IP address changes.</summary>
    public string ServerId { get; set; } = Guid.NewGuid().ToString("N");

    public List<PairedDevice> RemoteDevices { get; set; } = [];
}
