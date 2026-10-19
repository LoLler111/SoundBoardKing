using SoundBoardKing.Audio.MicFx;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;
using SoundBoardKing.Remote;

namespace SoundBoardKing.App.ViewModels;

// What the phone remote sees of the view model. Every member runs on the UI thread;
// RemoteController marshals calls coming from the server.
public sealed partial class MainViewModel
{
    private long _libraryVersion = 1;
    private MicFxState _micState = MicFxState.NotInstalled;
    private bool _micReportedEnabled = true;

    /// <summary>Sounds, categories or favourites changed: phones reload the state.</summary>
    public event EventHandler? LibraryChanged;

    public event EventHandler<PlaybackDto>? PlaybackChanged;
    public event EventHandler<MicDto>? MicChanged;

    /// <summary>The voice effect or its amount changed (here or from a phone).</summary>
    public event EventHandler? EffectChanged;

    public long LibraryVersion => Interlocked.Read(ref _libraryVersion);

    public bool RemoteEnabled
    {
        get => _settings.RemoteEnabled;
        set
        {
            if (value == _settings.RemoteEnabled)
                return;
            _settings.RemoteEnabled = value;
            OnPropertyChanged();
            ScheduleSave();
        }
    }

    public int RemotePort => _settings.RemotePort;
    public string ServerId => _settings.ServerId;
    public IReadOnlyList<PairedDevice> RemoteDevices => _settings.RemoteDevices;

    public void SaveRemoteDevices(IReadOnlyList<PairedDevice> devices)
    {
        _settings.RemoteDevices = [.. devices];
        ScheduleSave();
    }

    public StateDto BuildRemoteState() => new(
        ServerId: "",
        Name: "",
        Protocol: 0,
        LibraryVersion,
        _library.Sounds
            .Select(s => new SoundDto(s.Id, s.Title, s.Artist, s.DurationMs,
                _soundsById.TryGetValue(s.Id, out var item) && item.IsMissing, s.Hotkey))
            .ToList(),
        _library.Categories.Select(ToDto).ToList(),
        new HotbarDto(Hotbar.PageCount, Hotbar.SlotsPerPage, _library.Hotbar.Slots
            .Select(p => new HotbarSlotDto(p.Key / 1000, p.Key % 1000, p.Value))
            .OrderBy(s => s.Page).ThenBy(s => s.Slot)
            .ToList()),
        CurrentPlayback(),
        CurrentMic(),
        EffectNames.ToWire(SoundEffect),
        EffectAmountPercent,
        Math.Round(PlaybackSpeed, 3),
        EffectNames.ToWire(EffectTarget));

    public PlayResult PlaySoundById(Guid soundId, PlayTarget target = PlayTarget.Default)
    {
        if (!_soundsById.TryGetValue(soundId, out var sound))
            return PlayResult.NotFound;
        return TryPlay(sound, target) is { } error ? PlayResult.Failed(error) : PlayResult.Started;
    }

    /// <summary>Rename from a phone; false when the sound does not exist or the title is empty.</summary>
    public bool RenameSoundById(Guid soundId, string title)
    {
        if (!_soundsById.TryGetValue(soundId, out var sound) || title.Trim().Length == 0)
            return false;
        RenameSound(sound, title);
        return true;
    }

    public PlaybackDto CurrentPlayback()
    {
        if (_engine.Current is not { } current)
            return PlaybackDto.Stopped;
        return new PlaybackDto(
            _engine.IsPaused ? "paused" : "playing",
            current.Tag as Guid?,
            (long)current.Position.TotalMilliseconds,
            (long)current.Duration.TotalMilliseconds,
            NowPlaying
                .Select(p => p.Playback)
                .Select(p => new PlayingDto(p.Id, p.Tag as Guid?, (long)p.Position.TotalMilliseconds, (long)p.Duration.TotalMilliseconds, p.IsPaused))
                .ToList());
    }

    /// <summary>Stops one of the sounds playing together; false when it already ended.</summary>
    public bool StopPlayback(Guid playbackId)
    {
        if (NowPlaying.FirstOrDefault(p => p.Playback.Id == playbackId) is not { } item)
            return false;
        _engine.Stop(item.Playback);
        return true;
    }

    /// <summary>Pauses or resumes one of the sounds playing together; false when it already ended.</summary>
    public bool TogglePausePlayback(Guid playbackId)
    {
        if (NowPlaying.FirstOrDefault(p => p.Playback.Id == playbackId) is not { } item)
            return false;
        PauseOne(item);
        return true;
    }

    public MicDto CurrentMic() =>
        new(char.ToLowerInvariant(_micState.ToString()[0]) + _micState.ToString()[1..], PlayOnMic);

    /// <summary>Saves and tells connected phones that the library changed.</summary>
    private void LibraryEdited()
    {
        // Every list goes back to its chosen order before the phones hear of the change. Playing a
        // sound does not count, so "most played" moves only at the next edit, not under the mouse.
        _library.ApplySorts();
        RefreshVisible();
        RefreshHotbar(); // a sound may have moved to another category
        ScheduleSave();
        NotifyLibraryChanged();
    }

    private void NotifyLibraryChanged()
    {
        Interlocked.Increment(ref _libraryVersion);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RaisePlaybackChanged() => PlaybackChanged?.Invoke(this, CurrentPlayback());

    private static CategoryDto ToDto(Category category) =>
        new(category.Id, category.Name, [.. category.SoundIds], category.Children.Select(ToDto).ToList());
}
