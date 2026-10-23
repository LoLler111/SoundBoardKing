using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using OggVorbisEncoder;

namespace SoundBoardKing.Audio.Editing;

public enum ExportFormat
{
    Mp3,
    M4a,
    Wav,
    Flac,
    Ogg,
    Wma,
}

/// <summary>A format the editor saves to, as offered in its list.</summary>
public sealed record ExportFormatInfo(ExportFormat Format, string Extension, string Label, bool IsLossless);

/// <summary>Writes an <see cref="AudioClip"/> to a file. Media Foundation encodes everything but Wav and Ogg.</summary>
public static class AudioExport
{
    public static readonly IReadOnlyList<ExportFormatInfo> Formats =
    [
        new(ExportFormat.Mp3, ".mp3", "MP3", false),
        new(ExportFormat.M4a, ".m4a", "M4A (AAC)", false),
        new(ExportFormat.Wav, ".wav", "WAV", true),
        new(ExportFormat.Flac, ".flac", "FLAC", true),
        new(ExportFormat.Ogg, ".ogg", "OGG (Vorbis)", false),
        new(ExportFormat.Wma, ".wma", "WMA", false),
    ];

    /// <summary>The bitrates offered for the lossy formats, in kbit/s.</summary>
    public static readonly IReadOnlyList<int> Bitrates = [96, 128, 192, 256, 320];

    public const int DefaultBitrate = 192;

    public static ExportFormatInfo InfoOf(ExportFormat format) => Formats.First(f => f.Format == format);

    private static readonly Lazy<bool> OggPlayable = new(ProbeOggPlayback);

    /// <summary>
    /// The formats worth offering: Ogg only where Windows can also play it back (it needs the "Web Media
    /// Extensions"), or the saved sound would not play in the app. The first call may take a moment.
    /// </summary>
    public static IReadOnlyList<ExportFormatInfo> AvailableFormats() =>
        OggPlayable.Value ? Formats : Formats.Where(f => f.Format != ExportFormat.Ogg).ToList();

    private static bool ProbeOggPlayback()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sbk-probe-{Guid.NewGuid():N}.ogg");
        try
        {
            WriteOgg(AudioClip.Silence(4410, 44100, 2), path, DefaultBitrate);
            using var reader = new MediaFoundationReader(path);
            return reader.TotalTime > TimeSpan.Zero;
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>The format a file is in, judging by its extension; null when the editor cannot write it.</summary>
    public static ExportFormat? FormatOf(string path)
    {
        var extension = Path.GetExtension(path);
        return Formats.FirstOrDefault(f => string.Equals(f.Extension, extension, StringComparison.OrdinalIgnoreCase))?.Format;
    }

    /// <summary>
    /// Encodes <paramref name="clip"/> to <paramref name="path"/>, replacing a file already there only once
    /// the new one is complete. <paramref name="bitrate"/> (kbit/s) is ignored by the lossless formats.
    /// </summary>
    /// <exception cref="COMException">Windows has no encoder for the format.</exception>
    /// <exception cref="IOException">The file cannot be written.</exception>
    public static void Save(AudioClip clip, string path, ExportFormat format, int bitrate = DefaultBitrate)
    {
        // The encoder picks the container from the extension, so the temporary file keeps it.
        var folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var temporary = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{InfoOf(format).Extension}");
        try
        {
            switch (format)
            {
                case ExportFormat.Wav:
                    WaveFileWriter.CreateWaveFile(temporary, new SampleToWaveProvider16(clip.ToSampleProvider()));
                    break;
                case ExportFormat.Ogg:
                    WriteOgg(clip, temporary, bitrate);
                    break;
                default:
                    WriteWithMediaFoundation(clip, temporary, format, bitrate);
                    break;
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static void WriteWithMediaFoundation(AudioClip clip, string path, ExportFormat format, int bitrate)
    {
        var subtype = format switch
        {
            ExportFormat.Mp3 => AudioSubtypes.MFAudioFormat_MP3,
            ExportFormat.M4a => AudioSubtypes.MFAudioFormat_AAC,
            ExportFormat.Flac => AudioSubtypes.MFAudioFormat_FLAC,
            ExportFormat.Wma => AudioSubtypes.MFAudioFormat_WMAudioV8,
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        // The encoders take 16-bit PCM at the usual rates only: anything else is resampled first.
        int rate = format switch
        {
            ExportFormat.Flac => clip.SampleRate,
            _ => clip.SampleRate is 44100 or 48000 ? clip.SampleRate : clip.SampleRate > 44100 ? 48000 : 44100,
        };
        ISampleProvider source = clip.ToSampleProvider();
        if (rate != clip.SampleRate)
            source = new WdlResamplingSampleProvider(source, rate);

        MediaFoundationApi.Startup();
        var mediaType = MediaFoundationEncoder.GetOutputMediaTypes(subtype)
            .Where(t => t.SampleRate == rate && t.ChannelCount == clip.Channels && (format != ExportFormat.Flac || t.BitsPerSample is 16 or 0))
            .OrderBy(t => Math.Abs(t.AverageBytesPerSecond * 8 - bitrate * 1000))
            .FirstOrDefault()
            ?? throw new COMException($"Windows non sa salvare in {InfoOf(format).Label} a {rate} Hz.");
        using var encoder = new MediaFoundationEncoder(mediaType);
        encoder.Encode(path, new SampleToWaveProvider16(source));
    }

    private const int OggTailFrames = 2048;

    private static void WriteOgg(AudioClip clip, string path, int bitrate)
    {
        // Variable bitrate: the Vorbis quality that gives about the chosen kbit/s in stereo.
        float quality = bitrate switch
        {
            <= 96 => 0.2f,
            <= 128 => 0.4f,
            <= 192 => 0.6f,
            <= 256 => 0.8f,
            _ => 0.9f,
        };
        var info = VorbisInfo.InitVariableBitRate(clip.Channels, clip.SampleRate, quality);
        var ogg = new OggStream(Random.Shared.Next());
        ogg.PacketIn(HeaderPacketBuilder.BuildInfoPacket(info));
        ogg.PacketIn(HeaderPacketBuilder.BuildCommentsPacket(new Comments()));
        ogg.PacketIn(HeaderPacketBuilder.BuildBooksPacket(info));

        using var file = File.Create(path);
        void Flush(bool force)
        {
            while (ogg.PageOut(out var page, force))
            {
                file.Write(page.Header, 0, page.Header.Length);
                file.Write(page.Body, 0, page.Body.Length);
            }
        }
        Flush(force: true);

        var state = ProcessingState.Create(info);
        const int block = 4096;
        var channels = new float[clip.Channels][];
        for (int c = 0; c < clip.Channels; c++)
            channels[c] = new float[block];
        // The encoder drops the last few milliseconds it is given: a little silence goes after the sound.
        int padded = clip.Frames + OggTailFrames;
        for (int start = 0; start < padded; start += block)
        {
            int frames = Math.Min(block, padded - start);
            for (int f = 0; f < frames; f++)
            {
                int frame = start + f;
                for (int c = 0; c < clip.Channels; c++)
                    channels[c][f] = frame < clip.Frames ? clip.Samples[frame * clip.Channels + c] : 0;
            }
            state.WriteData(channels, frames);
            Drain(state, ogg, Flush);
        }
        state.WriteEndOfStream();
        Drain(state, ogg, Flush);
        Flush(force: true);
    }

    private static void Drain(ProcessingState state, OggStream ogg, Action<bool> flush)
    {
        while (!ogg.Finished && state.PacketOut(out var packet))
        {
            ogg.PacketIn(packet);
            flush(false);
        }
    }
}
