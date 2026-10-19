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
            _ => source,
        };
    }
}

/// <summary>Pitch shift whose factor follows the amount while the sound plays.</summary>
internal sealed class PitchEffect(ISampleProvider source, EffectControl control, Func<float, float> factor) : ISampleProvider
{
    private readonly SmbPitchShiftingSampleProvider _shifter = new(source);

    public WaveFormat WaveFormat => _shifter.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        _shifter.PitchFactor = factor(control.Amount);
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
        float wet = control.Amount;
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
        float wet = _control.Amount;
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
        float amount = _control.Amount;
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
