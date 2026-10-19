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
        EffectTarget.Voice => "Solo microfono",
        EffectTarget.Both => "Suoni e microfono",
        EffectTarget.Off => "Nessuno",
        _ => "Solo suoni",
    };
}
