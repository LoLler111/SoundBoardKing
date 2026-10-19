using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

public sealed partial class SoundItemViewModel : ObservableObject
{
    [ObservableProperty]
    private bool isMissing;

    [ObservableProperty]
    private bool isPlaying;

    public SoundItemViewModel(Sound model)
    {
        Model = model;
        RefreshFileState();
    }

    public Sound Model { get; }
    public Guid Id => Model.Id;
    public string Artist => Model.Artist;
    public string Duration => Model.DurationMs > 0 ? TimeFormat.Format(TimeSpan.FromMilliseconds(Model.DurationMs)) : "";

    public string Title
    {
        get => Model.Title;
        set
        {
            if (SetProperty(Model.Title, value, Model, (m, v) => m.Title = v))
                OnPropertyChanged(nameof(EditableTitle));
        }
    }

    /// <summary>
    /// The title as the list edits it. The grid only edits two-way bindings, but the new title goes
    /// through MainViewModel.RenameSound (trim, save, reorder, phones), so the setter keeps nothing.
    /// </summary>
    public string EditableTitle
    {
        get => Title;
        set { }
    }

    public string? Hotkey
    {
        get => Model.Hotkey;
        set => SetProperty(Model.Hotkey, value, Model, (m, v) => m.Hotkey = v);
    }

    public void RefreshFileState() => IsMissing = !File.Exists(Model.Path);
}
