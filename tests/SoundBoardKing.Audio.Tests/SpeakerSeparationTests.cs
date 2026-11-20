using NAudio.Wave;
using SoundBoardKing.Audio.Editing;
using SoundBoardKing.Audio.Speech;
using Xunit.Abstractions;

namespace SoundBoardKing.Audio.Tests;

/// <summary>
/// "Separa voci" (TIGER-speech). The model ships with the app, so the first tests always run; the ones
/// against PyTorch and on real voices need SBK_SEPARATION_DIR: house16k.wav with house_ref_a/b.npy (PyTorch on
/// the same samples zero-padded to one chunk), and the X_mix.wav / X_ref_*.wav pairs of two TTS voices.
/// </summary>
public class SpeakerSeparationTests(ITestOutputHelper output)
{
    private static readonly string? Root = Environment.GetEnvironmentVariable("SBK_SEPARATION_DIR");

    [Fact]
    public void The_two_voices_add_up_to_the_original()
    {
        // 9 s of stereo at 44.1 kHz: three chunks, so the cross-fades and the mask on the original are both crossed.
        const int rate = 44100;
        var random = new Random(7);
        int frames = rate * 9;
        var samples = new float[frames * 2];
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / rate;
            double a = Math.Sin(2 * Math.PI * 140 * t) * (Math.Sin(2 * Math.PI * 1.3 * t) > 0 ? 0.3 : 0);
            double b = Math.Sin(2 * Math.PI * 230 * t + 1) * (Math.Sin(2 * Math.PI * 0.7 * t) > 0 ? 0.25 : 0);
            double noise = (random.NextDouble() - 0.5) * 0.02;
            samples[i * 2] = (float)(a + 0.6 * b + noise);
            samples[i * 2 + 1] = (float)(0.6 * a + b + noise);
        }
        var clip = new AudioClip(samples, rate, 2);

        var voices = SpeakerSeparator.Separate(clip, null, CancellationToken.None);

        Assert.Equal(clip.Frames, voices.Main.Frames);
        Assert.Equal(clip.Frames, voices.Other.Frames);
        Assert.Equal(2, voices.Main.Channels);
        double error = 0, energy = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            double d = voices.Main.Samples[i] + voices.Other.Samples[i] - samples[i];
            error += d * d;
            energy += samples[i] * samples[i];
        }
        Assert.True(error / energy < 1e-6, $"residuo {error / energy}");
    }

    [Fact]
    public void A_short_or_empty_sound_comes_back_the_same_length()
    {
        var empty = SpeakerSeparator.Demix([], null, CancellationToken.None);
        Assert.Empty(empty.A);
        var shortClip = new AudioClip(Enumerable.Range(0, 800).Select(i => (float)Math.Sin(i * 0.2) * 0.2f).ToArray(), 8000, 1);
        var voices = SpeakerSeparator.Separate(shortClip, null, CancellationToken.None);
        Assert.Equal(800, voices.Main.Frames);
        Assert.Equal(8000, voices.Other.SampleRate);
    }

    [Fact]
    public void Chunks_are_matched_to_the_voice_they_continue()
    {
        int offset = SpeakerSeparator.ChunkSize - SpeakerSeparator.OverlapSize;
        var previousA = new float[SpeakerSeparator.ChunkSize];
        var previousB = new float[SpeakerSeparator.ChunkSize];
        var a = new float[SpeakerSeparator.ChunkSize];
        var b = new float[SpeakerSeparator.ChunkSize];
        for (int i = 0; i < SpeakerSeparator.OverlapSize; i++)
        {
            previousA[offset + i] = MathF.Sin(i * 0.05f);
            previousB[offset + i] = MathF.Sin(i * 0.31f);
            a[i] = previousB[offset + i];
            b[i] = previousA[offset + i];
        }
        Assert.True(SpeakerSeparator.ShouldSwap(previousA, previousB, a, b, offset));
        Assert.False(SpeakerSeparator.ShouldSwap(previousA, previousB, b, a, offset));
    }

    [Fact]
    public void One_chunk_matches_pytorch()
    {
        if (Root is null)
            return;
        var input = ReadWav(Path.Combine(Root, "house16k.wav"));
        var refA = KokoroModel.ReadNpyFloats(File.ReadAllBytes(Path.Combine(Root, "house_ref_a.npy")));
        var refB = KokoroModel.ReadNpyFloats(File.ReadAllBytes(Path.Combine(Root, "house_ref_b.npy")));

        var (a, b) = SpeakerSeparator.Demix(input, null, CancellationToken.None);

        if (Correlation(a, refA) < Correlation(a, refB))
            (a, b) = (b, a);
        double peak = refA.Concat(refB).Max(Math.Abs);
        double maxDiff = Enumerable.Range(0, a.Length).Max(i => Math.Max(Math.Abs(a[i] - refA[i]), Math.Abs(b[i] - refB[i])));
        output.WriteLine($"correlazione {Correlation(a, refA):0.00000} / {Correlation(b, refB):0.00000}, scarto massimo {maxDiff:0.00000} su picco {peak:0.000}");
        Assert.True(maxDiff < 0.01 * peak);
    }

    [Theory]
    [InlineData("A_donna_uomo", 9)]
    [InlineData("B_donna_donna", 7)]
    public void Two_voices_talking_together_come_apart(string name, double minimumGainDb)
    {
        if (Root is null)
            return;
        var mix = ReadWav(Path.Combine(Root, name + "_mix.wav"));
        var refs = Directory.GetFiles(Root, name + "_ref_*.wav").Order().Select(ReadWav).ToArray();

        // At 16 kHz (the model's own output) and on 48 kHz stereo (laid back on the original), each measured
        // against references at its own rate, resampled the way the mix was.
        var direct = SpeakerSeparator.Separate(new AudioClip(mix, 16000, 1), null, CancellationToken.None);
        var wide = SpeakerSeparator.Separate(new AudioClip(mix, 16000, 1).ConvertTo(48000, 2), null, CancellationToken.None);
        var cases = new[]
        {
            ("16 kHz", mix, refs, direct.Main.Samples, direct.Other.Samples),
            ("48 kHz", Up(mix), refs.Select(Up).ToArray(), wide.Main.ConvertTo(48000, 1).Samples, wide.Other.ConvertTo(48000, 1).Samples),
        };

        foreach (var (label, mixture, references, main, other) in cases)
        {
            mix = mixture;
            refs = references;
            double gain = 0;
            var lines = new List<string>();
            var pairs = SiSdr(main, refs[0]) + SiSdr(other, refs[1]) >= SiSdr(main, refs[1]) + SiSdr(other, refs[0])
                ? new[] { (main, refs[0]), (other, refs[1]) }
                : new[] { (main, refs[1]), (other, refs[0]) };
            foreach (var (estimate, reference) in pairs)
            {
                double before = SiSdr(mix, reference), after = SiSdr(estimate, reference);
                gain += (after - before) / 2;
                lines.Add($"{before:0.0} -> {after:0.0} dB");
            }
            output.WriteLine($"{name} {label}: {string.Join(", ", lines)}, guadagno medio {gain:0.0} dB");
            Assert.True(gain > minimumGainDb, $"{label}: {gain:0.0} dB");
        }
    }

    private static float[] ReadWav(string path)
    {
        using var reader = new WaveFileReader(path);
        var clip = AudioClip.FromProvider(reader.ToSampleProvider());
        return clip.ConvertTo(clip.SampleRate, 1).Samples;
    }

    private static float[] Up(float[] samples16k) => new AudioClip(samples16k, 16000, 1).ConvertTo(48000, 1).Samples;

    private static double Correlation(float[] x, float[] y)
    {
        double xy = 0, xx = 0, yy = 0;
        for (int i = 0; i < Math.Min(x.Length, y.Length); i++)
        {
            xy += x[i] * y[i];
            xx += x[i] * x[i];
            yy += y[i] * y[i];
        }
        return xy / Math.Sqrt(xx * yy + 1e-20);
    }

    // Scale-invariant signal-to-distortion ratio, as in the separation papers.
    private static double SiSdr(float[] estimate, float[] reference)
    {
        int n = Math.Min(estimate.Length, reference.Length);
        double meanE = 0, meanR = 0;
        for (int i = 0; i < n; i++)
        {
            meanE += estimate[i];
            meanR += reference[i];
        }
        meanE /= n;
        meanR /= n;
        double dot = 0, rr = 0;
        for (int i = 0; i < n; i++)
        {
            dot += (estimate[i] - meanE) * (reference[i] - meanR);
            rr += (reference[i] - meanR) * (reference[i] - meanR);
        }
        double scale = dot / (rr + 1e-12), target = 0, noise = 0;
        for (int i = 0; i < n; i++)
        {
            double t = scale * (reference[i] - meanR);
            double e = estimate[i] - meanE - t;
            target += t * t;
            noise += e * e;
        }
        return 10 * Math.Log10(target / (noise + 1e-12));
    }
}
