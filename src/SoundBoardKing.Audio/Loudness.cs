using System.Runtime.InteropServices;
using NAudio.Wave;

namespace SoundBoardKing.Audio;

/// <summary>How loud a sound is: integrated loudness in LUFS and sample peak in dBFS.</summary>
public readonly record struct LoudnessInfo(float Lufs, float PeakDb);

/// <summary>
/// Loudness measured as in ITU-R BS.1770 / EBU R128 (K-weighting, 400 ms blocks, absolute and
/// relative gates), and the gain that brings a sound to <see cref="TargetLufs"/>.
/// </summary>
public static class Loudness
{
    /// <summary>Where every sound is brought to when leveling is on.</summary>
    public const float TargetLufs = -16f;

    /// <summary>Floor for silent files, and for peaks of digital silence.</summary>
    public const float SilenceLufs = -70f;

    // A very quiet sound is raised at most this much: beyond it there is mostly noise to raise.
    private const float MaxBoostDb = 12f;

    // A raised sound may peak this far over full scale: the limiter on the mix catches the rest.
    private const float PeakAllowanceDb = 4f;

    // Long files (whole songs) are judged by their first minutes, so the analysis stays quick.
    private static readonly TimeSpan MaxAnalysed = TimeSpan.FromMinutes(10);

    /// <summary>Gain, linear, that levels a sound; 1 while it has not been measured yet.</summary>
    public static float GainFor(float? lufs, float? peakDb)
    {
        if (lufs is not float measured || measured <= SilenceLufs + 1)
            return 1f;
        float db = Math.Min(TargetLufs - measured, MaxBoostDb);
        if (db > 0 && peakDb is float peak)
            db = Math.Min(db, Math.Max(0, PeakAllowanceDb - peak));
        return MathF.Pow(10, db / 20);
    }

    /// <summary>Decodes the whole file; null when it cannot be read.</summary>
    public static LoudnessInfo? TryMeasure(string path)
    {
        try
        {
            using var reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
            {
                RequestFloatOutput = true,
            });
            return Measure(reader.ToSampleProvider());
        }
        catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    public static LoudnessInfo Measure(ISampleProvider source)
    {
        int channels = source.WaveFormat.Channels;
        int rate = source.WaveFormat.SampleRate;
        // A mono file is played on both speakers, so it is as loud as the same signal in stereo.
        double weight = channels == 1 ? 2 : 1;
        var filters = Enumerable.Range(0, channels).Select(_ => new KWeighting(rate)).ToArray();

        int stepFrames = Math.Max(1, rate / 10); // 100 ms: blocks of 400 ms overlap by 75%
        long maxFrames = (long)(MaxAnalysed.TotalSeconds * rate);
        var steps = new List<double>();
        double energy = 0;
        int stepFill = 0;
        long frames = 0;
        float peak = 0;

        var buffer = new float[stepFrames * channels];
        int read;
        while (frames < maxFrames && (read = source.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i + channels <= read; i += channels)
            {
                double sum = 0;
                for (int ch = 0; ch < channels; ch++)
                {
                    float x = buffer[i + ch];
                    peak = Math.Max(peak, Math.Abs(x));
                    double y = filters[ch].Process(x);
                    sum += y * y;
                }
                energy += sum * weight;
                frames++;
                if (++stepFill == stepFrames)
                {
                    steps.Add(energy);
                    energy = 0;
                    stepFill = 0;
                }
            }
        }

        var blocks = new List<double>();
        for (int i = 0; i + 4 <= steps.Count; i++)
            blocks.Add((steps[i] + steps[i + 1] + steps[i + 2] + steps[i + 3]) / (4.0 * stepFrames));
        if (blocks.Count == 0 && frames > 0) // shorter than one block: the whole sound is the block
            blocks.Add((steps.Sum() + energy) / frames);

        return new LoudnessInfo(Integrated(blocks), peak > 0 ? Math.Max(SilenceLufs, 20 * MathF.Log10(peak)) : SilenceLufs);
    }

    private static float Integrated(List<double> blocks)
    {
        var audible = blocks.Where(z => ToLufs(z) > SilenceLufs).ToList();
        if (audible.Count == 0)
            return SilenceLufs;
        double relativeGate = ToLufs(audible.Average()) - 10;
        var gated = audible.Where(z => ToLufs(z) > relativeGate).ToList();
        double lufs = gated.Count == 0 ? SilenceLufs : ToLufs(gated.Average());
        return (float)Math.Max(SilenceLufs, lufs);
    }

    private static double ToLufs(double meanSquare) =>
        meanSquare > 0 ? -0.691 + 10 * Math.Log10(meanSquare) : double.NegativeInfinity;

    /// <summary>The two BS.1770 filters (head shelf and high-pass), derived for any sample rate.</summary>
    private sealed class KWeighting
    {
        private readonly Biquad _shelf;
        private readonly Biquad _highPass;

        public KWeighting(int rate)
        {
            double k = Math.Tan(Math.PI * 1681.974450955533 / rate);
            double q = 0.7071752369554196;
            double vh = Math.Pow(10, 3.999843853973347 / 20);
            double vb = Math.Pow(vh, 0.4996667741545416);
            double a0 = 1 + k / q + k * k;
            _shelf = new Biquad(
                (vh + vb * k / q + k * k) / a0, 2 * (k * k - vh) / a0, (vh - vb * k / q + k * k) / a0,
                2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);

            k = Math.Tan(Math.PI * 38.13547087602444 / rate);
            q = 0.5003270373238773;
            a0 = 1 + k / q + k * k;
            _highPass = new Biquad(1, -2, 1, 2 * (k * k - 1) / a0, (1 - k / q + k * k) / a0);
        }

        public double Process(double x) => _highPass.Process(_shelf.Process(x));
    }

    private sealed class Biquad(double b0, double b1, double b2, double a1, double a2)
    {
        private double _z1;
        private double _z2;

        public double Process(double x)
        {
            double y = b0 * x + _z1;
            _z1 = b1 * x - a1 * y + _z2;
            _z2 = b2 * x - a2 * y;
            return y;
        }
    }
}
