using NAudio.Wave;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Audio;

/// <summary>One decoded copy of a sound feeding one mixer (speakers or microphone).</summary>
internal sealed class Voice : ISampleProvider, IDisposable
{
    private readonly MediaFoundationReader _reader;
    private readonly LiveEffect _chain;
    private long _reverseFromTicks = -1; // where the sound was when it switched to backwards
    private long _seekTicks = -1; // where to jump at the next read, asked from the UI thread
    private volatile bool _stopped;
    private volatile bool _paused;
    private readonly float _levelGain;
    private readonly LevelingSwitch? _leveling;
    private float _appliedGain;
    private int _ended;

    public Voice(string path, WaveFormat mixFormat, float volume, Playback owner, EffectControl? control = null,
        float levelGain = 1f, LevelingSwitch? leveling = null)
    {
        Owner = owner;
        Volume = volume;
        _levelGain = levelGain;
        _leveling = leveling;
        _appliedGain = TargetGain();
        _reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
        });
        try
        {
            control ??= new EffectControl();
            _chain = new LiveEffect(SampleChains.ToFormat(SpeedAndPitch.Apply(_reader.ToSampleProvider(), control), mixFormat), control);
        }
        catch
        {
            _reader.Dispose();
            throw;
        }
    }

    public Playback Owner { get; }
    public float Volume { get; set; }
    public WaveFormat WaveFormat => _chain.WaveFormat;
    public TimeSpan Duration => _reader.TotalTime;

    /// <summary>
    /// How far the sound has played. Backwards the reader is already at the end, so the progress runs
    /// from where it switched to the end as the reversed part plays.
    /// </summary>
    public TimeSpan Position
    {
        get
        {
            long from = Interlocked.Read(ref _reverseFromTicks);
            if (from < 0 || _chain.Reversed is not { } reversed)
                return _reader.CurrentTime;
            var start = TimeSpan.FromTicks(from);
            return start + (Duration - start) * reversed.Progress;
        }
    }

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Makes the next read return 0 so the mixer drops this voice.</summary>
    public void Stop() => _stopped = true;

    /// <summary>False once playing backwards: the rest of the sound is already read, it plays to the end.</summary>
    public bool CanSeek => _chain.Reversed is null;

    /// <summary>Jumps to <paramref name="position"/> at the next read, on the audio thread that owns the reader.</summary>
    public void Seek(TimeSpan position) =>
        Interlocked.Exchange(ref _seekTicks, Math.Clamp(position.Ticks, 0, Duration.Ticks));

    /// <summary>True only for the first caller, so a voice is accounted as ended exactly once.</summary>
    public bool TryMarkEnded() => Interlocked.Exchange(ref _ended, 1) == 0;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_stopped)
            return 0;
        // Applied while paused too, so the bar moves to the new place before playing on.
        long seek = Interlocked.Exchange(ref _seekTicks, -1);
        if (seek >= 0 && CanSeek)
            _reader.CurrentTime = TimeSpan.FromTicks(seek);
        if (_paused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        long before = _reverseFromTicks < 0 ? _reader.CurrentTime.Ticks : -1;
        int read = _chain.Read(buffer, offset, count);
        if (before >= 0 && _chain.Reversed is not null)
            Interlocked.Exchange(ref _reverseFromTicks, before);
        float from = _appliedGain;
        float to = TargetGain();
        if (from != to && read > 0)
        {
            // Leveling switched while playing: glide to the new gain instead of jumping.
            float step = (to - from) / read;
            for (int i = 0; i < read; i++)
                buffer[offset + i] *= from + step * i;
            _appliedGain = to;
        }
        else if (to != 1f)
        {
            for (int i = offset; i < offset + read; i++)
                buffer[i] *= to;
        }
        return read;
    }

    private float TargetGain() => Volume * (_leveling?.Enabled == true ? _levelGain : 1f);

    public void Dispose() => _reader.Dispose();
}
