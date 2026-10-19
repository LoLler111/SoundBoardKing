using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

/// <summary>One entry of the "order by" menu above the sound list.</summary>
public sealed partial class SortOptionViewModel(SoundSort sort) : ObservableObject
{
    [ObservableProperty]
    private bool isSelected;

    public SoundSort Sort { get; } = sort;
    public string Label => LabelOf(Sort);

    public static string LabelOf(SoundSort sort) => sort switch
    {
        SoundSort.Name => "Nome A→Z",
        SoundSort.Newest => "Aggiunti di recente",
        SoundSort.MostPlayed => "Più usati",
        SoundSort.RecentlyPlayed => "Usati di recente",
        SoundSort.Shortest => "Più corti",
        SoundSort.Longest => "Più lunghi",
        _ => "Manuale (trascina)",
    };
}
