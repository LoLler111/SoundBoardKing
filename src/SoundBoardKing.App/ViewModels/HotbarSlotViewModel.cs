using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class HotbarSlotViewModel(int slot) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private SoundItemViewModel? sound;

    /// <summary>Name of the category the sound is in, shown small above the title; empty when it has none.</summary>
    [ObservableProperty]
    private string category = "";

    public int Slot { get; } = slot;
    public string Number => (Slot + 1).ToString();
    public bool IsEmpty => Sound is null;
}
