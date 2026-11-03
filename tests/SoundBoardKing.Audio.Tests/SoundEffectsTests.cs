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
    [InlineData(SoundEffect.Megaphone)]
    [InlineData(SoundEffect.Overdrive)]
    [InlineData(SoundEffect.Bitcrush)]
    [InlineData(SoundEffect.Muffled)]
    [InlineData(SoundEffect.Underwater)]
    [InlineData(SoundEffect.Reverb)]
    [InlineData(SoundEffect.Chorus)]
    [InlineData(SoundEffect.Flanger)]
    [InlineData(SoundEffect.Vibrato)]
    [InlineData(SoundEffect.WalkieTalkie)]
    [InlineData(SoundEffect.Demon)]
    [InlineData(SoundEffect.Alien)]
    [InlineData(SoundEffect.Autotune)]
    [InlineData(SoundEffect.Whisper)]
    [InlineData(SoundEffect.Vinyl)]
    [InlineData(SoundEffect.Stutter)]
    [InlineData(SoundEffect.Reverse)]
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

    // Effects that shift the phase (vibrato, under water, the pitch shifters) are checked on their own below.
    [Theory]
    [InlineData(SoundEffect.Megaphone, false)]
    [InlineData(SoundEffect.Overdrive, false)]
    [InlineData(SoundEffect.Bitcrush, false)]
    [InlineData(SoundEffect.Muffled, false)]
    [InlineData(SoundEffect.Reverb, true)]
    [InlineData(SoundEffect.Chorus, false)]
    [InlineData(SoundEffect.Flanger, false)]
    [InlineData(SoundEffect.WalkieTalkie, true)]
    public void New_effects_keep_the_length_and_follow_the_amount(SoundEffect effect, bool ringsOn)
    {
        var dry = Render(Tone());
        var soft = Render(SoundEffects.Apply(Tone(), effect, new EffectControl { Amount = 0.2f }));
        var hard = Render(SoundEffects.Apply(Tone(), effect, new EffectControl { Amount = 1f }));

        if (ringsOn)
        {
            Assert.True(soft.Length > dry.Length && hard.Length > dry.Length);
        }
        else
        {
            Assert.Equal(dry.Length, soft.Length);
            Assert.Equal(dry.Length, hard.Length);
        }
        Assert.All(hard, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.5f));
        static double Distance(float[] a, float[] b) => a.Zip(b, (x, y) => Math.Abs(x - y)).Average(); // over the shorter
        Assert.True(Distance(dry, hard) > Distance(dry, soft), $"{effect}: more amount should change the sound more");
    }

    [Fact]
    public void Reverb_rings_on_and_fades_to_silence()
    {
        var dry = Render(Tone());
        var wet = Render(SoundEffects.Apply(Tone(), SoundEffect.Reverb));

        Assert.Equal(dry.Length + 4 * Rate * 2, wet.Length);
        Assert.Contains(wet.Skip(dry.Length + Rate * 2 / 2), s => Math.Abs(s) > 0.005f); // half a second later, still ringing
        Assert.All(wet.TakeLast(100), s => Assert.True(Math.Abs(s) < 0.001f));
    }

    [Fact]
    public void Walkie_talkie_beeps_and_crackles_after_the_voice_and_then_falls_silent()
    {
        var dry = Render(Tone());
        var wet = Render(SoundEffects.Apply(Tone(), SoundEffect.WalkieTalkie));

        var tail = wet.Skip(dry.Length).ToArray();
        Assert.Equal((int)(0.8 * Rate) * 2, tail.Length);
        // Quiet while the gate waits (past the filters' ring-out from the sudden end), then the beep (0.2 * 0.7 at the default amount).
        Assert.All(tail.Skip(Rate / 50 * 2).Take(Rate / 6 * 2), s => Assert.True(Math.Abs(s) < 0.05f));
        Assert.Contains(tail, s => Math.Abs(s) > 0.1f);
        Assert.All(tail.TakeLast(Rate / 10 * 2), s => Assert.True(Math.Abs(s) < 1e-4f));
    }

    // Power of one frequency in the left channel's second half (Goertzel).
    private static double Power(float[] stereo, double hz)
    {
        double coefficient = 2 * Math.Cos(2 * Math.PI * hz / Rate), s1 = 0, s2 = 0;
        int frames = stereo.Length / 2, from = frames / 2;
        for (int f = from; f < frames; f++)
        {
            double s = stereo[f * 2] + coefficient * s1 - s2;
            s2 = s1;
            s1 = s;
        }
        return (s1 * s1 + s2 * s2 - coefficient * s1 * s2) / ((double)(frames - from) * (frames - from));
    }

    [Fact]
    public void Demon_adds_a_much_lower_voice_under_the_original()
    {
        var dry = Render(Tone());
        var wet = Render(SoundEffects.Apply(Tone(), SoundEffect.Demon));

        // About 0.65 times 440 Hz at the default amount (a 0.5 sine has 0.0625), with the original still there.
        double lowest = Enumerable.Range(0, 26).Max(k => Power(wet, 260 + 2 * k));
        Assert.True(lowest > 0.01, $"260-310 Hz: {lowest:E2}");
        Assert.True(Power(wet, 440) > Power(dry, 440) * 0.05);
    }

    private static ISampleProvider Tone(double seconds, double hz) =>
        new SignalGenerator(Rate, 2) { Frequency = hz, Gain = 0.5 }.Take(TimeSpan.FromSeconds(seconds));

    [Theory]
    [InlineData(452, 1f, 440)]     // a bit sharp of A: down to A
    [InlineData(425, 1f, 440)]     // nearer to G#, but at full amount only the pentatonic counts: up to A
    [InlineData(425, 0.4f, 417.2)] // every semitone below half the amount: towards G# (415.3), 80% of the way
    public void Autotune_pulls_an_off_key_tone_to_its_note(double hz, float amount, double note)
    {
        var free = Periods(Render(SoundEffects.Apply(Tone(1, hz), SoundEffect.Autotune, new EffectControl { Amount = 0 })));
        var tuned = Periods(Render(SoundEffects.Apply(Tone(1, hz), SoundEffect.Autotune, new EffectControl { Amount = amount })));

        double Pitch(List<double> periods) => Rate / periods.Skip(periods.Count / 2).Average();
        Assert.InRange(Pitch(free), hz * 0.995, hz * 1.005);
        Assert.InRange(Pitch(tuned), note * 0.99, note * 1.01);
    }

    // A voice-like tone: six harmonics gliding 180 -> 260 Hz with a 5 Hz vibrato, never resting on a note.
    private static ISampleProvider GlidingVoice(double seconds)
    {
        int frames = (int)(Rate * seconds);
        var samples = new float[frames * 2];
        double phase = 0;
        for (int i = 0; i < frames; i++)
        {
            double t = (double)i / Rate;
            phase += 2 * Math.PI * (180 + 80 * t / seconds + 4 * Math.Sin(2 * Math.PI * 5 * t)) / Rate;
            double v = 0;
            for (int h = 1; h <= 6; h++)
                v += Math.Sin(h * phase) / h;
            samples[i * 2] = samples[i * 2 + 1] = (float)(0.15 * v);
        }
        var provider = new BufferedWaveProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)) { BufferLength = samples.Length * 4, ReadFully = false };
        provider.AddSamples(samples.SelectMany(BitConverter.GetBytes).ToArray(), 0, samples.Length * 4);
        return provider.ToSampleProvider();
    }

    // Mean distance of the pitch from the nearest semitone, in cents, past the first half second.
    private static double CentsOffKey(float[] stereo)
    {
        var detector = new PitchDetector(Rate);
        double sum = 0;
        int counted = 0;
        for (int f = 0; f < stereo.Length / 2; f++)
        {
            if (!detector.Push(stereo[f * 2]) || f < Rate / 2 || detector.Hz <= 0)
                continue;
            double note = 12 * Math.Log2(detector.Hz / 440);
            sum += Math.Abs(note - Math.Round(note)) * 100;
            counted++;
        }
        return sum / counted;
    }

    // Share of the output's notes (past the first half second) that are C, D, E, G or A.
    private static double PentatonicShare(float[] stereo)
    {
        bool[] pentatonic = [true, false, true, false, true, false, false, true, false, true, false, false];
        var detector = new PitchDetector(Rate);
        int inScale = 0, counted = 0;
        for (int f = 0; f < stereo.Length / 2; f++)
        {
            if (!detector.Push(stereo[f * 2]) || f < Rate / 2 || detector.Hz <= 0)
                continue;
            int note = (int)Math.Round(12 * Math.Log2(detector.Hz / 440));
            inScale += pentatonic[((note - 3) % 12 + 12) % 12] ? 1 : 0;
            counted++;
        }
        return (double)inScale / counted;
    }

    [Fact]
    public void Autotune_at_high_amount_jumps_between_the_notes_of_the_scale()
    {
        // The voice glides through F# G G# A A# B C: at full amount only G, A and C come out.
        double scaled = PentatonicShare(Render(SoundEffects.Apply(GlidingVoice(3), SoundEffect.Autotune, new EffectControl { Amount = 1 })));
        double chromatic = PentatonicShare(Render(SoundEffects.Apply(GlidingVoice(3), SoundEffect.Autotune, new EffectControl { Amount = 0.4f })));

        Assert.True(scaled > 0.95, $"full amount: {scaled:P0} on the scale");
        Assert.True(chromatic < 0.7, $"little amount: {chromatic:P0} on the scale");
    }

    [Fact]
    public void Autotune_keeps_a_gliding_voice_on_the_notes()
    {
        double free = CentsOffKey(Render(GlidingVoice(3)));
        double tuned = CentsOffKey(Render(SoundEffects.Apply(GlidingVoice(3), SoundEffect.Autotune)));

        Assert.InRange(free, 20, 30); // off key anywhere: 25 on average
        Assert.True(tuned < 8, $"at the default amount: {tuned:F1} cents from the note (free: {free:F1})");
    }

    [Fact]
    public void Whisper_keeps_the_level_but_takes_the_tone_away()
    {
        static double Rms(float[] s) => Math.Sqrt(s.Skip(s.Length / 2).Average(x => (double)x * x));
        var dry = Render(Tone());
        var wet = Render(SoundEffects.Apply(Tone(), SoundEffect.Whisper, new EffectControl { Amount = 1 }));

        Assert.Equal(dry.Length, wet.Length);
        Assert.InRange(Rms(wet), Rms(dry) * 0.4, Rms(dry) * 1.6);
        Assert.True(Power(wet, 440) < Power(dry, 440) * 0.1, $"440 Hz: {Power(wet, 440):E2} vs {Power(dry, 440):E2}");
    }

    [Fact]
    public void Vinyl_crackles_even_in_silence_and_not_at_no_amount()
    {
        static ISampleProvider Silence() => new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)).ToSampleProvider().Take(TimeSpan.FromSeconds(1));
        var crackle = Render(SoundEffects.Apply(Silence(), SoundEffect.Vinyl, new EffectControl { Amount = 1 }));
        var quiet = Render(SoundEffects.Apply(Silence(), SoundEffect.Vinyl, new EffectControl { Amount = 0 }));

        Assert.True(crackle.Count(s => Math.Abs(s) > 0.03f) > 20);
        Assert.All(crackle, s => Assert.True(Math.Abs(s) <= 0.35f));
        Assert.All(quiet, s => Assert.Equal(0f, s));
    }

    [Theory]
    [InlineData(1f, 3)]
    [InlineData(0.7f, 2)]
    [InlineData(0.2f, 1)]
    [InlineData(0f, 0)]
    public void Stutter_repeats_loud_pieces_and_lengthens_the_sound(float amount, int repeats)
    {
        // 1.2 s: two 0.6 s periods, each starting with an 80 ms piece.
        var wet = Render(SoundEffects.Apply(Tone(1.2, 440), SoundEffect.Stutter, new EffectControl { Amount = amount }));
        int piece = (int)(Rate * 0.08);
        Assert.Equal((Rate * 12 / 10 + 2 * repeats * piece) * 2, wet.Length);

        if (repeats > 0) // the first repeat replays the start of the piece
            Assert.Equal(wet.Skip(piece * 2 + 2 * 200).Take(100), wet.Skip(2 * 200).Take(100));

        var silence = Render(SoundEffects.Apply(
            new SilenceProvider(WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2)).ToSampleProvider().Take(TimeSpan.FromSeconds(1.2)),
            SoundEffect.Stutter, new EffectControl { Amount = 1 }));
        Assert.Equal(Rate * 12 / 10 * 2, silence.Length); // nothing loud, nothing repeated
    }

    private sealed class FramesProvider(int frames) : ISampleProvider
    {
        private int _next;
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(Rate, 2);

        // Frame f is (f, -f) / frames: easy to check the order and the channels.
        public int Read(float[] buffer, int offset, int count)
        {
            int n = Math.Min(count / 2, frames - _next);
            for (int i = 0; i < n; i++, _next++)
            {
                buffer[offset + i * 2] = _next / (float)frames;
                buffer[offset + i * 2 + 1] = -_next / (float)frames;
            }
            return n * 2;
        }
    }

    [Fact]
    public void Reverse_plays_the_frames_from_the_end_keeping_the_channels()
    {
        const int frames = 10000;
        var reverse = new ReverseEffect(new FramesProvider(frames));
        Assert.True(reverse.WaitLoaded(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, reverse.Progress);

        var all = Render(reverse);
        Assert.Equal(frames * 2, all.Length);
        for (int f = 0; f < frames; f++)
        {
            Assert.Equal((frames - 1 - f) / (float)frames, all[f * 2]);
            Assert.Equal(-(frames - 1 - f) / (float)frames, all[f * 2 + 1]);
        }
        Assert.Equal(1, reverse.Progress);
    }

    [Fact]
    public void A_sound_turned_backwards_stays_backwards()
    {
        var control = new EffectControl { Effect = SoundEffect.Reverse };
        var live = new LiveEffect(new FramesProvider(48000), control);
        var first = new float[960];
        live.Read(first, 0, first.Length);
        var reversed = Assert.IsType<ReverseEffect>(live.Reversed);
        Assert.True(reversed.WaitLoaded(TimeSpan.FromSeconds(5)));

        control.Effect = SoundEffect.None;
        var next = new float[960];
        live.Read(next, 0, next.Length);
        Assert.Same(reversed, live.Reversed);
        Assert.True(next[0] > 0.9f); // from the end of the sound
    }

    [Fact]
    public void Vibrato_shakes_the_pitch_with_the_amount()
    {
        var still = Periods(Render(SoundEffects.Apply(Tone(), SoundEffect.Vibrato, new EffectControl { Amount = 0 })));
        var shaking = Periods(Render(SoundEffects.Apply(Tone(), SoundEffect.Vibrato, new EffectControl { Amount = 1 })));

        Assert.True(still.Max() / still.Min() < 1.01, $"no amount: {still.Min():F2}..{still.Max():F2}");
        Assert.True(shaking.Max() / shaking.Min() > 1.06, $"full amount: {shaking.Min():F2}..{shaking.Max():F2}");
    }

    [Fact]
    public void Overdrive_never_goes_past_its_ceiling()
    {
        var loud = new SignalGenerator(Rate, 2) { Frequency = 80, Gain = 1 }.Take(TimeSpan.FromSeconds(0.5));
        var wet = Render(SoundEffects.Apply(loud, SoundEffect.Overdrive, new EffectControl { Amount = 1 }));

        Assert.All(wet, s => Assert.True(Math.Abs(s) <= 0.7f + 1e-6f));
        Assert.Contains(wet, s => Math.Abs(s) > 0.69f);
    }

    [Fact]
    public void Bitcrush_holds_frames_and_rounds_them()
    {
        var wet = Render(SoundEffects.Apply(Tone(), SoundEffect.Bitcrush, new EffectControl { Amount = 1 }));

        // At full amount 11 frames in a row repeat (48 kHz -> ~4.4 kHz) and only 4 bits are left.
        for (int f = 0; f + 11 <= wet.Length / 2; f += 11)
            Assert.All(Enumerable.Range(f, 11), g => Assert.Equal(wet[f * 2], wet[g * 2]));
        Assert.InRange(wet.Distinct().Count(), 2, 17);
    }

    [Fact]
    public void Muffled_takes_the_highs_away()
    {
        static ISampleProvider High() => new SignalGenerator(Rate, 2) { Frequency = 4000, Gain = 0.5 }.Take(TimeSpan.FromSeconds(0.5));
        static double Rms(float[] s) => Math.Sqrt(s.Skip(s.Length / 2).Average(x => (double)x * x));

        double dry = Rms(Render(High()));
        double wet = Rms(Render(SoundEffects.Apply(High(), SoundEffect.Muffled)));
        Assert.True(wet < dry * 0.05, $"4 kHz through the door: {wet:F4} vs {dry:F4}");
    }

    // Lengths of the left channel's cycles, from interpolated upward zero crossings, past the start-up.
    private static List<double> Periods(float[] stereo)
    {
        var crossings = new List<double>();
        for (int f = Rate / 10; f < stereo.Length / 2; f++)
        {
            float a = stereo[(f - 1) * 2], b = stereo[f * 2];
            if (a < 0 && b >= 0)
                crossings.Add(f - 1 + a / (a - b));
        }
        return [.. crossings.Zip(crossings.Skip(1), (x, y) => y - x)];
    }

    // The wobble shifts the phase, so a sample by sample comparison says little: check level and pitch instead.
    [Fact]
    public void Underwater_darkens_and_wobbles_more_with_the_amount()
    {
        static double Rms(float[] s) => Math.Sqrt(s.Skip(s.Length / 2).Average(x => (double)x * x));
        var still = Render(SoundEffects.Apply(Tone(), SoundEffect.Underwater, new EffectControl { Amount = 0 }));
        var soft = Render(SoundEffects.Apply(Tone(), SoundEffect.Underwater, new EffectControl { Amount = 0.2f }));
        var hard = Render(SoundEffects.Apply(Tone(), SoundEffect.Underwater, new EffectControl { Amount = 1 }));

        Assert.Equal(Render(Tone()).Length, hard.Length);
        Assert.All(hard, s => Assert.True(float.IsFinite(s) && Math.Abs(s) <= 1.5f));
        Assert.True(Rms(hard) < Rms(soft) * 0.7, $"440 Hz under water: {Rms(hard):F3} vs {Rms(soft):F3}");

        // At full amount the pitch swings by about 5% each way; with no amount it stays put.
        var steady = Periods(still);
        var wobbling = Periods(hard);
        Assert.True(steady.Max() / steady.Min() < 1.01, $"no amount: {steady.Min():F2}..{steady.Max():F2}");
        Assert.True(wobbling.Max() / wobbling.Min() > 1.05, $"full amount: {wobbling.Min():F2}..{wobbling.Max():F2}");
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
