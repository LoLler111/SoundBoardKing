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
        int frames = RenderedFrames(new VarispeedProvider(Tone(1), () => speed));
        Assert.InRange(frames, expectedFrames - 4, expectedFrames + 4);
    }

    [Fact]
    public void Normal_speed_keeps_the_samples()
    {
        var expected = new float[2000];
        Tone(1).Read(expected, 0, expected.Length);
        var actual = new float[2000];
        new VarispeedProvider(Tone(1), () => 1f).Read(actual, 0, actual.Length);
        Assert.Equal(expected, actual);
    }
}

public class SpeedAndPitchTests
{
    private const int Rate = 48000;

    private static ISampleProvider Tone(double seconds, double frequency = 440) =>
        new SignalGenerator(Rate, 2) { Frequency = frequency, Gain = 0.5 }.Take(TimeSpan.FromSeconds(seconds));

    private static float[] Render(ISampleProvider provider)
    {
        var all = new List<float>();
        var buffer = new float[960];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
            all.AddRange(buffer.AsSpan(0, read).ToArray());
        return [.. all];
    }

    // Upward zero crossings of the left channel, away from the edges.
    private static double Frequency(float[] stereo)
    {
        int frames = stereo.Length / 2, from = frames / 4, to = frames * 3 / 4, crossings = 0;
        for (int f = from + 1; f < to; f++)
        {
            if (stereo[(f - 1) * 2] < 0 && stereo[f * 2] >= 0)
                crossings++;
        }
        return crossings / ((to - from) / (double)Rate);
    }

    [Fact]
    public void Normal_speed_and_pitch_keep_the_sound()
    {
        var expected = Render(Tone(1));
        var actual = Render(SpeedAndPitch.Apply(Tone(1), new EffectControl()));

        Assert.InRange(actual.Length, expected.Length - 2, expected.Length + Rate / 50 * 2);
        for (int i = 0; i < expected.Length - 2; i++) // the record-like reading has no frame after the last to blend with
            Assert.True(Math.Abs(expected[i] - actual[i]) < 1e-5f, $"sample {i}: {expected[i]} vs {actual[i]}");
    }

    [Theory]
    [InlineData(2f)]
    [InlineData(1.5f)]
    [InlineData(0.5f)]
    [InlineData(0.75f)]
    public void Speed_changes_the_length_but_not_the_pitch(float speed)
    {
        var output = Render(SpeedAndPitch.Apply(Tone(1), new EffectControl { Speed = speed }));

        double expectedFrames = Rate / speed;
        Assert.InRange(output.Length / 2, expectedFrames - Rate * 0.03, expectedFrames + Rate * 0.03);
        Assert.InRange(Frequency(output), 430, 450);
        Assert.All(output, s => Assert.True(Math.Abs(s) <= 0.55f));
    }

    [Theory]
    [InlineData(2f, 880)]
    [InlineData(0.5f, 220)]
    [InlineData(1.25f, 550)]
    public void Pitch_changes_the_tone_but_not_the_length(float pitch, double expectedFrequency)
    {
        var output = Render(SpeedAndPitch.Apply(Tone(1), new EffectControl { Pitch = pitch }));

        Assert.InRange(output.Length / 2, Rate - Rate * 0.03, Rate + Rate * 0.03);
        Assert.InRange(Frequency(output), expectedFrequency * 0.98, expectedFrequency * 1.02);
    }

    // A low buzzy voice (110 Hz and its harmonics): badly joined pieces would cancel out and make the level wobble.
    private static ISampleProvider Voice(double seconds)
    {
        var parts = Enumerable.Range(1, 6)
            .Select(h => new SignalGenerator(Rate, 2) { Frequency = 110 * h, Gain = 0.3 / h })
            .ToArray<ISampleProvider>();
        return new MixingSampleProvider(parts).Take(TimeSpan.FromSeconds(seconds));
    }

    [Theory]
    [InlineData(2f, 1f)]
    [InlineData(0.5f, 1f)]
    [InlineData(1f, 0.5f)]
    [InlineData(1f, 1.8f)]
    [InlineData(1.6f, 0.7f)]
    public void The_level_stays_steady(float speed, float pitch)
    {
        var dry = Render(Voice(1));
        var wet = Render(SpeedAndPitch.Apply(Voice(1), new EffectControl { Speed = speed, Pitch = pitch }));

        static double Rms(float[] s, int from, int frames)
        {
            double sum = 0;
            for (int f = from; f < from + frames; f++)
                sum += s[f * 2] * s[f * 2];
            return Math.Sqrt(sum / frames);
        }
        double reference = Rms(dry, Rate / 4, Rate / 2);
        int window = Rate / 50; // 20 ms, longer than one period even an octave down
        for (int from = window * 2; from + window * 3 < wet.Length / 2; from += window / 2)
            Assert.InRange(Rms(wet, from, window), reference * 0.8, reference * 1.2);
    }

    [Fact]
    public void Both_together_and_changed_while_playing()
    {
        var control = new EffectControl { Speed = 2f, Pitch = 0.5f };
        var chain = SpeedAndPitch.Apply(Tone(2), control);
        var first = new float[Rate]; // half a second of output
        Assert.Equal(first.Length, chain.Read(first, 0, first.Length));
        Assert.InRange(Frequency(first), 215, 225);

        control.Speed = 0.5f;
        control.Pitch = 1.5f;
        var rest = Render(chain);
        // The first half second used one second of the source; the other second lasts twice as long.
        Assert.InRange(rest.Length / 2, Rate * 2 - Rate * 0.05, Rate * 2 + Rate * 0.05);
        Assert.InRange(Frequency(rest), 650, 670);
        Assert.All(rest, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 0.55f));
    }
}
