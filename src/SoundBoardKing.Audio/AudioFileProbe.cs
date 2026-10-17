using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundBoardKing.Audio;

public static class AudioFileProbe
{
    /// <summary>Extensions offered when adding sounds; anything Media Foundation can decode will play.</summary>
    public static readonly IReadOnlyList<string> SupportedExtensions =
    [
        ".mp3", ".wav", ".m4a", ".aac", ".mp4", ".m4v", ".wma", ".flac", ".ogg", ".opus", ".3gp", ".mpeg", ".mpg", ".webm",
    ];

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static TimeSpan? TryGetDuration(string path)
    {
        try
        {
            using var reader = new MediaFoundationReader(path);
            return reader.TotalTime;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return null;
        }
    }
}
