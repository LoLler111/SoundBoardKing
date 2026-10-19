using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Core.Models;

namespace SoundBoardKing.App.Views;

/// <summary>Stays open beside the main window, so sounds can be added while browsing the library.</summary>
public partial class MyinstantsWindow : Window
{
    private readonly MyinstantsViewModel _model;

    public MyinstantsWindow(MyinstantsViewModel model)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _model = model;
        DataContext = model;
        Loaded += (_, _) =>
        {
            QueryBox.Focus();
            // Something to listen to right away: what is trending.
            _model.SearchCommand.Execute(null);
        };
        Closed += (_, _) => _model.Dispose();
    }

    // "Aggiungi" asks where: every category (subcategories indented), a new one, or the library only.
    // The last choice is ticked, so adding a series to the same category stays quick.
    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { DataContext: MyinstantsResultViewModel result } button)
            return;

        // Tall enough for a dozen categories; more scroll. The fixed choices stay on top, always in reach.
        var menu = new ContextMenu { PlacementTarget = button, Placement = PlacementMode.Bottom, MaxHeight = 460 };
        menu.Items.Add(Choice("Nuova categoria…", 0, false, () =>
        {
            if (_model.AskNewCategory() is { } created)
                _ = _model.AddAsync(result, created);
        }));
        menu.Items.Add(Choice("Solo nella libreria", 0, _model.LastCategory is null, () => _ = _model.AddAsync(result, null)));
        var categories = _model.Categories;
        if (categories.Count > 0)
            menu.Items.Add(new Separator());
        MenuItem? ticked = null;
        foreach (var (category, depth) in categories)
        {
            var item = Choice(category.Name, depth, category == _model.LastCategory, () => _ = _model.AddAsync(result, category));
            menu.Items.Add(item);
            if (item.IsChecked)
                ticked = item;
        }
        // The last category used may be far down the list: bring it into view.
        if (ticked is not null)
            menu.Opened += (_, _) => ticked.BringIntoView();
        menu.IsOpen = true;
    }

    private static MenuItem Choice(string text, int depth, bool isChecked, Action chosen)
    {
        var item = new MenuItem
        {
            Header = new TextBlock { Text = text, Margin = new Thickness(depth * 16, 0, 0, 0) },
            IsChecked = isChecked,
        };
        item.Click += (_, _) => chosen();
        return item;
    }
}
