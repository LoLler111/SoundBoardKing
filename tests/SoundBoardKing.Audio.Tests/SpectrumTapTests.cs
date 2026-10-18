using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace SoundBoardKing.Audio.Tests;

public class SpectrumTapTests
{
    [Fact]
    public void A_tone_lights_up_only_its_band()
    {
        var tone = new SignalGenerator(48_000, 2) { Frequency = 1_000, Gain = 1, Type = SignalGeneratorType.Sin };
        var tap = new SpectrumTap(tone);
        var buffer = new float[4_800];

        // The audio passes through untouched.
        Assert.Equal(buffer.Length, tap.Read(buffer, 0, buffer.Length));
        Assert.Contains(buffer, s => s > 0.9f);

        var levels = new float[SpectrumTap.BandCount];
        tap.Read(levels);

        int loudest = Array.IndexOf(levels, levels.Max());
        // 1 kHz on a 40 Hz - 16 kHz log scale of 24 bands falls in band 11 or 12.
        Assert.InRange(loudest, 11, 12);
        Assert.True(levels[loudest] > 0.9f, $"picco {levels[loudest]}");
        Assert.True(levels[0] < 0.2f && levels[^1] < 0.2f, $"bassi {levels[0]}, alti {levels[^1]}");
    }

    [Fact]
    public void Silence_reads_as_zero()
    {
        var tap = new SpectrumTap(new SilenceProvider(new WaveFormat(48_000, 2)).ToSampleProvider());
        tap.Read(new float[4_800], 0, 4_800);

        var levels = new float[SpectrumTap.BandCount];
        tap.Read(levels);

        Assert.All(levels, level => Assert.Equal(0f, level));
    }
}
