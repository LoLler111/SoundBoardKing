using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoardKing.Audio;

namespace SoundBoardKing.Audio.Tests;

public class LevelingTests
{
    private const int Rate = 48000;

    private static ISampleProvider Tone(double frequency, double gain, double seconds, int channels = 2) =>
        new SignalGenerator(Rate, channels) { Frequency = frequency, Gain = gain }.Take(TimeSpan.FromSeconds(seconds));

    private static float[] Render(ISampleProvider provider)
    {
        var all = new List<float>();
        var buffer = new float[960];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return [.. all];
    }

    [Fact]
    public void A_full_scale_1kHz_stereo_sine_measures_as_in_the_standard()
    {
        // EBU Tech 3341: a 1 kHz sine in both channels reads as many LUFS as its peak in dBFS.
        var info = Loudness.Measure(Tone(1000, 0.1, 5));
        Assert.InRange(info.Lufs, -20.1f, -19.9f);
        Assert.InRange(info.PeakDb, -20.1f, -19.9f);
    }

    [Fact]
    public void Mono_counts_as_the_same_signal_on_both_speakers()
    {
        var stereo = Loudness.Measure(Tone(1000, 0.1, 3));
        var mono = Loudness.Measure(Tone(1000, 0.1, 3, channels: 1));
        Assert.InRange(mono.Lufs - stereo.Lufs, -0.1f, 0.1f);
    }

    [Fact]
    public void Silence_around_a_sound_does_not_make_it_quieter()
    {
        var alone = Loudness.Measure(Tone(1000, 0.1, 2));
        var padded = Loudness.Measure(new ConcatenatingSampleProvider(
        [
            new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)).ToSampleProvider().Take(TimeSpan.FromSeconds(3)),
            Tone(1000, 0.1, 2),
            new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)).ToSampleProvider().Take(TimeSpan.FromSeconds(3)),
        ]));
        // Only the blocks straddling the edges, which the relative gate lets through, count a little.
        Assert.InRange(padded.Lufs - alone.Lufs, -1f, 0.5f);
    }

    [Fact]
    public void A_sound_shorter_than_a_block_is_still_measured()
    {
        var info = Loudness.Measure(Tone(1000, 0.1, 0.2));
        Assert.InRange(info.Lufs, -20.5f, -19.5f);
    }

    [Fact]
    public void Silence_measures_as_the_floor_and_is_left_alone()
    {
        var silence = new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)).ToSampleProvider()
            .Take(TimeSpan.FromSeconds(1));
        var info = Loudness.Measure(silence);
        Assert.Equal(Loudness.SilenceLufs, info.Lufs);
        Assert.Equal(1f, Loudness.GainFor(info.Lufs, info.PeakDb));
    }

    [Fact]
    public void A_file_is_measured_as_its_samples()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sbk-loudness-{Guid.NewGuid():N}.wav");
        try
        {
            WaveFileWriter.CreateWaveFile16(path, Tone(1000, 0.1, 2));
            var info = Loudness.TryMeasure(path);
            Assert.NotNull(info);
            Assert.InRange(info.Value.Lufs, -20.3f, -19.7f);
            Assert.Null(Loudness.TryMeasure(path + ".missing"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void The_gain_brings_sounds_to_the_target()
    {
        Assert.Equal(1f, Loudness.GainFor(null, null));
        // Too loud: cut by 8 dB.
        Assert.Equal(MathF.Pow(10, -8f / 20), Loudness.GainFor(Loudness.TargetLufs + 8, -0.1f), 4);
        // Quiet with headroom: raised by 6 dB.
        Assert.Equal(MathF.Pow(10, 6f / 20), Loudness.GainFor(Loudness.TargetLufs - 6, -12f), 4);
        // Very quiet: raised at most 12 dB.
        Assert.Equal(MathF.Pow(10, 12f / 20), Loudness.GainFor(Loudness.TargetLufs - 30, -40f), 4);
        // Quiet but already peaking at full scale: raised only as far as the limiter can take.
        Assert.Equal(MathF.Pow(10, 4f / 20), Loudness.GainFor(Loudness.TargetLufs - 10, 0f), 4);
    }

    [Fact]
    public void The_limiter_keeps_overlapping_sounds_under_the_ceiling()
    {
        var mix = new MixingSampleProvider([Tone(440, 0.8, 1), Tone(660, 0.8, 1)]);
        var output = Render(new Limiter(mix, new LevelingSwitch()));
        Assert.All(output, s => Assert.True(Math.Abs(s) <= Limiter.Ceiling + 1e-4f, $"{s}"));
        Assert.True(output.Max() > Limiter.Ceiling * 0.8f); // limited, not muted
    }

    [Fact]
    public void The_limiter_leaves_quiet_sound_untouched_apart_from_the_delay()
    {
        var dry = Render(Tone(440, 0.5, 0.5));
        var wet = Render(new Limiter(Tone(440, 0.5, 0.5), new LevelingSwitch()));
        int delay = Array.FindIndex(wet, s => s != 0) - Array.FindIndex(dry, s => s != 0);
        Assert.True(delay > 0);
        for (int i = 0; i + delay < wet.Length; i++)
            Assert.Equal(dry[i], wet[i + delay], 5);
    }

    [Fact]
    public void Switched_off_the_limiter_lets_peaks_through()
    {
        var output = Render(new Limiter(Tone(440, 1.5, 0.5), new LevelingSwitch { Enabled = false }));
        Assert.Contains(output, s => Math.Abs(s) > Limiter.Ceiling + 0.05f);
    }
}
