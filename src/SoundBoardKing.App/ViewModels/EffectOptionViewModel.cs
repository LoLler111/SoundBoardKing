using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

/// <summary>One entry of the effect menu.</summary>
public sealed partial class EffectOptionViewModel(SoundEffect effect) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public SoundEffect Effect { get; } = effect;
    public string Label => LabelOf(Effect);

    public static string LabelOf(SoundEffect effect) => effect switch
    {
        SoundEffect.Chipmunk => "Scoiattolo",
        SoundEffect.Deep => "Voce profonda",
        SoundEffect.Robot => "Robot",
        SoundEffect.Radio => "Radio",
        SoundEffect.Echo => "Eco",
        _ => "Nessun effetto",
    };
}
