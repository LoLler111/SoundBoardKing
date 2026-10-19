using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoardKing.Audio;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Audio.Tests;

public class SoundEffectsTests
{
    private const int Rate = 48000;

    // Half a second of a 440 Hz tone, stereo, as the mixers use.
    private static ISampleProvider Tone() =>
        new SignalGenerator(Rate, 2) { Frequency = 440, Gain = 0.5 }.Take(TimeSpan.FromSeconds(0.5));

    private static float[] Render(ISampleProvider provider)
    {
        var all = new List<float>();
        var buffer = new float[960];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return [.. all];
    }

    [Theory]
    [InlineData(SoundEffect.Chipmunk)]
    [InlineData(SoundEffect.Deep)]
    [InlineData(SoundEffect.Robot)]
    [InlineData(SoundEffect.Radio)]
    [InlineData(SoundEffect.Echo)]
    public void Every_effect_changes_the_sound_and_stays_in_range(SoundEffect effect)
    {
        var dry = Render(Tone());
        var wet = Render(SoundEffects.Apply(Tone(), effect));

        Assert.Equal(dry.Length % 2, wet.Length % 2);
        Assert.All(wet, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.5f));
        int compared = Math.Min(dry.Length, wet.Length);
        Assert.True(Enumerable.Range(0, compared).Any(i => Math.Abs(dry[i] - wet[i]) > 0.01f));
    }

    [Fact]
    public void No_effect_leaves_the_sound_untouched()
    {
        var source = Tone();
        Assert.Same(source, SoundEffects.Apply(source, SoundEffect.None));
    }

    [Fact]
    public void Echo_keeps_sounding_after_the_sound_ends()
    {
        var dry = Render(Tone());
        var wet = Render(SoundEffects.Apply(Tone(), SoundEffect.Echo));

        Assert.True(wet.Length > dry.Length);
        Assert.Contains(wet.Skip(dry.Length), s => Math.Abs(s) > 0.01f);
    }
}
