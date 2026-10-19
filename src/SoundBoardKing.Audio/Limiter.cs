using NAudio.Wave;

namespace SoundBoardKing.Audio;

/// <summary>Turns leveling on and off for every voice and limiter at once, also while they play.</summary>
internal sealed class LevelingSwitch
{
    private volatile bool _enabled = true;

    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }
}

/// <summary>
/// Look-ahead peak limiter that keeps a mix under <see cref="Ceiling"/> when several loud sounds
/// add up. The gain each frame needs is held for the look-ahead window and averaged over it, so it
/// is already down when that frame, delayed by the same window, comes out; then it recovers slowly.
/// Switched off it only delays the sound, so turning it on or off never clicks.
/// </summary>
internal sealed class Limiter : ISampleProvider
{
    /// <summary>-1 dBFS.</summary>
    public const float Ceiling = 0.891f;

    private const double LookaheadMs = 5;
    private const double ReleaseMs = 150;

    private readonly ISampleProvider _source;
    private readonly LevelingSwitch _switch;
    private readonly int _channels;
    private readonly int _window;
    private readonly float _release;

    // The input, delayed by window - 1 frames.
    private readonly float[] _delay;
    private int _delayPos;

    // Smallest gain asked for in the last window frames: a monotonic queue of (frame, gain).
    private readonly long[] _holdFrame;
    private readonly float[] _holdGain;
    private int _holdHead;
    private int _holdCount;

    // Moving average of the held gain over the window.
    private readonly float[] _average;
    private double _averageSum;
    private int _averagePos;

    private long _frame;
    private float _gain = 1f;
    private float[] _input = [];

    public Limiter(ISampleProvider source, LevelingSwitch levelingSwitch)
    {
        _source = source;
        _switch = levelingSwitch;
        _channels = source.WaveFormat.Channels;
        int rate = source.WaveFormat.SampleRate;
        _window = Math.Max(2, (int)(rate * LookaheadMs / 1000));
        _release = (float)(1 - Math.Exp(-1 / (rate * ReleaseMs / 1000)));
        _delay = new float[(_window - 1) * _channels];
        _holdFrame = new long[_window];
        _holdGain = new float[_window];
        _average = new float[_window];
        Array.Fill(_average, 1f);
        _averageSum = _window;
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_input.Length < count)
            _input = new float[count];
        int read = _source.Read(_input, 0, count);
        bool enabled = _switch.Enabled;

        for (int i = 0; i + _channels <= read; i += _channels)
        {
            float peak = 0;
            for (int ch = 0; ch < _channels; ch++)
                peak = Math.Max(peak, Math.Abs(_input[i + ch]));
            float wanted = enabled && peak > Ceiling ? Ceiling / peak : 1f;

            float held = Hold(wanted);
            _averageSum += held - _average[_averagePos];
            _average[_averagePos] = held;
            _averagePos = (_averagePos + 1) % _window;
            float target = (float)(_averageSum / _window);
            _gain = target < _gain ? target : _gain + (target - _gain) * _release;

            int slot = _delayPos * _channels;
            for (int ch = 0; ch < _channels; ch++)
            {
                float delayed = _delay[slot + ch];
                _delay[slot + ch] = _input[i + ch];
                buffer[offset + i + ch] = Math.Clamp(delayed * _gain, -1f, 1f);
            }
            _delayPos = (_delayPos + 1) % (_window - 1);
            _frame++;
        }
        return read;
    }

    private float Hold(float wanted)
    {
        // Drop the gain that left the window, then those that can no longer be the smallest.
        if (_holdCount > 0 && _holdFrame[_holdHead] <= _frame - _window)
        {
            _holdHead = (_holdHead + 1) % _window;
            _holdCount--;
        }
        while (_holdCount > 0 && _holdGain[(_holdHead + _holdCount - 1) % _window] >= wanted)
            _holdCount--;
        int tail = (_holdHead + _holdCount) % _window;
        _holdFrame[tail] = _frame;
        _holdGain[tail] = wanted;
        _holdCount++;
        return _holdGain[_holdHead];
    }
}
