using System.Runtime.InteropServices;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundBoardKing.Audio.Editing;

/// <summary>
/// A whole sound decoded in memory, as interleaved float samples, for the editor. Never changed
/// once built: every edit returns a new clip, so the old one is the undo step.
/// </summary>
public sealed class AudioClip
{
    /// <summary>Longest sound the editor opens: an hour of film audio would not fit in memory with its undo steps.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(15);

    public AudioClip(float[] samples, int sampleRate, int channels)
    {
        if (channels is < 1 or > 2)
            throw new ArgumentOutOfRangeException(nameof(channels));
        if (samples.Length % channels != 0)
            throw new ArgumentException("Samples must hold whole frames.", nameof(samples));
        Samples = samples;
        SampleRate = sampleRate;
        Channels = channels;
    }

    /// <summary>Interleaved: left, right, left, right… for stereo. Not to be written to.</summary>
    public float[] Samples { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public int Frames => Samples.Length / Channels;
    public TimeSpan Duration => FramesToTime(Frames);

    public WaveFormat WaveFormat => WaveFormat.CreateIeeeFloatWaveFormat(SampleRate, Channels);

    public TimeSpan FramesToTime(long frames) => TimeSpan.FromSeconds((double)frames / SampleRate);

    public int TimeToFrames(TimeSpan time) => (int)Math.Clamp(Math.Round(time.TotalSeconds * SampleRate), 0, Frames);

    public static AudioClip Silence(int frames, int sampleRate, int channels) =>
        new(new float[frames * channels], sampleRate, channels);

    /// <summary>Decodes a file; more than two channels (a film's 5.1) are folded down to stereo.</summary>
    /// <exception cref="InvalidDataException">The sound is longer than <see cref="MaxDuration"/>.</exception>
    /// <exception cref="COMException">Windows cannot decode the file.</exception>
    public static AudioClip Load(string path)
    {
        using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
        });
        if (reader.TotalTime > MaxDuration)
            throw new InvalidDataException($"Il suono dura più di {MaxDuration.TotalMinutes:0} minuti: l'editor è fatto per suoni più brevi.");
        return FromProvider(reader.ToSampleProvider());
    }

    /// <summary>Reads <paramref name="source"/> to the end.</summary>
    public static AudioClip FromProvider(ISampleProvider source)
    {
        var format = source.WaveFormat;
        long maxSamples = (long)(MaxDuration.TotalSeconds * format.SampleRate) * format.Channels;
        var samples = new List<float>(format.SampleRate * format.Channels * 10);
        var buffer = new float[Math.Max(1, format.SampleRate / 10) * format.Channels];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read - read % format.Channels));
            if (samples.Count > maxSamples)
                throw new InvalidDataException($"Il suono dura più di {MaxDuration.TotalMinutes:0} minuti: l'editor è fatto per suoni più brevi.");
        }
        var all = samples.ToArray();
        return format.Channels <= 2 ? new AudioClip(all, format.SampleRate, format.Channels) : FoldToStereo(all, format);
    }

    /// <summary>A copy in another rate and channel count, for pasting a sound into one that differs.</summary>
    public AudioClip ConvertTo(int sampleRate, int channels)
    {
        if (sampleRate == SampleRate && channels == Channels)
            return this;
        ISampleProvider source = ToSampleProvider();
        if (Channels == 1 && channels == 2)
            source = new MonoToStereoSampleProvider(source);
        else if (Channels == 2 && channels == 1)
            source = new StereoToMonoSampleProvider(source) { LeftVolume = 0.5f, RightVolume = 0.5f };
        if (sampleRate != SampleRate)
            source = new WdlResamplingSampleProvider(source, sampleRate);
        var samples = new List<float>((int)((long)Frames * sampleRate / SampleRate + 16) * channels);
        var buffer = new float[8192 * channels];
        int read;
        while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            samples.AddRange(buffer.AsSpan(0, read - read % channels));
        return new AudioClip([.. samples], sampleRate, channels);
    }

    /// <summary>Plays the frames from <paramref name="start"/> to <paramref name="end"/>.</summary>
    public ISampleProvider ToSampleProvider(int start = 0, int end = -1) =>
        new ClipReader(this, start, end < 0 ? Frames : end);

    // The usual WAVEFORMATEXTENSIBLE order: front left/right, centre, LFE, then the surrounds.
    private static AudioClip FoldToStereo(float[] samples, WaveFormat format)
    {
        int channels = format.Channels;
        int frames = samples.Length / channels;
        var stereo = new float[frames * 2];
        const float side = 0.7071f;
        for (int f = 0; f < frames; f++)
        {
            int i = f * channels;
            float left = samples[i], right = samples[i + 1];
            if (channels >= 3)
            {
                left += side * samples[i + 2];
                right += side * samples[i + 2];
            }
            for (int c = 4; c < channels; c++)
            {
                // Surrounds alternate left and right.
                if ((c - 4) % 2 == 0)
                    left += side * samples[i + c];
                else
                    right += side * samples[i + c];
            }
            stereo[f * 2] = left * 0.6f;
            stereo[f * 2 + 1] = right * 0.6f;
        }
        return new AudioClip(stereo, format.SampleRate, 2);
    }

    private sealed class ClipReader(AudioClip clip, int start, int end) : ISampleProvider
    {
        private int _position = start * clip.Channels;
        private readonly int _end = end * clip.Channels;

        public WaveFormat WaveFormat { get; } = clip.WaveFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, _end - _position);
            if (n <= 0)
                return 0;
            Array.Copy(clip.Samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
