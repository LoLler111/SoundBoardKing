using SoundBoardKing.Audio.Editing;

namespace SoundBoardKing.Audio.Tests;

public class SoundEditingTests
{
    private const int Rate = 48000;

    // A second of a 440 Hz tone in stereo, the right channel quieter so the two can be told apart.
    private static AudioClip Tone(double seconds = 1)
    {
        int frames = (int)(seconds * Rate);
        var samples = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float s = (float)Math.Sin(2 * Math.PI * 440 * f / Rate);
            samples[f * 2] = 0.5f * s;
            samples[f * 2 + 1] = 0.25f * s;
        }
        return new AudioClip(samples, Rate, 2);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(12)]
    [InlineData(7680)]
    public void The_fft_matches_the_plain_dft_at_any_length(int n)
    {
        var random = new Random(n);
        var re = Enumerable.Range(0, n).Select(_ => random.NextDouble() - 0.5).ToArray();
        var im = Enumerable.Range(0, n).Select(_ => random.NextDouble() - 0.5).ToArray();
        var fftRe = (double[])re.Clone();
        var fftIm = (double[])im.Clone();
        new Fft(n).Transform(fftRe, fftIm, inverse: false);

        foreach (int k in new[] { 0, 1, n / 3, n - 1 })
        {
            double sumRe = 0, sumIm = 0;
            for (int j = 0; j < n; j++)
            {
                double angle = -2 * Math.PI * ((long)j * k % n) / n;
                sumRe += re[j] * Math.Cos(angle) - im[j] * Math.Sin(angle);
                sumIm += re[j] * Math.Sin(angle) + im[j] * Math.Cos(angle);
            }
            Assert.Equal(sumRe, fftRe[k], 6);
            Assert.Equal(sumIm, fftIm[k], 6);
        }

        new Fft(n).Transform(fftRe, fftIm, inverse: true);
        for (int j = 0; j < n; j++)
            Assert.Equal(re[j], fftRe[j] / n, 9);
    }

    [Fact]
    public void Analysis_then_synthesis_gives_the_signal_back()
    {
        var clip = Tone(0.3);
        var left = Enumerable.Range(0, clip.Frames).Select(f => clip.Samples[f * 2]).ToArray();
        var right = Enumerable.Range(0, clip.Frames).Select(f => (float)Math.Cos(f * 0.01) * 0.3f).ToArray();
        var stft = new StereoStft(7680, 1024);
        int frames = stft.FrameCount(left.Length);
        var synthesis = stft.StartSynthesis(frames);
        double[] lr = new double[stft.Bins], li = new double[stft.Bins], rr = new double[stft.Bins], ri = new double[stft.Bins];
        for (int t = 0; t < frames; t++)
        {
            stft.Analyze(left, right, left.Length, t, lr, li, rr, ri);
            synthesis.Add(t, lr, li, rr, ri);
        }
        var (outLeft, outRight) = synthesis.Finish(left.Length);

        for (int i = 0; i < left.Length; i++)
        {
            Assert.Equal(left[i], outLeft[i], 1e-4f);
            Assert.Equal(right[i], outRight[i], 1e-4f);
        }
    }

    [Fact]
    public void Keep_delete_and_insert_change_the_length_as_expected()
    {
        var clip = Tone();
        var kept = ClipEdits.Keep(clip, 12000, 36000);
        Assert.Equal(24000, kept.Frames);
        Assert.Equal(clip.Samples[12000 * 2], kept.Samples[0]);

        var deleted = ClipEdits.Delete(clip, 12000, 36000);
        Assert.Equal(clip.Frames - 24000, deleted.Frames);

        var inserted = ClipEdits.Insert(clip, 1000, AudioClip.Silence(500, Rate, 2));
        Assert.Equal(clip.Frames + 500, inserted.Frames);
        Assert.Equal(0f, inserted.Samples[1200 * 2]);
        // The original is never touched.
        Assert.Equal(48000, clip.Frames);
    }

    [Fact]
    public void A_pasted_sound_in_another_format_is_converted()
    {
        var clip = Tone();
        var mono = new AudioClip(new float[22050], 22050, 1); // one second, mono, other rate
        var result = ClipEdits.Insert(clip, 0, mono);
        Assert.Equal(2, result.Channels);
        Assert.InRange(result.Frames - clip.Frames, Rate - 100, Rate + 100);
    }

    [Fact]
    public void Fades_start_and_end_in_silence()
    {
        var clip = Tone();
        var fadedIn = ClipEdits.Fade(clip, 0, 24000, FadeDirection.In, FadeCurve.Smooth);
        Assert.Equal(0f, fadedIn.Samples[0]);
        Assert.Equal(clip.Samples[30000 * 2], fadedIn.Samples[30000 * 2]);

        var fadedOut = ClipEdits.Fade(clip, 24000, clip.Frames, FadeDirection.Out, FadeCurve.Exponential);
        Assert.Equal(0f, fadedOut.Samples[^1], 6);
        Assert.Equal(clip.Samples[100], fadedOut.Samples[100]);
    }

    [Theory]
    [InlineData(FadeCurve.Linear)]
    [InlineData(FadeCurve.Smooth)]
    [InlineData(FadeCurve.Exponential)]
    [InlineData(FadeCurve.Logarithmic)]
    public void Every_curve_rises_from_zero_to_one(FadeCurve curve)
    {
        Assert.Equal(0, ClipEdits.Gain(0, curve), 9);
        Assert.Equal(1, ClipEdits.Gain(1, curve), 9);
        for (double t = 0; t < 1; t += 0.05)
            Assert.True(ClipEdits.Gain(t + 0.05, curve) >= ClipEdits.Gain(t, curve));
    }

    [Fact]
    public void Normalize_brings_the_peak_to_minus_one_db()
    {
        var clip = Tone();
        var normalized = ClipEdits.Normalize(clip, 0, clip.Frames);
        Assert.Equal(-1, ClipEdits.PeakDb(normalized, 0, normalized.Frames)!.Value, 2);
        Assert.Null(ClipEdits.PeakDb(AudioClip.Silence(100, Rate, 2), 0, 100));
    }

    [Fact]
    public void Amplify_changes_only_the_range()
    {
        var clip = Tone();
        var louder = ClipEdits.Amplify(clip, 24000, 48000, 6);
        Assert.Equal(clip.Samples[1000], louder.Samples[1000]);
        int f = 36000 * 2;
        Assert.Equal(clip.Samples[f] * Math.Pow(10, 6 / 20.0), louder.Samples[f], 4);
    }

    [Fact]
    public void Five_one_is_folded_to_stereo()
    {
        var format = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(Rate, 6);
        var samples = new float[600];
        for (int f = 0; f < 100; f++)
            samples[f * 6 + 2] = 0.5f; // centre only, as film dialogue
        var clip = AudioClip.FromProvider(new ArraySource(samples, format));
        Assert.Equal(2, clip.Channels);
        Assert.True(clip.Samples[0] > 0.1f && Math.Abs(clip.Samples[0] - clip.Samples[1]) < 1e-6f);
    }

    [Theory]
    [InlineData(ExportFormat.Wav)]
    [InlineData(ExportFormat.Mp3)]
    [InlineData(ExportFormat.M4a)]
    [InlineData(ExportFormat.Flac)]
    [InlineData(ExportFormat.Wma)]
    public void Saves_a_file_that_opens_again_with_about_the_same_length(ExportFormat format)
    {
        var folder = Directory.CreateTempSubdirectory("sbk-export-").FullName;
        try
        {
            var clip = Tone();
            var path = Path.Combine(folder, "suono" + AudioExport.InfoOf(format).Extension);
            AudioExport.Save(clip, path, format, 128);
            var back = AudioClip.Load(path);
            Assert.Equal(2, back.Channels);
            Assert.InRange(back.Duration.TotalSeconds, 0.95, 1.1);
            Assert.Single(Directory.GetFiles(folder)); // no temporary file left behind
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void The_quick_filter_keeps_the_centre_and_drops_a_side()
    {
        // Centre: a tone in the voice band on both channels; side: another tone on the left only.
        int frames = Rate;
        var samples = new float[frames * 2];
        for (int f = 0; f < frames; f++)
        {
            float centre = 0.3f * (float)Math.Sin(2 * Math.PI * 500 * f / Rate);
            float side = 0.3f * (float)Math.Sin(2 * Math.PI * 1500 * f / Rate);
            samples[f * 2] = centre + side;
            samples[f * 2 + 1] = centre;
        }
        var result = CenterVoiceFilter.Isolate(new AudioClip(samples, Rate, 2), VoicePart.Voice, CancellationToken.None);

        double error = 0, energy = 0;
        for (int f = Rate / 4; f < 3 * Rate / 4; f++)
        {
            float centre = 0.3f * (float)Math.Sin(2 * Math.PI * 500 * f / Rate);
            error += Math.Pow(result.Samples[f * 2] - centre, 2);
            energy += centre * centre;
        }
        Assert.True(10 * Math.Log10(energy / error) > 10, "the side tone should be mostly gone");
    }

    private sealed class ArraySource(float[] samples, NAudio.Wave.WaveFormat format) : NAudio.Wave.ISampleProvider
    {
        private int _position;

        public NAudio.Wave.WaveFormat WaveFormat => format;

        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count, samples.Length - _position);
            Array.Copy(samples, _position, buffer, offset, n);
            _position += n;
            return n;
        }
    }
}
