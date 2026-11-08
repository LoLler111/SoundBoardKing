namespace SoundBoardKing.Core.Storage;

/// <summary>Settings that only make sense on this PC, so they stay out of the exported profile.</summary>
public sealed class LocalSettings
{
    /// <summary>Output device id (headphones/speakers); null follows the Windows default.</summary>
    public string? OutputDeviceId { get; set; }

    public string? LastImportFolder { get; set; }

    /// <summary>
    /// The profile file the user chose; null means automatic: next to the executable when a
    /// profile is there, otherwise in the app data folder.
    /// </summary>
    public string? ProfilePath { get; set; }

    /// <summary>Volume of the Myinstants preview, 0..1: low to start with, since some uploads are ear-rape.</summary>
    public float PreviewVolume { get; set; } = 0.4f;

    /// <summary>Where the main window was when it closed (screens differ between PCs); null until then.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>Favourite rows in view: more rows make smaller buttons, narrower too.</summary>
    public int HotbarRows { get; set; } = 2;

    /// <summary>Height of the favourites buttons area, in device-independent pixels.</summary>
    public double HotbarHeight { get; set; } = 128;

    /// <summary>Name of the text-to-speech voice last used (the voices installed differ between PCs).</summary>
    public string? TtsVoice { get; set; }

    /// <summary>Speaking rate, -10 (slowest) .. 0 (normal) .. 10 (fastest).</summary>
    public int TtsRate { get; set; }

    /// <summary>Phrases spoken lately, newest first, to say them again with a click.</summary>
    public List<string> TtsHistory { get; set; } = [];

    /// <summary>Compute the neural voices on the graphics card (DirectML) when this PC has one.</summary>
    public bool TtsUseGpu { get; set; } = true;

    /// <summary>The RVC voices set up on this PC (their models are files on this PC).</summary>
    public List<RvcVoiceSetting> RvcVoices { get; set; } = [];
}

/// <summary>An RVC voice: a base voice says the text, the RVC model turns it into its own voice.</summary>
public sealed class RvcVoiceSetting
{
    public string Name { get; set; } = "";
    public string ModelPath { get; set; } = "";
    public string? IndexPath { get; set; }
    public string BaseVoice { get; set; } = "";

    /// <summary>Semitones added to the base voice's pitch.</summary>
    public int Pitch { get; set; }

    /// <summary>How much of the model's index goes into each frame, 0..1.</summary>
    public float IndexRate { get; set; } = 0.66f;

    /// <summary>How much the melody moves around its average: 1 as the base voice, less flatter, more livelier.</summary>
    public float Intonation { get; set; } = 1f;
}

/// <summary>Position and size in device-independent pixels; <see cref="Maximized"/> keeps the normal size too.</summary>
public sealed class WindowPlacement
{
    public double Left { get; set; }
    public double Top { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public bool Maximized { get; set; }
}
