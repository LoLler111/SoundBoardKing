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
        SoundEffect.Megaphone => "Megafono",
        SoundEffect.Overdrive => "Distrutto",
        SoundEffect.Bitcrush => "8-bit",
        SoundEffect.Muffled => "Dietro la porta",
        SoundEffect.Underwater => "Sott'acqua",
        SoundEffect.Reverb => "Cattedrale",
        SoundEffect.Chorus => "Coro",
        SoundEffect.Flanger => "Jet",
        SoundEffect.Vibrato => "Pecora",
        SoundEffect.WalkieTalkie => "Walkie-talkie",
        SoundEffect.Demon => "Demone",
        SoundEffect.Alien => "Alieno",
        SoundEffect.Autotune => "Autotune",
        SoundEffect.Whisper => "Sussurro",
        SoundEffect.Vinyl => "Vinile",
        SoundEffect.Stutter => "Balbuziente",
        SoundEffect.Reverse => "Al contrario (solo suoni)",
        _ => "Nessun effetto",
    };
}
