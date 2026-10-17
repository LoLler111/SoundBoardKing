using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Audio;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class SettingsDialogModel(IReadOnlyList<AudioDevice> devices) : ObservableObject
{
    /// <summary>Device with an empty id stands for "follow the Windows default".</summary>
    public IReadOnlyList<AudioDevice> Devices { get; } = devices;

    [ObservableProperty]
    private AudioDevice? selectedDevice;

    [ObservableProperty]
    private bool allowOverlap;

    [ObservableProperty]
    private string? stopHotkey;

    [ObservableProperty]
    private string? pauseHotkey;
}
