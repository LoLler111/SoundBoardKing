using NAudio.Dsp;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Audio;

/// <summary>Builds the effect chain a sound plays through.</summary>
public static class SoundEffects
{
    public static ISampleProvider Apply(ISampleProvider source, SoundEffect effect) => effect switch
    {
        // About five semitones up or down: clearly changed, still understandable.
        SoundEffect.Chipmunk => new SmbPitchShiftingSampleProvider(source) { PitchFactor = 1.45f },
        SoundEffect.Deep => new SmbPitchShiftingSampleProvider(source) { PitchFactor = 0.72f },
        SoundEffect.Robot => new RobotEffect(source),
        SoundEffect.Radio => new RadioEffect(source),
        SoundEffect.Echo => new EchoEffect(source),
        _ => source,
    };
}

/// <summary>Ring modulation with a low sine: the classic metallic robot voice.</summary>
internal sealed class RobotEffect(ISampleProvider source) : ISampleProvider
{
    private const double Frequency = 55;
    private readonly int _channels = source.WaveFormat.Channels;
    private readonly double _step = 2 * Math.PI * Frequency / source.WaveFormat.SampleRate;
    private double _phase;

    public WaveFormat WaveFormat => source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int read = source.Read(buffer, offset, count);
        for (int i = 0; i < read; i += _channels)
        {
            float carrier = (float)Math.Sin(_phase);
            _phase += _step;
            if (_phase > 2 * Math.PI)
                _phase -= 2 * Math.PI;
            for (int c = 0; c < _channels && i + c < read; c++)
                buffer[offset + i + c] *= carrier * 1.4f;
        }
        return read;
    }
}

/// <summary>Telephone band (300 Hz - 3.4 kHz) with a little saturation.</summary>
internal sealed class RadioEffect : ISampleProvider
{
    private readonly ISampleProvider _source;
    private readonly BiQuadFilter[] _highPass;
    private readonly BiQuadFilter[] _lowPass;

    public RadioEffect(ISampleProvider source)
    {
        _source = source;
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
        for (int i = 0; i < read; i++)
        {
            int c = i % channels;
            float x = _lowPass[c].Transform(_highPass[c].Transform(buffer[offset + i]));
            buffer[offset + i] = MathF.Tanh(x * 2.5f) * 0.6f;
        }
        return read;
    }
}

/// <summary>Feedback delay; keeps sounding for a moment after the sound ends so the echo fades out.</summary>
internal sealed class EchoEffect : ISampleProvider
{
    private const float Delay = 0.28f;
    private const float Feedback = 0.45f;
    private const float Wet = 0.55f;
    private const float TailSeconds = 1.5f;

    private readonly ISampleProvider _source;
    private readonly float[] _line;
    private readonly int _tail;
    private int _position;
    private int _tailLeft = -1;

    public EchoEffect(ISampleProvider source)
    {
        _source = source;
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

        for (int i = offset; i < offset + total; i++)
        {
            float delayed = _line[_position];
            float dry = buffer[i];
            _line[_position] = dry + delayed * Feedback;
            buffer[i] = dry + delayed * Wet;
            if (++_position == _line.Length)
                _position = 0;
        }
        return total;
    }
}
