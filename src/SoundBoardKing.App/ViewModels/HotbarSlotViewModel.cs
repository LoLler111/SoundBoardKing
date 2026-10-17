using CommunityToolkit.Mvvm.ComponentModel;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class HotbarSlotViewModel(int slot) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    private SoundItemViewModel? sound;

    public int Slot { get; } = slot;
    public string Number => (Slot + 1).ToString();
    public bool IsEmpty => Sound is null;
}
