namespace SoundBoardKing.Core.Storage;

public sealed class AppSettings
{
    /// <summary>Output device id (headphones/speakers); null follows the Windows default.</summary>
    public string? OutputDeviceId { get; set; }

    public float SpeakerVolume { get; set; } = 0.8f;
    public float MicVolume { get; set; } = 1f;

    /// <summary>Also send sounds into the microphone when the mic module is available (phase 2).</summary>
    public bool PlayOnMic { get; set; } = true;

    /// <summary>When false, starting a sound stops the one already playing.</summary>
    public bool AllowOverlap { get; set; }

    public string? StopHotkey { get; set; }
    public string? PauseHotkey { get; set; }

    public int HotbarPage { get; set; }
    public string? LastImportFolder { get; set; }
}
