using NAudio.Wave;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Audio;

/// <summary>One decoded copy of a sound feeding one mixer (speakers or microphone).</summary>
internal sealed class Voice : ISampleProvider, IDisposable
{
    private readonly MediaFoundationReader _reader;
    private readonly ISampleProvider _chain;
    private volatile bool _stopped;
    private volatile bool _paused;
    private int _ended;

    public Voice(string path, WaveFormat mixFormat, float volume, Playback owner, SoundEffect effect = SoundEffect.None, EffectControl? control = null)
    {
        Owner = owner;
        Volume = volume;
        _reader = new MediaFoundationReader(path, new MediaFoundationReader.MediaFoundationReaderSettings
        {
            RequestFloatOutput = true,
        });
        try
        {
            _chain = SoundEffects.Apply(SampleChains.ToFormat(_reader.ToSampleProvider(), mixFormat), effect, control);
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
    public TimeSpan Position => _reader.CurrentTime;
    public TimeSpan Duration => _reader.TotalTime;

    public bool Paused
    {
        get => _paused;
        set => _paused = value;
    }

    /// <summary>Makes the next read return 0 so the mixer drops this voice.</summary>
    public void Stop() => _stopped = true;

    /// <summary>True only for the first caller, so a voice is accounted as ended exactly once.</summary>
    public bool TryMarkEnded() => Interlocked.Exchange(ref _ended, 1) == 0;

    public int Read(float[] buffer, int offset, int count)
    {
        if (_stopped)
            return 0;
        if (_paused)
        {
            Array.Clear(buffer, offset, count);
            return count;
        }

        int read = _chain.Read(buffer, offset, count);
        float volume = Volume;
        if (volume != 1f)
        {
            for (int i = offset; i < offset + read; i++)
                buffer[i] *= volume;
        }
        return read;
    }

    public void Dispose() => _reader.Dispose();
}
