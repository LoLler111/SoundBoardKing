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
