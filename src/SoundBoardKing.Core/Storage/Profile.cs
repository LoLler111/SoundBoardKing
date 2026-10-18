using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Core.Storage;

/// <summary>Everything the user can carry to another PC: the whole library plus the settings.</summary>
public sealed class Profile
{
    public const string FormatName = "SoundBoardKing.Profile";

    /// <summary>Tells a profile apart from any other JSON file the user might pick; set when saving.</summary>
    public string? Format { get; set; }

    public int Version { get; set; } = 1;
    public AppSettings Settings { get; set; } = new();
    public SoundLibrary Library { get; set; } = new();
}
