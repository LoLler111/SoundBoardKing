namespace SoundBoardKing.Remote;

/// <summary>What the server needs from the app. Called from request threads: implementations marshal to the UI.</summary>
public interface IRemoteController
{
    /// <summary>Bumped whenever sounds, categories or favourites change.</summary>
    long LibraryVersion { get; }

    /// <summary>Full state; <see cref="StateDto.ServerId"/>, Name and Protocol are filled in by the server.</summary>
    Task<StateDto> GetStateAsync();

    Task<PlayResult> PlayAsync(Guid soundId);

    Task StopAsync();
    Task TogglePauseAsync();
    Task SetMicEnabledAsync(bool enabled);
}
