using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Audio;

namespace SoundBoardKing.App.ViewModels;

/// <summary>One row of the playback bar: a sound that is playing right now.</summary>
public sealed partial class PlayingSoundViewModel(Playback playback, string title) : ObservableObject
{
    [ObservableProperty]
    private double positionSeconds;

    [ObservableProperty]
    private double durationSeconds = 1;

    [ObservableProperty]
    private string positionText = "";

    [ObservableProperty]
    private bool isPaused;

    public Playback Playback { get; } = playback;
    public string Title { get; } = title;

    public void Refresh()
    {
        DurationSeconds = Math.Max(Playback.Duration.TotalSeconds, 0.001);
        PositionSeconds = Math.Min(Playback.Position.TotalSeconds, DurationSeconds);
        PositionText = $"{TimeFormat.Format(Playback.Position)} / {TimeFormat.Format(Playback.Duration)}";
        IsPaused = Playback.IsPaused;
    }
}
