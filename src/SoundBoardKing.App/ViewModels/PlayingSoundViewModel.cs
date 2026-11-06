using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Audio;

namespace SoundBoardKing.App.ViewModels;

/// <summary>One row of the playback bar: a sound that is playing right now.</summary>
public sealed partial class PlayingSoundViewModel(Playback playback, string title) : ObservableObject
{
    // After a jump the audio thread moves the sound a moment later: until then the bar keeps showing
    // the point asked for, instead of going back for a frame.
    private const long SeekSettleMs = 300;
    private long _showAskedUntil;

    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds = 1;

    [ObservableProperty]
    private string positionText = "";

    [ObservableProperty]
    private bool isPaused;

    [ObservableProperty]
    private bool canSeek = true;

    public Playback Playback { get; } = playback;
    public string Title { get; } = title;

    public void Refresh()
    {
        DurationSeconds = Math.Max(Playback.Duration.TotalSeconds, 0.001);
        IsPaused = Playback.IsPaused;
        CanSeek = Playback.CanSeek;
        if (Environment.TickCount64 < _showAskedUntil)
            return;
        ShowPosition(Playback.Position.TotalSeconds);
    }

    /// <summary>Shows a jump to <paramref name="seconds"/> at once, while the sound gets there.</summary>
    public void ShowSeek(double seconds)
    {
        _showAskedUntil = Environment.TickCount64 + SeekSettleMs;
        ShowPosition(seconds);
    }

    private void ShowPosition(double seconds)
    {
        PositionSeconds = Math.Clamp(seconds, 0, DurationSeconds);
        PositionText = $"{TimeFormat.Format(TimeSpan.FromSeconds(PositionSeconds))} / {TimeFormat.Format(Playback.Duration)}";
    }
}
