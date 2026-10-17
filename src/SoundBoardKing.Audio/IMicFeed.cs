using NAudio.Wave;

namespace SoundBoardKing.Audio;

/// <summary>
/// Destination that mixes audio into the real microphone stream. Phase 2 implements it with the
/// SbkMicFx APO; until then <see cref="NullMicFeed"/> reports it as unavailable.
/// </summary>
public interface IMicFeed : IDisposable
{
    bool IsAvailable { get; }

    /// <summary>Format the microphone runs at; sounds are converted to it before mixing.</summary>
    WaveFormat? Format { get; }

    /// <summary>Starts pulling samples from <paramref name="source"/> at the microphone's pace.</summary>
    void Attach(ISampleProvider source);

    void Detach();
}

public sealed class NullMicFeed : IMicFeed
{
    public bool IsAvailable => false;
    public WaveFormat? Format => null;
    public void Attach(ISampleProvider source) { }
    public void Detach() { }
    public void Dispose() { }
}
