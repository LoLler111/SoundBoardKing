using SoundBoardKing.Core.Models;

namespace SoundBoardKing.Remote;

/// <summary>What the server needs from the app. Called from request threads: implementations marshal to the UI.</summary>
public interface IRemoteController
{
    /// <summary>Bumped whenever sounds, categories or favourites change.</summary>
    long LibraryVersion { get; }

    /// <summary>Full state; <see cref="StateDto.ServerId"/>, Name and Protocol are filled in by the server.</summary>
    Task<StateDto> GetStateAsync();

    Task<PlayResult> PlayAsync(Guid soundId, PlayTarget target = PlayTarget.Default);

    Task StopAsync();

    /// <summary>Stops one of the sounds playing together; false when it already ended.</summary>
    Task<bool> StopOneAsync(Guid playbackId);
    /// <summary>Pauses or resumes one of the sounds playing together; false when it already ended.</summary>
    Task<bool> TogglePauseOneAsync(Guid playbackId);
    Task TogglePauseAsync();
    Task SetMicEnabledAsync(bool enabled);

    /// <summary>Changes the voice effect the sounds play through.</summary>
    Task SetEffectAsync(SoundEffect effect);

    /// <summary>Changes how strong the effect is, 0-100.</summary>
    Task SetEffectAmountAsync(int percent);

    /// <summary>Playback speed of every sound, like a record: 0.5 to 2.</summary>
    Task SetPlaybackSpeedAsync(double speed);

    /// <summary>Whether the effect changes the sounds, the voice, both or nothing.</summary>
    Task SetEffectTargetAsync(EffectTarget target);

    /// <summary>Changes a sound's title (not its file); false when the sound does not exist.</summary>
    Task<bool> RenameSoundAsync(Guid soundId, string title);
}
