using NAudio.Wave;

namespace SoundBoardKing.Audio;

/// <summary>
/// Changes the tempo and keeps the pitch (WSOLA), the way a voice message plays at 2x: the source is
/// cut into pieces that overlap by half, each taken near where the tempo puts it, at the spot whose
/// shape best continues the piece before, so the waves join without clicks or doubled echoes. The
/// tempo is read before every piece, so it follows changes while the sound plays; at 1 the pieces
/// simply follow each other and the sound comes out unchanged.
/// </summary>
internal sealed class TimeStretchProvider : ISampleProvider
{
    public const float MinTempo = 0.25f;
    public const float MaxTempo = 4f;

    private readonly ISampleProvider _source;
    private readonly Func<float> _tempo;
    private readonly int _channels;
    private readonly int _hop;  // frames each piece adds to the output; a piece is twice as long
    private readonly int _seek; // how far a piece may move from where the tempo puts it, frames
    private readonly float[] _window; // Hann over a piece: two halves that overlap sum to 1

    private float[] _input; // source frames from _inputStart on, interleaved; zeros past the end
    private float[] _mono;  // the same frames with the channels summed, to compare shapes
    private int _inputCount;
    private long _inputStart;
    private bool _sourceEnded;
    private long _sourceFrames = long.MaxValue;

    private double _nominal; // where the next piece starts if the tempo alone decided
    private long _natural;   // where it starts to continue the last piece seamlessly
    private readonly float[] _tail; // the last piece's second half, windowed, waiting for the next one
    private readonly float[] _out;  // one hop of output
    private int _outPosition, _outCount;
    private bool _primed, _finished;

    public TimeStretchProvider(ISampleProvider source, Func<float> tempo)
    {
        _source = source;
        _tempo = tempo;
        _channels = source.WaveFormat.Channels;
        int rate = source.WaveFormat.SampleRate;
        // 40 ms pieces, moved by up to 12 ms: long enough for the lowest voices, short enough not to smear.
        _hop = Math.Max(16, rate / 50);
        _seek = Math.Max(4, rate * 12 / 1000);
        _window = new float[2 * _hop];
        for (int i = 0; i < _window.Length; i++)
            _window[i] = (float)(0.5 - 0.5 * Math.Cos(Math.PI * i / _hop));
        _input = new float[8 * _hop * _channels];
        _mono = new float[8 * _hop];
        _tail = new float[_hop * _channels];
        _out = new float[_hop * _channels];
    }

    public WaveFormat WaveFormat => _source.WaveFormat;

    public int Read(float[] buffer, int offset, int count)
    {
        int written = 0;
        while (written < count)
        {
            if (_outPosition == _outCount && (_finished || !NextPiece()))
                break;
            int n = Math.Min(count - written, _outCount - _outPosition);
            Array.Copy(_out, _outPosition, buffer, offset + written, n);
            _outPosition += n;
            written += n;
        }
        return written;
    }

    private bool NextPiece()
    {
        if (!_primed)
            Prime();
        float tempo = Math.Clamp(_tempo(), MinTempo, MaxTempo);
        bool plain = Math.Abs(tempo - 1f) < 1e-4f;
        if (plain)
            _nominal = _natural; // nothing to stretch: the pieces just follow each other
        EnsureInput((long)Math.Ceiling(_nominal) + 2 * _hop);

        int h = _hop * _channels;
        long start = _natural;
        if (_sourceEnded && _nominal >= _sourceFrames)
        {
            // The tempo has gone through the whole sound: only the last tail is left to fade out.
            _finished = true;
            if (_natural >= _sourceFrames)
                return false;
            Array.Copy(_tail, _out, h);
        }
        else
        {
            if (!plain)
                start = BestStart((long)Math.Round(_nominal));
            EnsureInput(start + 2 * _hop);
            int at = (int)(start - _inputStart) * _channels;
            for (int i = 0; i < h; i++)
            {
                int frame = i / _channels;
                _out[i] = _tail[i] + _input[at + i] * _window[frame];
                _tail[i] = _input[at + h + i] * _window[_hop + frame];
            }
        }
        _outPosition = 0;
        _outCount = h;
        _natural = start + _hop;
        _nominal += _hop * tempo;
        Discard();
        return true;
    }

    // As if a piece had started half a piece before the sound: the first output is not faded in.
    private void Prime()
    {
        _primed = true;
        EnsureInput(2 * _hop);
        for (int i = 0; i < _tail.Length; i++)
            _tail[i] = _input[i] * _window[_hop + i / _channels];
        _natural = 0;
        _nominal = 0;
    }

    /// <summary>Where within the seek range a piece's first half most looks like what follows the last piece.</summary>
    private long BestStart(long nominal)
    {
        long from = Math.Max(nominal - _seek, _inputStart);
        long to = Math.Max(nominal + _seek, from);
        EnsureInput(Math.Max(to, _natural) + 2 * _hop);
        int reference = (int)(_natural - _inputStart);

        // Every fourth spot and sample first, then every one around the best.
        long best = from;
        double bestScore = double.NegativeInfinity;
        for (long p = from; p <= to; p += 4)
        {
            double score = Similarity((int)(p - _inputStart), reference, 4);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        long coarse = best;
        bestScore = double.NegativeInfinity;
        for (long p = Math.Max(from, coarse - 3); p <= Math.Min(to, coarse + 3); p++)
        {
            double score = Similarity((int)(p - _inputStart), reference, 1);
            if (score > bestScore)
            {
                bestScore = score;
                best = p;
            }
        }
        return best;
    }

    // Correlation scaled by the candidate's own energy, so loud spots do not win just by being loud.
    private double Similarity(int candidate, int reference, int step)
    {
        double dot = 0, energy = 1e-9;
        for (int i = 0; i < _hop; i += step)
        {
            float c = _mono[candidate + i];
            dot += c * _mono[reference + i];
            energy += c * c;
        }
        return dot / Math.Sqrt(energy);
    }

    private void EnsureInput(long until)
    {
        int needed = (int)(until - _inputStart);
        if (needed <= _inputCount)
            return;
        if (needed > _mono.Length)
        {
            int frames = Math.Max(needed, _mono.Length * 2);
            Array.Resize(ref _input, frames * _channels);
            Array.Resize(ref _mono, frames);
        }
        while (_inputCount < needed)
        {
            int from = _inputCount;
            if (!_sourceEnded)
            {
                int read = _source.Read(_input, from * _channels, (_mono.Length - from) * _channels) / _channels;
                if (read == 0)
                {
                    _sourceEnded = true;
                    _sourceFrames = _inputStart + from;
                }
                _inputCount += read;
            }
            if (_sourceEnded)
            {
                Array.Clear(_input, from * _channels, (needed - from) * _channels);
                _inputCount = needed;
            }
            for (int f = from; f < _inputCount; f++)
            {
                float sum = 0;
                for (int c = 0; c < _channels; c++)
                    sum += _input[f * _channels + c];
                _mono[f] = sum;
            }
        }
    }

    // Drops the frames no piece can reach any more; memory moves only once half the buffer is behind.
    private void Discard()
    {
        long keep = Math.Min(_natural, (long)Math.Floor(_nominal) - _seek);
        int drop = (int)Math.Clamp(keep - _inputStart, 0, _inputCount);
        if (drop < _mono.Length / 2)
            return;
        Array.Copy(_input, drop * _channels, _input, 0, (_inputCount - drop) * _channels);
        Array.Copy(_mono, drop, _mono, 0, _inputCount - drop);
        _inputCount -= drop;
        _inputStart += drop;
    }
}
