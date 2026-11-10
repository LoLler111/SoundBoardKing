using System.Globalization;
using System.Text.Json;

namespace SoundBoardKing.Core.Video;

/// <summary>What is inside a video (or audio) file, as FFprobe reads it.</summary>
public sealed record MediaInfo(
    TimeSpan Duration,
    string Container,
    int AudioSampleRate,
    int AudioChannels,
    string? VideoCodec,
    int Width,
    int Height)
{
    public bool HasAudio => AudioSampleRate > 0 && AudioChannels > 0;
    public bool HasVideo => VideoCodec is not null;

    /// <summary>
    /// Whether the player built into Windows shows the video as it is: H.264 in an MP4 or MOV file,
    /// which is what nearly every site and phone gives. Anything else is converted for the preview.
    /// </summary>
    public bool IsPlayableByWindows =>
        VideoCodec == "h264" &&
        Container.Split(',').Any(c => c is "mp4" or "mov" or "m4v");
}

public static class MediaProbe
{
    /// <summary>Reads the streams of <paramref name="path"/>.</summary>
    /// <exception cref="InvalidDataException">FFprobe cannot read it: not a video or audio file.</exception>
    public static async Task<MediaInfo> ProbeAsync(VideoTools tools, string path, CancellationToken cancel)
    {
        var result = await ToolProcess.RunAsync(tools.FfprobePath,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], cancel: cancel);
        if (!result.Succeeded)
            throw new InvalidDataException(result.Errors.LastOrDefault() ?? "File non leggibile.");
        return Parse(string.Join('\n', result.Output));
    }

    /// <summary>Reads FFprobe's JSON (-show_format -show_streams).</summary>
    /// <exception cref="InvalidDataException">Neither audio nor video inside.</exception>
    internal static MediaInfo Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        double duration = 0;
        string container = "";
        if (root.TryGetProperty("format", out var format))
        {
            duration = Number(format, "duration");
            container = format.TryGetProperty("format_name", out var name) ? name.GetString() ?? "" : "";
        }

        int sampleRate = 0, channels = 0, width = 0, height = 0;
        string? videoCodec = null;
        double streamDuration = 0;
        if (root.TryGetProperty("streams", out var streams))
        {
            foreach (var stream in streams.EnumerateArray())
            {
                var type = stream.TryGetProperty("codec_type", out var t) ? t.GetString() : null;
                var codec = stream.TryGetProperty("codec_name", out var c) ? c.GetString() : null;
                // A cover picture in an mp3 or m4a is a "video" stream of one frame: not a video.
                bool attachedPicture = stream.TryGetProperty("disposition", out var disposition) &&
                    disposition.TryGetProperty("attached_pic", out var pic) && pic.ValueKind == JsonValueKind.Number && pic.GetInt32() == 1;
                if (type == "audio" && sampleRate == 0)
                {
                    sampleRate = (int)Number(stream, "sample_rate");
                    channels = stream.TryGetProperty("channels", out var ch) && ch.ValueKind == JsonValueKind.Number ? ch.GetInt32() : 0;
                    streamDuration = Math.Max(streamDuration, Number(stream, "duration"));
                }
                else if (type == "video" && videoCodec is null && !attachedPicture && codec is not null)
                {
                    videoCodec = codec;
                    width = stream.TryGetProperty("width", out var w) && w.ValueKind == JsonValueKind.Number ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number ? h.GetInt32() : 0;
                    streamDuration = Math.Max(streamDuration, Number(stream, "duration"));
                }
            }
        }
        if (sampleRate == 0 && videoCodec is null)
            throw new InvalidDataException("Nel file non c'è né audio né video.");
        if (duration <= 0)
            duration = streamDuration;
        return new MediaInfo(TimeSpan.FromSeconds(duration), container, sampleRate, channels, videoCodec, width, height);
    }

    // FFprobe writes numbers as strings ("44100", "12.345000").
    private static double Number(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            return 0;
        if (value.ValueKind == JsonValueKind.Number)
            return value.GetDouble();
        return value.ValueKind == JsonValueKind.String &&
            double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var number)
            ? number
            : 0;
    }
}
