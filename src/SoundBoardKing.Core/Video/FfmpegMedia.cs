using System.Globalization;
using System.Runtime.InteropServices;

namespace SoundBoardKing.Core.Video;

/// <summary>What the video import asks of FFmpeg: the audio as samples, and a video Windows can show.</summary>
public static class FfmpegMedia
{
    /// <summary>
    /// Decodes the audio of <paramref name="path"/> (from <paramref name="start"/>, for <paramref name="length"/>
    /// when given) into interleaved float samples at <paramref name="sampleRate"/> with <paramref name="channels"/>
    /// channels (more are mixed down by FFmpeg). <paramref name="progress"/> gets 0..1.
    /// </summary>
    /// <exception cref="InvalidDataException">FFmpeg failed.</exception>
    public static async Task<float[]> ReadAudioAsync(VideoTools tools, string path, TimeSpan? start, TimeSpan? length,
        int sampleRate, int channels, TimeSpan expected, IProgress<double>? progress, CancellationToken cancel)
    {
        var arguments = new List<string> { "-v", "error", "-nostdin", "-nostats", "-progress", "pipe:2" };
        if (start is { } from && from > TimeSpan.Zero)
            arguments.AddRange(["-ss", Seconds(from)]);
        arguments.AddRange(["-i", path]);
        if (length is { } span)
            arguments.AddRange(["-t", Seconds(span)]);
        arguments.AddRange(["-vn", "-sn", "-dn", "-ac", channels.ToString(CultureInfo.InvariantCulture),
            "-ar", sampleRate.ToString(CultureInfo.InvariantCulture), "-f", "f32le", "pipe:1"]);

        var total = length ?? expected;
        var samples = new SampleBuffer((long)(total.TotalSeconds * sampleRate * channels) + sampleRate * channels);
        var result = await ToolProcess.RunBinaryAsync(tools.FfmpegPath, arguments,
            (output, token) => samples.FillAsync(output, token),
            line => ReportTime(line, total, progress),
            cancel);
        if (!result.Succeeded)
            throw new InvalidDataException(ErrorOf(result));
        var all = samples.ToArray();
        return all.Length % channels == 0 ? all : all[..(all.Length - all.Length % channels)];
    }

    /// <summary>
    /// Writes a small H.264 copy of the video, without sound, for the preview: up to 480 lines high, with
    /// a key frame every second so jumping around is quick. <paramref name="progress"/> gets 0..1.
    /// </summary>
    /// <exception cref="InvalidDataException">FFmpeg failed.</exception>
    public static async Task MakePreviewVideoAsync(VideoTools tools, string path, string output, TimeSpan duration,
        IProgress<double>? progress, CancellationToken cancel)
    {
        var result = await ToolProcess.RunAsync(tools.FfmpegPath,
        [
            "-v", "error", "-nostdin", "-nostats", "-progress", "pipe:2", "-y",
            "-i", path, "-an", "-sn", "-dn",
            "-vf", "scale=-2:'min(480,ih)',format=yuv420p",
            "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-g", "30", "-keyint_min", "30",
            "-movflags", "+faststart", output,
        ], onError: line => ReportTime(line, duration, progress), cancel: cancel);
        if (!result.Succeeded)
            throw new InvalidDataException(ErrorOf(result));
    }

    /// <summary>The seconds of an FFmpeg "-progress" line ("out_time_us=12345678"), or null.</summary>
    internal static double? ProgressSeconds(string line)
    {
        const string key = "out_time_us=";
        return line.StartsWith(key, StringComparison.Ordinal) &&
            long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long micro) && micro >= 0
            ? micro / 1_000_000.0
            : null;
    }

    private static void ReportTime(string line, TimeSpan total, IProgress<double>? progress)
    {
        if (progress is not null && total > TimeSpan.Zero && ProgressSeconds(line) is { } seconds)
            progress.Report(Math.Clamp(seconds / total.TotalSeconds, 0, 1));
    }

    private static string ErrorOf(ToolResult result)
    {
        // The -progress lines ("key=value") are mixed with the errors.
        var error = result.Errors.LastOrDefault(l => !IsProgressLine(l));
        return error is null ? $"FFmpeg si è fermato (codice {result.ExitCode})." : $"FFmpeg: {error}";
    }

    private static bool IsProgressLine(string line)
    {
        int equals = line.IndexOf('=');
        return equals > 0 && !line.AsSpan(0, equals).Contains(' ');
    }

    private static string Seconds(TimeSpan time) => time.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);

    // Grows by halves rather than doubling: an hour of audio is already hundreds of MB.
    private sealed class SampleBuffer(long expected)
    {
        private float[] _samples = new float[Math.Clamp(expected, 1 << 16, Array.MaxLength)];
        private long _count;

        public async Task FillAsync(Stream input, CancellationToken cancel)
        {
            var bytes = new byte[1 << 16];
            int pending = 0;
            int read;
            while ((read = await input.ReadAsync(bytes.AsMemory(pending), cancel)) > 0)
            {
                int available = pending + read;
                int whole = available - available % sizeof(float);
                Append(MemoryMarshal.Cast<byte, float>(bytes.AsSpan(0, whole)));
                // A float split between two reads waits for its other bytes.
                pending = available - whole;
                if (pending > 0)
                    bytes.AsSpan(whole, pending).CopyTo(bytes);
            }
        }

        private void Append(ReadOnlySpan<float> values)
        {
            if (_count + values.Length > _samples.Length)
            {
                long size = Math.Max(_count + values.Length, _samples.Length + _samples.Length / 2);
                if (size > Array.MaxLength)
                    throw new InvalidDataException("L'audio è troppo lungo per essere caricato.");
                Array.Resize(ref _samples, (int)size);
            }
            values.CopyTo(_samples.AsSpan((int)_count));
            _count += values.Length;
        }

        public float[] ToArray() => _count == _samples.Length ? _samples : _samples[..(int)_count];
    }
}
