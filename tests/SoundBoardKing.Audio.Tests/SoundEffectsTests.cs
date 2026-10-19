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
    public void The_effect_type_changes_while_the_sound_plays()
    {
        var control = new EffectControl { Amount = 1 };
        var live = new LiveEffect(Tone(), control);
        var dry = Render(Tone());

        var first = new float[4800];
        live.Read(first, 0, first.Length);
        Assert.Equal(dry.Take(first.Length), first); // no effect yet

        control.Effect = SoundEffect.Robot;
        var second = new float[4800];
        live.Read(second, 0, second.Length);
        Assert.NotEqual(dry.Skip(4800).Take(4800), second);

        // "Voice only": the sounds play clean again, with the robot still chosen.
        control.SoundsEnabled = false;
        var third = new float[4800];
        live.Read(third, 0, third.Length);
        Assert.Equal(dry.Skip(9600).Take(4800), third);
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

public class VarispeedTests
{
    private static ISampleProvider Tone(double seconds) =>
        new SignalGenerator(48000, 2) { Frequency = 440, Gain = 0.5 }.Take(TimeSpan.FromSeconds(seconds));

    private static int RenderedFrames(ISampleProvider provider)
    {
        var buffer = new float[1000];
        int total = 0, read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            total += read;
        return total / 2;
    }

    [Theory]
    [InlineData(1f, 48000)]
    [InlineData(2f, 24000)]
    [InlineData(0.5f, 96000)]
    public void Speed_changes_how_long_the_sound_lasts(float speed, int expectedFrames)
    {
        var control = new EffectControl { Speed = speed };
        int frames = RenderedFrames(new VarispeedProvider(Tone(1), control));
        Assert.InRange(frames, expectedFrames - 4, expectedFrames + 4);
    }

    [Fact]
    public void Normal_speed_keeps_the_samples()
    {
        var expected = new float[2000];
        Tone(1).Read(expected, 0, expected.Length);
        var actual = new float[2000];
        new VarispeedProvider(Tone(1), new EffectControl()).Read(actual, 0, actual.Length);
        Assert.Equal(expected, actual);
    }
}
