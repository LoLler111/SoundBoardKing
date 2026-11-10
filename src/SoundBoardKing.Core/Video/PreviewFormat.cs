namespace SoundBoardKing.Core.Video;

/// <summary>
/// How the audio of a video is held in memory while choosing the piece to keep. Up to 15 minutes it is
/// the source itself (what is heard is what gets saved, cut straight from it); a longer video is held at
/// a lower rate, mono if need be, just to see and hear where things are, and the chosen piece is then
/// read again from the file at full quality.
/// </summary>
public readonly record struct PreviewFormat(int SampleRate, int Channels, bool IsFullQuality)
{
    /// <summary>Longest audio held at full quality: as the sound editor, which opens up to 15 minutes.</summary>
    public static readonly TimeSpan FullQualityLimit = TimeSpan.FromMinutes(15);

    /// <summary>Samples held at most (256 MB of floats).</summary>
    internal const long SampleBudget = 64L << 20;

    /// <summary>The rate the saved sound gets: the source's, at most 48 kHz.</summary>
    public static int SaveRateFor(int sourceRate) => sourceRate is >= 8000 and <= 48000 ? sourceRate : 48000;

    public static PreviewFormat Choose(TimeSpan duration, int sourceRate, int sourceChannels)
    {
        int channels = Math.Clamp(sourceChannels, 1, 2);
        if (duration <= FullQualityLimit && sourceRate is >= 8000 and <= 48000)
            return new PreviewFormat(sourceRate, channels, true);

        (int Rate, int Channels)[] candidates = [(48000, channels), (32000, channels), (24000, channels), (16000, 1), (11025, 1), (8000, 1)];
        foreach (var (rate, ch) in candidates)
        {
            if (duration.TotalSeconds * rate * ch <= SampleBudget)
                return new PreviewFormat(rate, ch, false);
        }
        return new PreviewFormat(8000, 1, false);
    }
}
