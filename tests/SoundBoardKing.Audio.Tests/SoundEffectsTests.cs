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
    public void The_amount_changes_the_effect_while_it_plays()
    {
        var control = new EffectControl { Amount = 0 };
        var robot = SoundEffects.Apply(Tone(), SoundEffect.Robot, control);
        var dry = Render(Tone());

        var first = new float[4800];
        robot.Read(first, 0, first.Length);
        Assert.Equal(dry.Take(first.Length), first);

        control.Amount = 1;
        var second = new float[4800];
        robot.Read(second, 0, second.Length);
        Assert.NotEqual(dry.Skip(first.Length).Take(second.Length), second);
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
