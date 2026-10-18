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
        CurrentMic());

    public PlayResult PlaySoundById(Guid soundId, PlayTarget target = PlayTarget.Default)
    {
        if (!_soundsById.TryGetValue(soundId, out var sound))
            return PlayResult.NotFound;
        return TryPlay(sound, target) is { } error ? PlayResult.Failed(error) : PlayResult.Started;
    }

    public PlaybackDto CurrentPlayback()
    {
        if (_engine.Current is not { } current)
            return PlaybackDto.Stopped;
        return new PlaybackDto(
            _engine.IsPaused ? "paused" : "playing",
            current.Tag as Guid?,
            (long)current.Position.TotalMilliseconds,
            (long)current.Duration.TotalMilliseconds);
    }

    public MicDto CurrentMic() =>
        new(char.ToLowerInvariant(_micState.ToString()[0]) + _micState.ToString()[1..], PlayOnMic);

    /// <summary>Saves and tells connected phones that the library changed.</summary>
    private void LibraryEdited()
    {
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
