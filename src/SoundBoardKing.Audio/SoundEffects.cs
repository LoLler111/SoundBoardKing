using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Audio;

/// <summary>How strong the effect is, 0..1; read by the effects on every buffer, so it can change while sounds play.</summary>
public sealed class EffectControl
{
    private float _amount = DefaultAmount;

    public const float DefaultAmount = 0.7f;

    public float Amount
    {
        get => Volatile.Read(ref _amount);
        set => Volatile.Write(ref _amount, Math.Clamp(value, 0f, 1f));
    }

    private volatile bool _soundsEnabled = true;

    /// <summary>False when the effect is set to the voice only (or off): sounds play clean, even those already playing.</summary>
    public bool SoundsEnabled
    {
        get => _soundsEnabled;
        set => _soundsEnabled = value;
    }

    private volatile int _effect;

    /// <summary>The effect every sound plays through; changing it switches the sounds already playing too.</summary>
    public SoundEffect Effect
    {
        get => (SoundEffect)_effect;
        set => _effect = (int)value;
    }

    /// <summary>The effect the sounds actually use: none while the effect is set to the voice only (or off).</summary>
    public SoundEffect ActiveEffect => _soundsEnabled ? Effect : SoundEffect.None;

    /// <summary>The amount the sound effects use: 0 while they are switched off.</summary>
    public float Wet => _soundsEnabled ? Amount : 0f;

    private float _speed = 1f;
    private float _pitch = 1f;

    public const float MinSpeed = 0.5f;
    public const float MaxSpeed = 2f;
    public const float MinPitch = 0.5f;
    public const float MaxPitch = 2f;

    /// <summary>Playback speed with the pitch kept, like a voice message at 2x: 2 is twice as fast.</summary>
    public float Speed
    {
        get => Volatile.Read(ref _speed);
        set => Volatile.Write(ref _speed, Math.Clamp(value, MinSpeed, MaxSpeed));
    }

    /// <summary>Pitch factor with the speed kept: 2 is an octave higher, 0.5 an octave lower.</summary>
    public float Pitch
    {
        get => Volatile.Read(ref _pitch);
        set => Volatile.Write(ref _pitch, Math.Clamp(value, MinPitch, MaxPitch));
    }
}

/// <summary>Speed and pitch apart, both following <see cref="EffectControl"/> while the sound plays.</summary>
internal static class SpeedAndPitch
{
    // Read faster by the pitch (higher and shorter, like a record), then stretched back to the speed.
    public static ISampleProvider Apply(ISampleProvider source, EffectControl control) =>
        new TimeStretchProvider(new VarispeedProvider(source, () => control.Pitch), () => control.Speed / control.Pitch);
}

/// <summary>
/// Reads the source faster or slower than real time, following <paramref name="rate"/> while it
/// plays; linear interpolation between frames, so pitch moves with the speed, like a record.
/// </summary>
internal sealed class VarispeedProvider(ISampleProvider source, Func<float> rate) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private float[] _frames = new float[4096 * source.WaveFormat.Channels];
    private int _count;       // valid frames in _frames
    private double _position; // read position in frames, from the start of _frames
    private bool _sourceEnded;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int wanted = count / _channels;
        float speed = rate();
        int written = 0;
        while (written < wanted)
        {
            int index = (int)_position;
            if (index + 1 >= _count && !Refill())
                break;
            index = (int)_position;
            float t = (float)(_position - index);
            int a = index * _channels, b = a + _channels;
            for (int c = 0; c < _channels; c++)
                buffer[offset + written * _channels + c] = _frames[a + c] + (_frames[b + c] - _frames[a + c]) * t;
            written++;
            _position += speed;
        }
        return written * _channels;
    }

    // Keeps the frames still needed, reads more behind them; false once nothing is left to interpolate.
    private bool Refill()
    {
        // Drop the frames already passed; at high speed the position may be past the end, so the
        // skipped frames come out of the next read instead.
        int start = Math.Min((int)_position, _count);
        Array.Copy(_frames, start * _channels, _frames, 0, (_count - start) * _channels);
        _count -= start;
        _position -= start;
        while (!_sourceEnded && (_count * _channels < _frames.Length / 2 || (int)_position + 1 >= _count))
        {
            int read = source.Read(_frames, _count * _channels, _frames.Length - _count * _channels);
            if (read == 0)
                _sourceEnded = true;
            _count += read / _channels;
        }
        return (int)_position + 1 < _count;
    }
}

/// <summary>Builds the effect chain a sound plays through.</summary>
public static class SoundEffects
{
    public static ISampleProvider Apply(ISampleProvider source, SoundEffect effect, EffectControl? control = null)
    {
        control ??= new EffectControl();
        return effect switch
        {
            // At the default amount about five semitones up or down: clearly changed, still understandable.
            SoundEffect.Chipmunk => new PitchEffect(source, control, a => 1f + 0.65f * a),
            SoundEffect.Deep => new PitchEffect(source, control, a => 1f - 0.4f * a),
            SoundEffect.Robot => new RobotEffect(source, control),
            SoundEffect.Radio => new RadioEffect(source, control),
            SoundEffect.Echo => new EchoEffect(source, control),
            SoundEffect.Megaphone => new MegaphoneEffect(source, control),
            SoundEffect.Overdrive => new OverdriveEffect(source, control),
            SoundEffect.Bitcrush => new BitcrushEffect(source, control),
            SoundEffect.Muffled => new MuffledEffect(source, control),
            SoundEffect.Underwater => new UnderwaterEffect(source, control),
            SoundEffect.Reverb => new ReverbEffect(source, control),
            SoundEffect.Chorus => new ChorusEffect(source, control),
            SoundEffect.Flanger => new FlangerEffect(source, control),
            SoundEffect.Vibrato => new VibratoEffect(source, control),
            SoundEffect.WalkieTalkie => new WalkieTalkieEffect(source, control),
            SoundEffect.Demon => new DemonEffect(source, control),
            SoundEffect.Alien => new AlienEffect(new PitchEffect(source, control, a => 1f + 0.25f * a), control),
            SoundEffect.Autotune => new AutotuneEffect(source, control),
            SoundEffect.Whisper => new WhisperEffect(source, control),
            SoundEffect.Vinyl => new VinylEffect(source, control),
            SoundEffect.Stutter => new StutterEffect(source, control),
            SoundEffect.Reverse => new ReverseEffect(source),
            _ => source,
        };
    }

    /// <summary>Low-pass cutoff sliding from <paramref name="from"/> Hz at amount 0 to <paramref name="to"/> at 1, evenly by ear.</summary>
    internal static float Cutoff(float from, float to, float amount, int rate) =>
        Math.Min(from * MathF.Pow(to / from, amount), rate * 0.45f);
}

/// <summary>
/// Follows <see cref="EffectControl.ActiveEffect"/> while the sound plays: on a change the next buffer
/// goes through a fresh instance of the new effect, so no echo or filter state leaks from the old one.
/// </summary>
internal sealed class LiveEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private SoundEffect _current = SoundEffect.None;
    private ISampleProvider _chain = source;
    private bool _sourceEnded;

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>The backwards playback this sound switched to, if any.</summary>
    public ReverseEffect? Reversed => _chain as ReverseEffect;

    public int Read(float[] buffer, int offset, int count)
    {
        var wanted = control.ActiveEffect;
        // Once the sound has ended only the effect's own tail (echo) is left: switching then would cut it.
        // Played backwards, the rest of the sound has already been read: it stays backwards to the end.
        if (wanted != _current && !_sourceEnded && _chain is not ReverseEffect)
        {
            _current = wanted;
            _chain = SoundEffects.Apply(source, wanted, control);
        }
        int read = _chain.Read(buffer, offset, count);
        if (read < count)
            _sourceEnded = true;
        return read;
    }
}

/// <summary>Pitch shift whose factor follows the amount while the sound plays.</summary>
internal sealed class PitchEffect(ISampleProvider source, EffectControl control, Func<float, float> factor) : ISampleProvider
{
    private readonly SmbPitchShiftingSampleProvider _shifter = new(source);

    public WaveFormat WaveFormat => _shifter.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        _shifter.PitchFactor = factor(control.Wet);
        return _shifter.Read(buffer, offset, count);
    }
}

/// <summary>Ring modulation with a low sine: the classic metallic robot voice. The amount is the wet share.</summary>
internal sealed class RobotEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private const double Frequency = 55;
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly double _step = 2 * Math.PI * Frequency / source.WaveFormat.SampleRate;
    private double _phase;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float wet = control.Wet;
        for (int i = 0; i < read; i += _channels)
        {
            float carrier = (float)Math.Sin(_phase) * 1.4f;
            _phase += _step;
            if (_phase > 2 * Math.PI)
                _phase -= 2 * Math.PI;
            for (int c = 0; c < _channels && i + c < read; c++)
            {
                float dry = buffer[offset + i + c];
                buffer[offset + i + c] = dry + (dry * carrier - dry) * wet;
            }
        }
        return read;
    }
}

/// <summary>Telephone band (300 Hz - 3.4 kHz) with saturation that grows with the amount.</summary>
internal sealed class RadioEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;

    public RadioEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        int rate = source.WaveFormat.SampleRate;
        int channels = source.WaveFormat.Channels;
        _highPass = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.HighPassFilter(rate, 300, 0.8f)).ToArray();
        _lowPass = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.LowPassFilter(rate, 3400, 0.8f)).ToArray();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = _highPass.Length;
        float wet = _control.Wet;
        float drive = 1f + 3f * wet;
        float level = 0.6f / MathF.Tanh(drive * 0.5f + 0.5f);
        for (int i = 0; i < read; i++)
        {
            int c = i % channels;
            float dry = buffer[offset + i];
            float band = _lowPass[c].Transform(_highPass[c].Transform(dry));
            float radio = MathF.Tanh(band * drive) * level;
            buffer[offset + i] = dry + (radio - dry) * wet;
        }
        return read;
    }
}

/// <summary>Feedback delay; keeps sounding for a moment after the sound ends so the echo fades out.</summary>
internal sealed class EchoEffect : ISampleProvider
{
    private const float Delay = 0.28f;
    private const float TailSeconds = 1.5f;

    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly float[] _line;
    private readonly int _tail;
    private int _position;
    private int _tailLeft = -1;

    public EchoEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        var format = source.WaveFormat;
        _line = new float[(int)(Delay * format.SampleRate) * format.Channels];
        _tail = (int)(TailSeconds * format.SampleRate) * format.Channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _tailLeft < 0 ? _source.Read(buffer, offset, count) : 0;
        if (read < count && _tailLeft < 0)
            _tailLeft = _tail;

        // Past the end of the sound, keep reading silence through the delay line until the tail is out.
        int total = read;
        if (read < count && _tailLeft > 0)
        {
            int extra = Math.Min(count - read, _tailLeft);
            Array.Clear(buffer, offset + read, extra);
            _tailLeft -= extra;
            total += extra;
        }

        // More amount: louder repeats that last longer.
        float amount = _control.Wet;
        float wet = 0.8f * amount;
        float feedback = 0.2f + 0.4f * amount;
        for (int i = offset; i < offset + total; i++)
        {
            float delayed = _line[_position];
            float dry = buffer[i];
            _line[_position] = dry + delayed * feedback;
            buffer[i] = dry + delayed * wet;
            if (++_position == _line.Length)
                _position = 0;
        }
        return total;
    }
}

/// <summary>Loudhailer: a narrow band (500 Hz - 2.5 kHz) honking at 1.5 kHz, driven harder than the radio.</summary>
internal sealed class MegaphoneEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;
    private readonly BiQuadFilter[] _honk;

    public MegaphoneEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        int rate = source.WaveFormat.SampleRate;
        int channels = source.WaveFormat.Channels;
        _highPass = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.HighPassFilter(rate, 500, 0.8f)).ToArray();
        _lowPass = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.LowPassFilter(rate, 2500, 0.8f)).ToArray();
        _honk = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.PeakingEQ(rate, 1500, 1.5f, 9f)).ToArray();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = _highPass.Length;
        float wet = _control.Wet;
        float drive = 2f + 6f * wet;
        float level = 0.6f / MathF.Tanh(drive * 0.4f);
        for (int i = 0; i < read; i++)
        {
            int c = i % channels;
            float dry = buffer[offset + i];
            float band = _honk[c].Transform(_lowPass[c].Transform(_highPass[c].Transform(dry)));
            float megaphone = MathF.Tanh(band * drive) * level;
            buffer[offset + i] = dry + (megaphone - dry) * wet;
        }
        return read;
    }
}

/// <summary>Blown out: the bass pushed up and the sound clipped flat, capped so it stays loud but not painful.</summary>
internal sealed class OverdriveEffect : ISampleProvider
{
    private const float Ceiling = 0.7f;

    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly BiQuadFilter[] _bass;

    public OverdriveEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        int rate = source.WaveFormat.SampleRate;
        _bass = Enumerable.Range(0, source.WaveFormat.Channels).Select(_ => BiQuadFilter.LowPassFilter(rate, 150, 0.8f)).ToArray();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = _bass.Length;
        float wet = _control.Wet;
        float gain = 1f + 16f * wet;
        for (int i = 0; i < read; i++)
        {
            int c = i % channels;
            float dry = buffer[offset + i];
            float boosted = (dry + 3f * wet * _bass[c].Transform(dry)) * gain;
            float blown = Math.Clamp(boosted, -1f, 1f) * Ceiling;
            buffer[offset + i] = dry + (blown - dry) * wet;
        }
        return read;
    }
}

/// <summary>8-bit voice: each frame held for several (about 6 kHz at the default amount) and rounded to few levels.</summary>
internal sealed class BitcrushEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private readonly float[] _held = new float[source.WaveFormat.Channels];
    private int _left; // frames the held one still lasts

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float amount = control.Wet;
        if (amount <= 0)
            return read;
        // From 48 kHz and 16 bits at no amount down to ~4.4 kHz and 4 bits at full.
        int hold = Math.Max(1, (int)MathF.Round(_rate * (1f + 10f * amount) / 48000f));
        float steps = MathF.Pow(2f, 15f - 12f * amount);
        for (int i = 0; i < read; i += _channels)
        {
            if (_left == 0)
            {
                for (int c = 0; c < _channels && i + c < read; c++)
                    _held[c] = MathF.Round(buffer[offset + i + c] * steps) / steps;
                _left = hold;
            }
            _left--;
            for (int c = 0; c < _channels && i + c < read; c++)
                buffer[offset + i + c] = _held[c];
        }
        return read;
    }
}

/// <summary>Behind a door: two low-pass stages, from 12 kHz at no amount down to 300 Hz at full, and a bit quieter.</summary>
internal sealed class MuffledEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly BiQuadFilter[] _first;
    private readonly BiQuadFilter[] _second;
    private float _designed = -1; // amount the filters are set for

    public MuffledEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        int rate = source.WaveFormat.SampleRate;
        int channels = source.WaveFormat.Channels;
        _first = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.LowPassFilter(rate, 1000, 0.707f)).ToArray();
        _second = Enumerable.Range(0, channels).Select(_ => BiQuadFilter.LowPassFilter(rate, 1000, 0.707f)).ToArray();
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = _first.Length;
        float amount = _control.Wet;
        if (amount != _designed)
        {
            _designed = amount;
            int rate = _source.WaveFormat.SampleRate;
            float hz = SoundEffects.Cutoff(12000, 300, amount, rate);
            foreach (var filter in _first.Concat(_second))
                filter.SetLowPassFilter(rate, hz, 0.707f);
        }
        float level = 1f - 0.4f * amount;
        for (int i = 0; i < read; i++)
        {
            int c = i % channels;
            buffer[offset + i] = _second[c].Transform(_first[c].Transform(buffer[offset + i])) * level;
        }
        return read;
    }
}

/// <summary>Under water: a dark resonant low-pass (4 kHz down to 250 Hz) and the pitch wobbling, read from a swinging delay.</summary>
internal sealed class UnderwaterEffect : ISampleProvider
{
    private const double WobbleHz = 2.5;
    private const double WobbleSeconds = 0.003; // how far the read head swings at full amount

    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly BiQuadFilter[] _lowPass;
    private readonly DelayLine _line;
    private double _phase;
    private float _designed = -1; // amount the filter is set for

    public UnderwaterEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        var format = source.WaveFormat;
        _lowPass = Enumerable.Range(0, format.Channels).Select(_ => BiQuadFilter.LowPassFilter(format.SampleRate, 1000, 1.2f)).ToArray();
        _line = new DelayLine(format.Channels, (int)(format.SampleRate * WobbleSeconds * 2) + 4);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        int channels = _lowPass.Length;
        int rate = _source.WaveFormat.SampleRate;
        float amount = _control.Wet;
        if (amount != _designed)
        {
            _designed = amount;
            float hz = SoundEffects.Cutoff(4000, 250, amount, rate);
            foreach (var filter in _lowPass)
                filter.SetLowPassFilter(rate, hz, 1.2f);
        }
        double depth = rate * WobbleSeconds * amount;
        double step = 2 * Math.PI * WobbleHz / rate;
        for (int i = 0; i + channels <= read; i += channels)
        {
            _line.Advance();
            for (int c = 0; c < channels; c++)
                _line.Set(c, _lowPass[c].Transform(buffer[offset + i + c]));

            // One to 1 + 2 * depth frames behind what was just written: the swing bends the pitch up and down.
            double delay = 1 + depth * (1 + Math.Sin(_phase));
            for (int c = 0; c < channels; c++)
                buffer[offset + i + c] = _line.Read(c, delay);

            _phase = Lfo.Step(_phase, step);
        }
        return read;
    }
}

/// <summary>
/// Interleaved frames read back at a fractional distance behind the newest one (linear interpolation);
/// under water, chorus, flanger and vibrato swing that distance to bend the pitch.
/// </summary>
internal sealed class DelayLine(int channels, int frames)
{
    private readonly float[] _line = new float[channels * frames];
    private int _newest;

    /// <summary>Longest delay that can be read, in frames.</summary>
    public double MaxDelay => frames - 2;

    /// <summary>Moves on to a new frame; its samples are then written with <see cref="Set"/>.</summary>
    public void Advance() => _newest = (_newest + 1) % frames;

    public void Set(int channel, float sample) => _line[_newest * channels + channel] = sample;

    /// <summary>The sample <paramref name="delay"/> frames behind the newest; at least 1 before the newest is set.</summary>
    public float Read(int channel, double delay)
    {
        double position = _newest - Math.Clamp(delay, 0, MaxDelay);
        if (position < 0)
            position += frames;
        int a = (int)position, b = (a + 1) % frames;
        float t = (float)(position - a);
        float first = _line[a * channels + channel];
        return first + (_line[b * channels + channel] - first) * t;
    }
}

/// <summary>Phase of a slow sine (radians), wrapped so it never loses precision.</summary>
internal static class Lfo
{
    public static double Step(double phase, double step)
    {
        phase += step;
        return phase > 2 * Math.PI ? phase - 2 * Math.PI : phase;
    }
}

/// <summary>
/// A big hall: Freeverb's eight damped combs and four all-passes per channel (the right channel a little
/// longer, for width). More amount, a longer and louder hall; it rings on after the sound ends.
/// </summary>
internal sealed class ReverbEffect : ISampleProvider
{
    private const float TailSeconds = 4f;
    private const float FadeSeconds = 1f; // the last part of the tail fades so it never stops abruptly
    private const float Damping = 0.25f;
    private static readonly int[] CombLengths = [1116, 1188, 1277, 1356, 1422, 1491, 1557, 1617];
    private static readonly int[] AllPassLengths = [556, 441, 341, 225];
    private const int Spread = 23;

    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly int _channels;
    private readonly float[][] _combs;     // [channel * 8 + comb]
    private readonly int[] _combPos;
    private readonly float[] _combStore;
    private readonly float[][] _allPasses; // [channel * 4 + all-pass]
    private readonly int[] _allPassPos;
    private readonly int _tail;
    private readonly int _fade;
    private int _tailLeft = -1;

    public ReverbEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        var format = source.WaveFormat;
        _channels = format.Channels;
        double scale = format.SampleRate / 44100.0;
        int Length(int baseLength, int channel) => Math.Max(1, (int)((baseLength + (channel % 2) * Spread) * scale));
        _combs = Enumerable.Range(0, _channels * CombLengths.Length)
            .Select(k => new float[Length(CombLengths[k % CombLengths.Length], k / CombLengths.Length)]).ToArray();
        _combPos = new int[_combs.Length];
        _combStore = new float[_combs.Length];
        _allPasses = Enumerable.Range(0, _channels * AllPassLengths.Length)
            .Select(k => new float[Length(AllPassLengths[k % AllPassLengths.Length], k / AllPassLengths.Length)]).ToArray();
        _allPassPos = new int[_allPasses.Length];
        _tail = (int)(TailSeconds * format.SampleRate) * _channels;
        _fade = (int)(FadeSeconds * format.SampleRate) * _channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _tailLeft < 0 ? _source.Read(buffer, offset, count) : 0;
        if (read < count && _tailLeft < 0)
            _tailLeft = _tail;

        // Past the end of the sound, keep feeding silence until the hall has died away.
        int total = read;
        if (read < count && _tailLeft > 0)
        {
            int extra = Math.Min(count - read, _tailLeft);
            Array.Clear(buffer, offset + read, extra);
            _tailLeft -= extra;
            total += extra;
        }

        float amount = _control.Wet;
        float feedback = 0.7f + 0.25f * amount;
        for (int i = 0; i < total; i++)
        {
            float dry = buffer[offset + i];
            float output = dry * (1f - 0.3f * amount) + Hall(i % _channels, dry * 0.015f, feedback) * HallLevel * amount;
            int left = _tailLeft + (total - i); // tail samples still to come, past the end of the sound
            if (i >= read && left < _fade)
                output *= (float)left / _fade;
            buffer[offset + i] = output;
        }
        return total;
    }

    private const float HallLevel = 2.5f;

    private float Hall(int channel, float input, float feedback)
    {
        float sum = 0;
        for (int k = channel * CombLengths.Length; k < (channel + 1) * CombLengths.Length; k++)
        {
            var line = _combs[k];
            float late = line[_combPos[k]];
            _combStore[k] = late * (1f - Damping) + _combStore[k] * Damping;
            line[_combPos[k]] = input + _combStore[k] * feedback;
            if (++_combPos[k] == line.Length)
                _combPos[k] = 0;
            sum += late;
        }
        for (int k = channel * AllPassLengths.Length; k < (channel + 1) * AllPassLengths.Length; k++)
        {
            var line = _allPasses[k];
            float stored = line[_allPassPos[k]];
            line[_allPassPos[k]] = sum + stored * 0.5f;
            if (++_allPassPos[k] == line.Length)
                _allPassPos[k] = 0;
            sum = stored - sum;
        }
        return sum;
    }
}

/// <summary>Three copies 20-30 ms late, each swinging ±2 ms at 0.8 Hz a third apart: several voices together.</summary>
internal sealed class ChorusEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private const double RateHz = 0.8;
    private const double SwingSeconds = 0.002;
    private static readonly double[] BaseSeconds = [0.020, 0.025, 0.030];

    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private readonly DelayLine _line = new(source.WaveFormat.Channels, (int)(source.WaveFormat.SampleRate * 0.04) + 4);
    private double _phase;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float wet = control.Wet;
        double step = 2 * Math.PI * RateHz / _rate;
        for (int i = 0; i + _channels <= read; i += _channels)
        {
            _line.Advance();
            for (int c = 0; c < _channels; c++)
                _line.Set(c, buffer[offset + i + c]);
            for (int c = 0; c < _channels; c++)
            {
                float voices = 0;
                for (int v = 0; v < BaseSeconds.Length; v++)
                {
                    double swing = Math.Sin(_phase + v * 2 * Math.PI / BaseSeconds.Length);
                    voices += _line.Read(c, _rate * (BaseSeconds[v] + SwingSeconds * swing));
                }
                float dry = buffer[offset + i + c];
                buffer[offset + i + c] = dry * (1f - 0.4f * wet) + voices / BaseSeconds.Length * 0.9f * wet;
            }
            _phase = Lfo.Step(_phase, step);
        }
        return read;
    }
}

/// <summary>Jet: a copy 0.5-4.5 ms late sweeping at 0.25 Hz, fed back into itself; more amount, more feedback and whoosh.</summary>
internal sealed class FlangerEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private const double RateHz = 0.25;

    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private readonly DelayLine _line = new(source.WaveFormat.Channels, (int)(source.WaveFormat.SampleRate * 0.006) + 4);
    private double _phase;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float wet = control.Wet;
        float feedback = 0.5f * wet;
        double step = 2 * Math.PI * RateHz / _rate;
        for (int i = 0; i + _channels <= read; i += _channels)
        {
            // Read before this frame is set: the newest slot still holds an old frame, so stay behind it.
            double delay = 1 + _rate * (0.0005 + 0.002 * (1 + Math.Sin(_phase)));
            _line.Advance();
            for (int c = 0; c < _channels; c++)
            {
                float dry = buffer[offset + i + c];
                float late = _line.Read(c, delay);
                _line.Set(c, dry + late * feedback);
                buffer[offset + i + c] = (dry + late * wet) / (1f + 0.5f * wet);
            }
            _phase = Lfo.Step(_phase, step);
        }
        return read;
    }
}

/// <summary>Bleating: the pitch shaking at 6.5 Hz (up to ~5% each way) and the level dipping with it.</summary>
internal sealed class VibratoEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private const double RateHz = 6.5;
    private const double SwingSeconds = 0.0012; // at full amount

    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private readonly DelayLine _line = new(source.WaveFormat.Channels, (int)(source.WaveFormat.SampleRate * SwingSeconds * 2) + 4);
    private double _phase;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float wet = control.Wet;
        double depth = _rate * SwingSeconds * wet;
        double step = 2 * Math.PI * RateHz / _rate;
        for (int i = 0; i + _channels <= read; i += _channels)
        {
            _line.Advance();
            for (int c = 0; c < _channels; c++)
                _line.Set(c, buffer[offset + i + c]);
            double swing = Math.Sin(_phase);
            float level = 1f - 0.35f * wet * (float)(1 + swing) / 2;
            for (int c = 0; c < _channels; c++)
                buffer[offset + i + c] = _line.Read(c, 1 + depth * (1 + swing)) * level;
            _phase = Lfo.Step(_phase, step);
        }
        return read;
    }
}

/// <summary>
/// Walkie-talkie: a narrow, driven radio band with hiss while someone talks; a quarter of a second after
/// the voice stops comes the roger beep and a burst of static. Plays that tail after the sound ends too.
/// </summary>
internal sealed class WalkieTalkieEffect : ISampleProvider
{
    private const float TailSeconds = 0.8f; // silence fed in after the sound: enough for the gate to close and the tail to play

    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly int _channels;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;
    private readonly WalkieGate _gate;
    private readonly int _tail;
    private int _tailLeft = -1;

    public WalkieTalkieEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        int rate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        _highPass = Enumerable.Range(0, _channels).Select(_ => BiQuadFilter.HighPassFilter(rate, 500, 0.8f)).ToArray();
        _lowPass = Enumerable.Range(0, _channels).Select(_ => BiQuadFilter.LowPassFilter(rate, 2500, 0.8f)).ToArray();
        _gate = new WalkieGate(rate);
        _tail = (int)(TailSeconds * rate) * _channels;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _tailLeft < 0 ? _source.Read(buffer, offset, count) : 0;
        if (read < count && _tailLeft < 0)
            _tailLeft = _tail;
        int total = read;
        if (read < count && _tailLeft > 0)
        {
            int extra = Math.Min(count - read, _tailLeft);
            Array.Clear(buffer, offset + read, extra);
            _tailLeft -= extra;
            total += extra;
        }

        float wet = _control.Wet;
        float drive = 2f + 4f * wet;
        float level = 0.6f / MathF.Tanh(drive * 0.4f);
        for (int i = 0; i + _channels <= total; i += _channels)
        {
            float peak = 0;
            for (int c = 0; c < _channels; c++)
                peak = Math.Max(peak, Math.Abs(buffer[offset + i + c]));
            float extra = _gate.Next(peak, out bool talking);
            for (int c = 0; c < _channels; c++)
            {
                float dry = buffer[offset + i + c];
                float band = _lowPass[c].Transform(_highPass[c].Transform(dry));
                float radio = MathF.Tanh(band * drive) * level + (talking ? _gate.Noise() * 0.03f : 0f);
                buffer[offset + i + c] = dry + (radio - dry) * wet + extra * wet;
            }
        }
        return total;
    }
}

/// <summary>
/// The walkie-talkie's ear: follows the voice's level and, once it has been quiet for a quarter of a second
/// after talking, plays the roger beep (1.4 kHz, 80 ms) and a fading burst of static (150 ms).
/// The microphone's copy in VoiceFx.cpp does the same with the same numbers.
/// </summary>
internal sealed class WalkieGate(int rate)
{
    private const float Threshold = 0.02f;
    private readonly float _release = MathF.Exp(-1f / (0.03f * rate));
    private readonly int _hang = rate / 4;
    private readonly int _beep = (int)(rate * 0.08);
    private readonly int _gap = (int)(rate * 0.02);
    private readonly int _static = (int)(rate * 0.15);
    private float _envelope;
    private bool _talked;
    private int _quiet;
    private int _tail = -1; // frames into the tail, -1 when none plays
    private uint _noise = 0x12345678;

    /// <summary>The tail's sample for this frame (0 when none plays); <paramref name="talking"/> while the voice is on.</summary>
    public float Next(float peak, out bool talking)
    {
        _envelope = Math.Max(peak, _envelope * _release);
        if (_envelope > Threshold)
        {
            _talked = true;
            _quiet = 0;
        }
        else if (_talked && ++_quiet > _hang)
        {
            _talked = false;
            _tail = 0;
        }
        talking = _talked && _quiet == 0;

        if (_tail < 0)
            return 0;
        int at = _tail++;
        if (_tail >= _beep + _gap + _static)
            _tail = -1;
        if (at < _beep)
            return 0.2f * MathF.Sin(2 * MathF.PI * 1400f * at / rate);
        if (at < _beep + _gap)
            return 0;
        return Noise() * 0.25f * (1f - (float)(at - _beep - _gap) / _static);
    }

    /// <summary>White noise in -1..1 (xorshift), the same sequence as the microphone's.</summary>
    public float Noise()
    {
        _noise ^= _noise << 13;
        _noise ^= _noise >> 17;
        _noise ^= _noise << 5;
        return (int)_noise / 2147483648f;
    }
}

/// <summary>Passes the source through, keeping a copy of the last block read (index for index).</summary>
internal sealed class TeeProvider(ISampleProvider source) : ISampleProvider
{
    public float[] Last { get; private set; } = [];

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        if (Last.Length < read)
            Last = new float[read];
        Array.Copy(buffer, offset, Last, 0, read);
        return read;
    }
}

/// <summary>Demon: the voice much lower (0.65 at the default amount), doubled with the original and growling.</summary>
internal sealed class DemonEffect : ISampleProvider
{
    private readonly TeeProvider _dry;
    private readonly SmbPitchShiftingSampleProvider _shifter;
    private readonly EffectControl _control;

    public DemonEffect(ISampleProvider source, EffectControl control)
    {
        _dry = new TeeProvider(source);
        _shifter = new SmbPitchShiftingSampleProvider(_dry);
        _control = control;
    }

    public WaveFormat WaveFormat => _shifter.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        float wet = _control.Wet;
        _shifter.PitchFactor = 1f - 0.5f * wet;
        int read = _shifter.Read(buffer, offset, count);
        var dry = _dry.Last;
        float drive = 1f + 3f * wet;
        float level = 0.8f / MathF.Tanh(drive);
        for (int i = 0; i < read; i++)
        {
            float both = buffer[offset + i] + 0.6f * wet * dry[i];
            buffer[offset + i] = both + (MathF.Tanh(both * drive) * level - both) * wet;
        }
        return read;
    }
}

/// <summary>Alien: after a small pitch up, a ring modulator whose carrier sweeps 200-800 Hz at 0.4 Hz.</summary>
internal sealed class AlienEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private double _carrier;
    private double _sweep;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        float mix = 0.6f * control.Wet;
        double sweepStep = 2 * Math.PI * 0.4 / _rate;
        for (int i = 0; i < read; i += _channels)
        {
            float carrier = (float)Math.Sin(_carrier) * 1.4f;
            _carrier = Lfo.Step(_carrier, 2 * Math.PI * (500 + 300 * Math.Sin(_sweep)) / _rate);
            _sweep = Lfo.Step(_sweep, sweepStep);
            for (int c = 0; c < _channels && i + c < read; c++)
            {
                float dry = buffer[offset + i + c];
                buffer[offset + i + c] = dry + (dry * carrier - dry) * mix;
            }
        }
        return read;
    }
}

/// <summary>Uniform noise from xorshift; the effects seed it the same way as the microphone, so both sound alike.</summary>
internal struct NoiseSource()
{
    private uint _state = 0x12345678;

    /// <summary>-1..1.</summary>
    public float Next()
    {
        _state ^= _state << 13;
        _state ^= _state >> 17;
        _state ^= _state << 5;
        return (int)_state / 2147483648f;
    }
}

/// <summary>
/// The microphone's delay-line pitch shifter (VoiceFx::PitchShift) for the autotune: two taps half a window
/// apart slide through the recent past at (1 - factor) frames per frame, each fading in and out.
/// Unlike the phase-vocoder shifter it follows a factor that changes every few milliseconds cleanly;
/// its 20 ms window (not the usual 35) keeps the latency close to the detector's, so corrections land on time.
/// </summary>
internal sealed class DelayLineShifter(int channels, int rate)
{
    private const int LineFrames = 8192; // power of two, > window at any common rate

    private readonly float[] _line = new float[LineFrames * channels];
    private readonly double _window = Math.Min(rate * 0.02, LineFrames / 2.0);
    private int _write;
    private double _phase;

    public void Process(float[] buffer, int offset, int frames, float factor)
    {
        const int mask = LineFrames - 1;
        double step = (1.0 - factor) / _window;
        for (int f = 0; f < frames; f++)
        {
            int at = offset + f * channels;
            for (int c = 0; c < channels; c++)
                _line[_write * channels + c] = buffer[at + c];

            double second = _phase + 0.5 >= 1 ? _phase - 0.5 : _phase + 0.5;
            for (int c = 0; c < channels; c++)
                buffer[at + c] = Tap(_phase, c) + Tap(second, c);

            _phase += step;
            if (_phase >= 1)
                _phase -= 1;
            if (_phase < 0)
                _phase += 1;
            _write = (_write + 1) & mask;
        }

        float Tap(double phase, int c)
        {
            double read = _write - (2.0 + phase * _window);
            double floor = Math.Floor(read);
            float t = (float)(read - floor);
            int i0 = (int)((long)floor & mask), i1 = (i0 + 1) & mask;
            float a = _line[i0 * channels + c], b = _line[i1 * channels + c];
            float gain = (float)(1.0 - Math.Abs(2.0 * phase - 1.0));
            return (a + (b - a) * t) * gain;
        }
    }
}

/// <summary>
/// Finds the pitch of a voice every ~5 ms with YIN on a ~12 kHz mono copy (70-1000 Hz); 0 when it
/// hears no clear tone. The microphone runs the same in VoiceFx.cpp.
/// </summary>
internal sealed class PitchDetector
{
    private const int Window = 512;
    private const int Half = 256;
    private const int Hop = 64;
    private const double Threshold = 0.15;

    private readonly int _decimate;
    private readonly double _rate;
    private readonly float[] _window = new float[Window];
    private readonly float[] _difference = new float[Half];
    private float _sum;
    private int _summed;
    private int _filled;

    public PitchDetector(int rate)
    {
        _decimate = Math.Max(1, (int)Math.Round(rate / 12000.0, MidpointRounding.AwayFromZero));
        _rate = rate / (double)_decimate;
    }

    /// <summary>The last estimate in Hz, 0 for no tone.</summary>
    public double Hz { get; private set; }

    /// <summary>Takes one frame (its channels averaged); true when a new estimate is ready.</summary>
    public bool Push(float sample)
    {
        _sum += sample;
        if (++_summed < _decimate)
            return false;
        _window[Window - Hop + _filled] = _sum / _decimate;
        _sum = 0;
        _summed = 0;
        if (++_filled < Hop)
            return false;
        _filled = 0;
        Hz = Analyse();
        Array.Copy(_window, Hop, _window, 0, Window - Hop);
        return true;
    }

    private double Analyse()
    {
        double energy = 0;
        for (int j = Window - Half; j < Window; j++)
            energy += _window[j] * _window[j];
        if (Math.Sqrt(energy / Half) < 0.005)
            return 0;

        int tauMin = (int)(_rate / 1000), tauMax = Math.Min(Half - 1, (int)(_rate / 70));
        // Cumulative mean normalised difference: dips towards 0 at the period. The newest half of the
        // window is compared with what came a lag before, so the estimate is fresh.
        double running = 0;
        _difference[0] = 1;
        for (int tau = 1; tau <= tauMax; tau++)
        {
            double d = 0;
            for (int j = Window - Half; j < Window; j++)
            {
                double delta = _window[j] - _window[j - tau];
                d += delta * delta;
            }
            running += d;
            _difference[tau] = running > 0 ? (float)(d * tau / running) : 1;
        }
        int best = -1;
        for (int tau = Math.Max(2, tauMin); tau < tauMax; tau++)
        {
            if (_difference[tau] >= Threshold)
                continue;
            while (tau + 1 < tauMax && _difference[tau + 1] < _difference[tau])
                tau++;
            best = tau;
            break;
        }
        if (best < 0)
            return 0;
        double a = _difference[best - 1], b = _difference[best], c = _difference[best + 1];
        double bend = a - 2 * b + c;
        double shift = bend > 0 ? (a - c) / (2 * bend) : 0;
        return _rate / (best + shift);
    }
}

/// <summary>Shared tuning of the autotune, so sounds and microphone snap the same way.</summary>
internal static class Autotune
{
    /// <summary>Estimates without a tone (~60 ms) before letting go of the note: a consonant or a breath keeps it.</summary>
    public const int Hold = 12;

    /// <summary>No note chosen yet.</summary>
    public const int NoNote = int.MinValue;

    // From half the amount up only the C major pentatonic (C D E G A) counts: jumps of up to three
    // semitones, the stepping sound. Below, every semitone.
    private const float ScaleFrom = 0.5f;
    private static readonly bool[] Pentatonic = [true, false, true, false, true, false, false, true, false, true, false, false];

    // A pitch has to be this much nearer (in semitones) to another note before it leaves its own:
    // without it a voice sitting between two notes would flip back and forth.
    private const double Stickiness = 0.2;

    /// <summary>
    /// Factor that moves <paramref name="hz"/> (above 0) to its note, as strong as the amount wants;
    /// <paramref name="note"/> (semitones from A 440) is the note held so far and is updated.
    /// </summary>
    public static float Target(double hz, float amount, ref int note)
    {
        double pitch = 12 * Math.Log2(hz / 440);
        bool scale = amount >= ScaleFrom;
        int nearest = Nearest(pitch, scale);
        if (note == NoNote || !Allowed(note, scale) || Math.Abs(pitch - nearest) + Stickiness < Math.Abs(pitch - note))
            note = nearest;
        double ratio = Math.Pow(2, (note - pitch) / 12);
        return (float)Math.Pow(ratio, Math.Min(1f, 2f * amount));
    }

    private static bool Allowed(int note, bool scale) => !scale || Pentatonic[((note - 3) % 12 + 12) % 12]; // C is 3 above A

    private static int Nearest(double pitch, bool scale)
    {
        int best = NoNote;
        for (int candidate = (int)Math.Floor(pitch) - 3; candidate <= (int)Math.Ceiling(pitch) + 3; candidate++)
        {
            if (Allowed(candidate, scale) && (best == NoNote || Math.Abs(pitch - candidate) < Math.Abs(pitch - best)))
                best = candidate;
        }
        return best;
    }

    /// <summary>How fast the correction moves (seconds): slow and natural at little amount, a hard snap at full.</summary>
    public static double Glide(float amount) => 0.001 + 0.1 * (1 - amount) * (1 - amount) * (1 - amount);
}

/// <summary>
/// Autotune: the pitch pulled to the nearest semitone, or from half the amount to the nearest note of the
/// C major pentatonic; more amount, a stronger and quicker pull.
/// </summary>
internal sealed class AutotuneEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _rate = source.WaveFormat.SampleRate;
    private readonly PitchDetector _detector = new(source.WaveFormat.SampleRate);
    private readonly DelayLineShifter _shifter = new(source.WaveFormat.Channels, source.WaveFormat.SampleRate);
    private float _target = 1f;
    private float _factor = 1f;
    private int _misses;
    private int _note = Autotune.NoNote;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        int frames = read / _channels;
        float wet = control.Wet;
        for (int f = 0; f < frames; f++)
        {
            float mono = 0;
            for (int c = 0; c < _channels; c++)
                mono += buffer[offset + f * _channels + c];
            if (!_detector.Push(mono / _channels))
                continue;
            if (_detector.Hz > 0)
            {
                _misses = 0;
                _target = Autotune.Target(_detector.Hz, wet, ref _note);
            }
            else if (++_misses >= Autotune.Hold)
            {
                _target = 1f; // no tone for a while: back to the voice as it is
                _note = Autotune.NoNote;
            }
        }
        _factor += (_target - _factor) * (float)(1 - Math.Exp(-frames / (Autotune.Glide(wet) * _rate)));
        _shifter.Process(buffer, offset, frames, _factor);
        return read;
    }
}

/// <summary>
/// Whisper: short slices (~10 ms, a quarter apart) keep their spectrum but get random phases, so the
/// words stay and the tone goes; a high-pass makes it airy. Comes out ~10 ms late, the end trimmed by as much.
/// </summary>
internal sealed class WhisperEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly int _channels;
    private readonly int _size;
    private readonly int _hop;
    private readonly Editing.Fft _fft;    // the editor's; its inverse does not divide by the size
    private readonly float[] _window;
    private readonly float[][] _history;  // per channel, the last _size input samples
    private readonly float[][] _overlap;  // per channel, output being added up
    private readonly float[][] _ready;    // per channel, the next _hop output samples
    private readonly double[] _re;
    private readonly double[] _im;
    private readonly BiQuadFilter[] _airy;
    private NoiseSource _noise = new();
    private int _filled;

    public WhisperEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        int rate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        _size = WhisperSize(rate);
        _hop = _size / 4;
        _fft = new Editing.Fft(_size);
        // Hann, and the overlap of four Hann-squared windows sums to 1.5: divided out here once.
        _window = Enumerable.Range(0, _size).Select(i => (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * i / _size))).ToArray();
        _history = Enumerable.Range(0, _channels).Select(_ => new float[_size]).ToArray();
        _overlap = Enumerable.Range(0, _channels).Select(_ => new float[_size]).ToArray();
        _ready = Enumerable.Range(0, _channels).Select(_ => new float[_hop]).ToArray();
        _re = new double[_size];
        _im = new double[_size];
        _airy = Enumerable.Range(0, _channels).Select(_ => BiQuadFilter.HighPassFilter(rate, 250, 0.7f)).ToArray();
    }

    /// <summary>Power of two closest to 10.7 ms (512 at 48 kHz).</summary>
    internal static int WhisperSize(int rate)
    {
        int size = 64;
        while (size * 2 <= rate * 0.0107 * 1.42)
            size *= 2;
        return size;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float wet = _control.Wet;
        for (int i = 0; i + _channels <= read; i += _channels)
        {
            for (int c = 0; c < _channels; c++)
            {
                float dry = buffer[offset + i + c];
                _history[c][_size - _hop + _filled] = dry;
                float whisper = _airy[c].Transform(_ready[c][_filled]);
                buffer[offset + i + c] = dry * (1f - wet) + whisper * 1.4f * wet;
            }
            if (++_filled == _hop)
            {
                _filled = 0;
                for (int c = 0; c < _channels; c++)
                    Slice(c);
            }
        }
        return read;
    }

    private void Slice(int channel)
    {
        var history = _history[channel];
        for (int i = 0; i < _size; i++)
        {
            _re[i] = history[i] * _window[i];
            _im[i] = 0;
        }
        _fft.Transform(_re, _im, inverse: false);
        for (int k = 1; k < _size / 2; k++)
        {
            double magnitude = Math.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            double phase = _noise.Next() * Math.PI;
            _re[k] = magnitude * Math.Cos(phase);
            _im[k] = magnitude * Math.Sin(phase);
            _re[_size - k] = _re[k];
            _im[_size - k] = -_im[k];
        }
        _im[0] = _im[_size / 2] = 0;
        _fft.Transform(_re, _im, inverse: true);

        var overlap = _overlap[channel];
        for (int i = 0; i < _size; i++)
            overlap[i] += (float)(_re[i] / _size) * _window[i] / 1.5f;
        Array.Copy(overlap, 0, _ready[channel], 0, _hop);
        Array.Copy(overlap, _hop, overlap, 0, _size - _hop);
        Array.Clear(overlap, _size - _hop, _hop);
        Array.Copy(history, _hop, history, 0, _size - _hop);
    }
}

/// <summary>
/// Old record: a narrow band (120 Hz up to 8 kHz at no amount, 2.5 kHz at full), the slow wow and quick
/// flutter of an uneven turntable, and crackle and hiss that grow with the amount.
/// </summary>
internal sealed class VinylEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly EffectControl _control;
    private readonly int _channels;
    private readonly int _rate;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;
    private readonly DelayLine _line;
    private readonly float _clickDecay;
    private NoiseSource _noise = new();
    private float _designed = -1;
    private double _wow;
    private double _flutter;
    private float _click;

    public VinylEffect(ISampleProvider source, EffectControl control)
    {
        _source = source;
        _control = control;
        _rate = source.WaveFormat.SampleRate;
        _channels = source.WaveFormat.Channels;
        _highPass = Enumerable.Range(0, _channels).Select(_ => BiQuadFilter.HighPassFilter(_rate, 120, 0.707f)).ToArray();
        _lowPass = Enumerable.Range(0, _channels).Select(_ => BiQuadFilter.LowPassFilter(_rate, 1000, 0.707f)).ToArray();
        _line = new DelayLine(_channels, (int)(_rate * 0.004) + 4);
        _clickDecay = MathF.Exp(-1f / (0.0008f * _rate));
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = _source.Read(buffer, offset, count);
        float amount = _control.Wet;
        if (amount != _designed)
        {
            _designed = amount;
            float hz = SoundEffects.Cutoff(8000, 2500, amount, _rate);
            foreach (var filter in _lowPass)
                filter.SetLowPassFilter(_rate, hz, 0.707f);
        }
        float clicksPerFrame = (2f + 30f * amount) / _rate;
        for (int i = 0; i + _channels <= read; i += _channels)
        {
            _line.Advance();
            for (int c = 0; c < _channels; c++)
                _line.Set(c, _lowPass[c].Transform(_highPass[c].Transform(buffer[offset + i + c])));
            double delay = 1 + _rate * (0.0015 * amount * (1 + Math.Sin(_wow)) + 0.00012 * amount * (1 + Math.Sin(_flutter)));
            _wow = Lfo.Step(_wow, 2 * Math.PI * 0.55 / _rate);
            _flutter = Lfo.Step(_flutter, 2 * Math.PI * 7 / _rate);

            if ((_noise.Next() + 1) / 2 < clicksPerFrame)
                _click = (_noise.Next() < 0 ? -1 : 1) * (0.05f + 0.25f * Math.Abs(_noise.Next())) * amount;
            float crackle = _click + _noise.Next() * 0.004f * amount;
            _click *= _clickDecay;
            for (int c = 0; c < _channels; c++)
                buffer[offset + i + c] = _line.Read(c, delay) + crackle;
        }
        return read;
    }
}

/// <summary>
/// Stutter: every 0.6 s an 80 ms piece is played 2 to 4 times (more with the amount) before the sound
/// goes on, so it gets longer; quiet pieces are not repeated. Repeats fade in and out over 3 ms.
/// The microphone, which cannot fall behind, repeats over what is said meanwhile instead.
/// </summary>
internal sealed class StutterEffect(ISampleProvider source, EffectControl control) : ISampleProvider
{
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly int _period = (int)(source.WaveFormat.SampleRate * 0.6);
    private readonly int _fade = Math.Max(1, (int)(source.WaveFormat.SampleRate * 0.003));
    private readonly float[] _grain = new float[(int)(source.WaveFormat.SampleRate * 0.08) * source.WaveFormat.Channels];
    private int _grainFrames;  // frames in the piece being repeated
    private int _plays;        // times the piece is played in all
    private int _play;         // which play is going on
    private int _position;     // frame within the piece
    private int _passLeft;     // frames to pass through before the next piece
    private bool _ended;

    public WaveFormat WaveFormat => source.WaveFormat;

    /// <summary>Extra plays of a loud piece: 1 at little amount, 2 at the default, 3 at full.</summary>
    internal static int Repeats(float amount) => amount <= 0 ? 0 : 1 + (int)MathF.Round(2 * amount, MidpointRounding.AwayFromZero);

    public int Read(float[] buffer, int offset, int count)
    {
        int wanted = count / _channels, written = 0;
        while (written < wanted)
        {
            if (_play < _plays)
            {
                int at = offset + written * _channels;
                float fadeIn = _play > 0 ? Math.Min(1f, _position / (float)_fade) : 1f;
                float fadeOut = _play < _plays - 1 ? Math.Min(1f, (_grainFrames - _position) / (float)_fade) : 1f;
                float gain = Math.Min(fadeIn, fadeOut);
                for (int c = 0; c < _channels; c++)
                    buffer[at + c] = _grain[_position * _channels + c] * gain;
                written++;
                if (++_position == _grainFrames)
                {
                    _position = 0;
                    _play++;
                }
                continue;
            }
            if (_ended)
                break;
            if (_passLeft > 0)
            {
                int asked = Math.Min(_passLeft, wanted - written);
                int got = source.Read(buffer, offset + written * _channels, asked * _channels) / _channels;
                written += got;
                _passLeft -= got;
                if (got < asked)
                    _ended = true;
                continue;
            }
            Capture();
        }
        return written * _channels;
    }

    // Reads the next piece and decides how many times it plays.
    private void Capture()
    {
        int filled = 0;
        while (filled < _grain.Length)
        {
            int got = source.Read(_grain, filled, _grain.Length - filled);
            if (got == 0)
            {
                _ended = true;
                break;
            }
            filled += got;
        }
        _grainFrames = filled / _channels;
        double energy = 0;
        for (int i = 0; i < _grainFrames * _channels; i++)
            energy += _grain[i] * _grain[i];
        bool loud = _grainFrames > 0 && Math.Sqrt(energy / (_grainFrames * _channels)) > 0.02;
        _plays = _grainFrames == 0 ? 0 : 1 + (loud && !_ended ? Repeats(control.Wet) : 0);
        _play = 0;
        _position = 0;
        _passLeft = _period - _grainFrames;
    }
}

/// <summary>
/// Backwards: reads the rest of the sound (up to a minute) on a worker thread, then plays it from the
/// end. Silent while that runs, a few milliseconds for a short sound. Sounds only: a live voice cannot
/// be reversed.
/// </summary>
internal sealed class ReverseEffect : ISampleProvider
{
    private const int MaxSeconds = 60;

    private readonly ISampleProvider _source;
    private readonly int _channels;
    private float[] _samples = [];
    private int _total;
    private int _left;          // samples still to play, counting down from the end
    private volatile bool _loaded;

    public ReverseEffect(ISampleProvider source)
    {
        _source = source;
        _channels = source.WaveFormat.Channels;
        _ = Task.Run(Load);
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    /// <summary>Share of the reversed part already played, 0..1.</summary>
    public double Progress => _loaded && _total > 0 ? 1 - (double)Volatile.Read(ref _left) / _total : 0;

    /// <summary>Waits for the worker; for the tests.</summary>
    internal bool WaitLoaded(TimeSpan timeout) => SpinWait.SpinUntil(() => _loaded, timeout);

    private void Load()
    {
        var all = new List<float>();
        var block = new float[_source.WaveFormat.SampleRate * _channels / 10];
        long max = (long)MaxSeconds * _source.WaveFormat.SampleRate * _channels;
        try
        {
            int read;
            while (all.Count < max && (read = _source.Read(block, 0, block.Length)) > 0)
                all.AddRange(block.AsSpan(0, read));
        }
        catch (Exception)
        {
            // The sound was stopped and its file closed while loading: play what was read.
        }
        _total = all.Count - all.Count % _channels;
        _samples = [.. all];
        Volatile.Write(ref _left, _total);
        _loaded = true;
    }

    public int Read(float[] buffer, int offset, int count)
    {
        if (!_loaded)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }
        int frames = Math.Min(count / _channels, _left / _channels);
        for (int f = 0; f < frames; f++)
        {
            int from = _left - (f + 1) * _channels; // the frame just before what is left, channels in order
            Array.Copy(_samples, from, buffer, offset + f * _channels, _channels);
        }
        Volatile.Write(ref _left, _left - frames * _channels);
        return frames * _channels;
    }
}
