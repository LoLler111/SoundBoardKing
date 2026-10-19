using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Audio;

public sealed record PlayRequest(
    object? Tag,
    float Volume = 1f,
    bool Speakers = true,
    bool Mic = false,
    bool AllowOverlap = false);

/// <summary>A sound being played, on the speakers, the microphone, or both.</summary>
public sealed class Playback
{
    private Voice? _speaker;
    private Voice? _mic;
    private int _activeVoices;

    internal Playback(object? tag) => Tag = tag;

    /// <summary>Tells apart two plays of the same sound.</summary>
    public Guid Id { get; } = Guid.NewGuid();

    public object? Tag { get; }
    public TimeSpan Position => (_speaker ?? _mic)?.Position ?? TimeSpan.Zero;
    public TimeSpan Duration => (_speaker ?? _mic)?.Duration ?? TimeSpan.Zero;

    /// <summary>Paused on its own or with all the others.</summary>
    public bool IsPaused { get; private set; }

    internal Voice? SpeakerVoice => _speaker;

    internal IEnumerable<Voice> Voices
    {
        get
        {
            if (_speaker is not null) yield return _speaker;
            if (_mic is not null) yield return _mic;
        }
    }

    internal void Attach(Voice? speaker, Voice? mic)
    {
        _speaker = speaker;
        _mic = mic;
        _activeVoices = (speaker is null ? 0 : 1) + (mic is null ? 0 : 1);
    }

    /// <summary>Returns true when the last voice has ended.</summary>
    internal bool OnVoiceEnded() => --_activeVoices <= 0;

    internal void Stop()
    {
        foreach (var voice in Voices)
            voice.Stop();
    }

    internal void SetPaused(bool paused)
    {
        IsPaused = paused;
        foreach (var voice in Voices)
            voice.Paused = paused;
    }
}
