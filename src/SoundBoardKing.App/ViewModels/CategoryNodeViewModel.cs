using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.ViewModels;

/// <summary>A node of the category tree; <see cref="Model"/> is null for the "All sounds" node.</summary>
public sealed partial class CategoryNodeViewModel : ObservableObject
{
    private readonly Action<CategoryNodeViewModel> _onSelected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DisplayName))]
    private int count;

    [ObservableProperty]
    private bool isExpanded;

    [ObservableProperty]
    private bool isSelected;

    public CategoryNodeViewModel(Category? model, CategoryNodeViewModel? parent, Action<CategoryNodeViewModel> onSelected)
    {
        Model = model;
        Parent = parent;
        _onSelected = onSelected;
    }

    public Category? Model { get; }
    public CategoryNodeViewModel? Parent { get; }
    public ObservableCollection<CategoryNodeViewModel> Children { get; } = [];
    public string Name => Model?.Name ?? "Tutti i suoni";
    public string DisplayName => $"{Name} ({Count})";

    public IEnumerable<CategoryNodeViewModel> SelfAndDescendants() =>
        new[] { this }.Concat(Children.SelectMany(c => c.SelfAndDescendants()));

    public void NotifyNameChanged()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(DisplayName));
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value)
            _onSelected(this);
    }
}
