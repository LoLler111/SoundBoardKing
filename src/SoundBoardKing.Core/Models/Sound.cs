namespace SoundBoardKing.Core.Models;

public sealed class Sound
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Artist { get; set; } = "";
    public string Path { get; set; } = "";
    public long DurationMs { get; set; }

    /// <summary>Per-sound gain, 0..1, applied on top of the output volume.</summary>
    public float Volume { get; set; } = 1f;

    /// <summary>Global hotkey in "Ctrl+Alt+1" form, or null.</summary>
    public string? Hotkey { get; set; }

    public DateOnly AddedOn { get; set; } = DateOnly.FromDateTime(DateTime.Today);

    /// <summary>How many times it was played (previews excluded), for the "most played" order.</summary>
    public int PlayCount { get; set; }

    /// <summary>When it was last played, UTC.</summary>
    public DateTime? LastPlayed { get; set; }

    public Sound WithPath(string path)
    {
        var copy = (Sound)MemberwiseClone();
        copy.Path = path;
        return copy;
    }
}
