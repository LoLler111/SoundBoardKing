using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

/// <summary>One entry of the menu that says what the effect is applied to.</summary>
public sealed partial class EffectTargetOptionViewModel(EffectTarget target) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public EffectTarget Target { get; } = target;
    public string Label => LabelOf(Target);

    public static string LabelOf(EffectTarget target) => target switch
    {
        EffectTarget.Voice => "Applica solo al microfono",
        EffectTarget.Both => "Applica a entrambi",
        EffectTarget.Off => "Non applicare",
        _ => "Applica solo ai suoni",
    };

    /// <summary>On the button, which says "applica" in its tooltip: short, so the playback bars keep their room.</summary>
    public static string ShortLabelOf(EffectTarget target) => target switch
    {
        EffectTarget.Voice => "Solo al microfono",
        EffectTarget.Both => "A entrambi",
        EffectTarget.Off => "Non applicare",
        _ => "Solo ai suoni",
    };
}
