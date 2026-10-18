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
}
