using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Threading;
using SoundBoardKing.App.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using SoundBoardKing.App.Hotkeys;
using SoundBoardKing.App.Remote;
using SoundBoardKing.App.Views;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Audio;
using SoundBoardKing.Audio.MicFx;
using SoundBoardKing.Core.Models;
using SoundBoardKing.Core.Storage;

namespace SoundBoardKing.App;

public partial class MainWindow : Window
{
    private const string SoundsDataFormat = "SoundBoardKing.Sounds";

    private readonly MainViewModel _viewModel;
    private RemoteHost? _remote;
    private Point _dragStart;
    private bool _dragArmed;

    public MainWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        var micFeed = new ApoMicFeed();
        var updates = new UpdateService();
        // Unpacked from the Portable zip, the folder the user sees is the one above the app's own
        // (replaced at each update); installed with Setup.exe, nothing near the app survives an update.
        var appFolder = UpdateService.PortableRootDirectory ?? AppContext.BaseDirectory;
        _storage = new ProfileStorage(AppPaths.Default, appFolder, allowAppFolder: !updates.IsSetupInstall);
        RestorePlacement();
        _viewModel = new MainViewModel(
            _storage,
            new AudioEngine(micFeed),
            micFeed,
            new HotkeyService(),
            new DialogService(this),
            updates);
        DataContext = _viewModel;
        _viewModel.UpdateRestartRequested += (_, _) => Close();
        _viewModel.RestartRequested += (_, _) =>
        {
            // The new instance waits for this one to exit before taking the single-instance lock.
            Process.Start(Environment.ProcessPath!, $"--after {Environment.ProcessId}");
            Close();
        };

        // The library and the phone server load only after the first frame, so the window shows up
        // right away (dark, with this text) instead of staying white while the work runs.
        _viewModel.StatusText = "Caricamento della libreria…";
        ContentRendered += OnContentRendered;
        Closed += (_, _) => _viewModel.Dispose();
        _viewModel.RenameRequested += (_, sound) => StartRename(sound);
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasArtists))
                ArtistColumn.Visibility = _viewModel.HasArtists ? Visibility.Visible : Visibility.Collapsed;
        };
    }

    // Until WPF draws, Windows fills the window with this colour: dark like the theme, not white.
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource { CompositionTarget: { } target } &&
            Background is SolidColorBrush brush)
            target.BackgroundColor = brush.Color;
    }

    private async void OnContentRendered(object? sender, EventArgs e)
    {
        ContentRendered -= OnContentRendered;
        _viewModel.StatusText = "";
        _viewModel.Initialize();
        _remote = new RemoteHost(_viewModel, Dispatcher);
        if (_viewModel.RemoteEnabled)
            await _remote.StartAsync();
        await _viewModel.OfferMicModuleRefreshAsync();
        await _viewModel.CheckForUpdatesInBackgroundAsync();
    }

    // ---- Window position ---------------------------------------------------------------------

    private readonly ProfileStorage _storage;

    // Back where it was last time (a FancyZones zone too), as long as that spot is still on a screen.
    private void RestorePlacement()
    {
        try
        {
            _storage.LoadLocal(); // loaded again with the profile, which also reports a broken file
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (_storage.Local.Window is not { Width: >= 200, Height: >= 150 } saved)
            return;
        var screen = new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);
        var bounds = new Rect(saved.Left, saved.Top, saved.Width, saved.Height);
        // At least the title bar must be reachable, or the window would open where nobody can see it.
        var titleBar = new Rect(bounds.Left + 40, bounds.Top, Math.Max(bounds.Width - 80, 1), 30);
        if (!screen.IntersectsWith(titleBar))
            return;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = bounds.Left;
        Top = bounds.Top;
        Width = bounds.Width;
        Height = bounds.Height;
        if (saved.Maximized)
            WindowState = WindowState.Maximized;
    }

    private void SavePlacement()
    {
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, ActualWidth, ActualHeight) : RestoreBounds;
        if (bounds.IsEmpty)
            return;
        _storage.Local.Window = new WindowPlacement
        {
            Left = bounds.Left,
            Top = bounds.Top,
            Width = bounds.Width,
            Height = bounds.Height,
            Maximized = WindowState == WindowState.Maximized,
        };
    }

    // The phone server shuts down asynchronously, so the first close request waits for it.
    protected override async void OnClosing(CancelEventArgs e)
    {
        SavePlacement(); // written with the other settings when the window has closed
        if (_remote is { } remote)
        {
            e.Cancel = true;
            _remote = null;
            await remote.DisposeAsync();
            Close();
            return;
        }
        base.OnClosing(e);
    }

    private void EffectButton_Click(object sender, RoutedEventArgs e) => OpenMenu(EffectButton);

    private void EffectTargetButton_Click(object sender, RoutedEventArgs e) => OpenMenu(EffectTargetButton);

    private void SortButton_Click(object sender, RoutedEventArgs e) => OpenMenu(SortButton, PlacementMode.Bottom);

    // The dropdowns: the button's own context menu, opened on a normal click (upwards in the bottom bar).
    private static void OpenMenu(Button button, PlacementMode placement = PlacementMode.Top)
    {
        var menu = button.ContextMenu;
        menu.PlacementTarget = button;
        menu.Placement = placement;
        menu.IsOpen = true;
    }

    // Clicking outside closes the pad: un-press its button too.
    private void PadPopup_Closed(object? sender, EventArgs e) => PadToggle.IsChecked = false;

    private void PhoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is not null)
            new PhoneWindow(_remote) { Owner = this }.ShowDialog();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    // ---- Sound list --------------------------------------------------------------------------

    private void SoundGrid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject)?.Item is SoundItemViewModel sound)
            _viewModel.PlaySoundCommand.Execute(sound);
    }

    private void SoundGrid_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_renaming is not null)
        {
            // Enter confirms; Tab / Shift+Tab confirm and go on renaming the next / previous sound. Esc cancels (the grid does it).
            if (e.Key is Key.Enter or Key.Tab)
            {
                var sounds = _viewModel.VisibleSounds;
                int index = sounds is IList<SoundItemViewModel> list ? list.IndexOf(_renaming) : -1;
                int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1;
                var next = e.Key == Key.Tab && index >= 0 && index + step >= 0 && index + step < sounds.Count
                    ? sounds[index + step]
                    : null;
                SoundGrid.CommitEdit(DataGridEditingUnit.Row, exitEditingMode: true);
                if (next is not null)
                    Dispatcher.BeginInvoke(() => StartRename(next), DispatcherPriority.Background);
                e.Handled = true;
            }
            return;
        }
        switch (e.Key)
        {
            case Key.Enter:
                _viewModel.PlaySoundCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Delete:
                _viewModel.RemoveSoundsCommand.Execute(SoundGrid.SelectedItems);
                e.Handled = true;
                break;
            case Key.F2:
                _viewModel.RenameSoundCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void SoundGrid_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = FindAncestor<DataGridRow>(e.OriginalSource as DependencyObject) is not null;
        _dragStart = e.GetPosition(null);
    }

    private void SoundGrid_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed || _renaming is not null)
            return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        var sounds = SoundGrid.SelectedItems.OfType<SoundItemViewModel>().ToList();
        if (sounds.Count == 0)
            return;
        // Copy onto a category or a favourite, move within the list itself.
        _draggingRows = true;
        try
        {
            DragDrop.DoDragDrop(SoundGrid, new DataObject(SoundsDataFormat, sounds), DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            _draggingRows = false;
            HideInsertionLine();
        }
    }

    // ---- Rename in the row -------------------------------------------------------------------

    private SoundItemViewModel? _renaming;
    private bool _startingRename;

    private void StartRename(SoundItemViewModel sound)
    {
        if (!_viewModel.VisibleSounds.Contains(sound))
            return;
        SoundGrid.SelectedItem = sound;
        SoundGrid.ScrollIntoView(sound, TitleColumn);
        SoundGrid.Focus();
        SoundGrid.CurrentCell = new DataGridCellInfo(sound, TitleColumn);
        _startingRename = true;
        try
        {
            SoundGrid.BeginEdit();
        }
        finally
        {
            _startingRename = false;
        }
    }

    // Only F2, "Rinomina" and Tab start editing: a click on a selected row (or a double click, which plays) must not.
    private void SoundGrid_BeginningEdit(object? sender, DataGridBeginningEditEventArgs e)
    {
        if (!_startingRename || e.Column != TitleColumn)
            e.Cancel = true;
    }

    private void SoundGrid_PreparingCellForEdit(object? sender, DataGridPreparingCellForEditEventArgs e)
    {
        _renaming = e.Row.Item as SoundItemViewModel;
        if (e.EditingElement is TextBox box)
        {
            box.Focus();
            box.SelectAll();
        }
    }

    private void SoundGrid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        var sound = _renaming;
        _renaming = null;
        if (e.EditAction != DataGridEditAction.Commit || sound is null || e.EditingElement is not TextBox box)
            return;
        // The list may reorder itself after the rename: let the grid finish editing first.
        var title = box.Text;
        Dispatcher.BeginInvoke(() => _viewModel.RenameSound(sound, title), DispatcherPriority.Background);
    }

    // A click on a column header picks the matching order instead of a temporary sort.
    private void SoundGrid_Sorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        if (e.Column == TitleColumn)
            _viewModel.CurrentSort = SoundSort.Name;
        else if (e.Column == DurationColumn)
            _viewModel.CurrentSort = _viewModel.CurrentSort == SoundSort.Shortest ? SoundSort.Longest : SoundSort.Shortest;
    }

    // ---- Reorder by dragging rows ------------------------------------------------------------

    private bool _draggingRows;
    private InsertionAdorner? _insertionLine;

    /// <summary>The sound the dragged rows would go in front of (null: at the end), and where to draw the line.</summary>
    private (SoundItemViewModel? before, double y)? DropPosition(DragEventArgs e)
    {
        var row = FindAncestor<DataGridRow>(SoundGrid.InputHitTest(e.GetPosition(SoundGrid)) as DependencyObject);
        if (row?.Item is not SoundItemViewModel over)
        {
            // Below the last row: at the end.
            var sounds = _viewModel.VisibleSounds;
            if (sounds.Count == 0 || SoundGrid.ItemContainerGenerator.ContainerFromItem(sounds[^1]) is not DataGridRow last)
                return null;
            return (null, last.TranslatePoint(new Point(0, last.ActualHeight), SoundGrid).Y);
        }
        bool lowerHalf = e.GetPosition(row).Y > row.ActualHeight / 2;
        var list = _viewModel.VisibleSounds;
        int index = list is IList<SoundItemViewModel> l ? l.IndexOf(over) : -1;
        var before = lowerHalf ? (index + 1 < list.Count ? list[index + 1] : null) : over;
        double y = row.TranslatePoint(new Point(0, lowerHalf ? row.ActualHeight : 0), SoundGrid).Y;
        return (before, y);
    }

    private void SoundGrid_DragOver(object sender, DragEventArgs e)
    {
        if (!_draggingRows || !e.Data.GetDataPresent(SoundsDataFormat))
            return; // files dropped from Explorer go on to the window
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        AutoScroll(e);
        if (_viewModel.SearchText.Trim().Length > 0 || DropPosition(e) is not { } drop)
        {
            HideInsertionLine();
            return;
        }
        e.Effects = DragDropEffects.Move;
        if (_insertionLine is null && AdornerLayer.GetAdornerLayer(SoundGrid) is { } layer)
        {
            _insertionLine = new InsertionAdorner(SoundGrid, (Brush)FindResource("AccentBrush"));
            layer.Add(_insertionLine);
        }
        if (_insertionLine is not null)
            _insertionLine.Y = drop.y;
    }

    private void SoundGrid_DragLeave(object sender, DragEventArgs e) => HideInsertionLine();

    private void SoundGrid_Drop(object sender, DragEventArgs e)
    {
        if (!_draggingRows || e.Data.GetData(SoundsDataFormat) is not List<SoundItemViewModel> sounds)
            return;
        e.Handled = true;
        var drop = DropPosition(e);
        HideInsertionLine();
        if (drop is not { } position || !_viewModel.MoveSounds(sounds, position.before))
            return;
        // Keep the moved rows selected, so they can be dragged again.
        SoundGrid.SelectedItems.Clear();
        foreach (var sound in sounds)
            SoundGrid.SelectedItems.Add(sound);
    }

    private void HideInsertionLine()
    {
        if (_insertionLine is null)
            return;
        AdornerLayer.GetAdornerLayer(SoundGrid)?.Remove(_insertionLine);
        _insertionLine = null;
    }

    // Near the top or bottom edge the list scrolls, so a sound can be carried far.
    private void AutoScroll(DragEventArgs e)
    {
        if (FindDescendant<ScrollViewer>(SoundGrid) is not { } scroll)
            return;
        const double edge = 28;
        double y = e.GetPosition(SoundGrid).Y;
        if (y < edge + 26) // the header row is about 26 px tall
            scroll.LineUp();
        else if (y > SoundGrid.ActualHeight - edge)
            scroll.LineDown();
    }

    private static T? FindDescendant<T>(DependencyObject parent) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match)
                return match;
            if (FindDescendant<T>(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    // ---- Drop targets ------------------------------------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await _viewModel.AddPathsAsync(paths);
    }

    // WPF does not scroll by itself during drag and drop: nudge the favourites when the pointer is near an edge.
    private void HotbarScroll_PreviewDragOver(object sender, DragEventArgs e)
    {
        const double edge = 24, step = 8;
        var y = e.GetPosition(HotbarScroll).Y;
        if (y < edge)
            HotbarScroll.ScrollToVerticalOffset(HotbarScroll.VerticalOffset - step);
        else if (y > HotbarScroll.ActualHeight - edge)
            HotbarScroll.ScrollToVerticalOffset(HotbarScroll.VerticalOffset + step);
    }

    private void HotbarSlot_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(SoundsDataFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void HotbarSlot_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotbarSlotViewModel slot &&
            e.Data.GetData(SoundsDataFormat) is List<SoundItemViewModel> { Count: > 0 } sounds)
            _viewModel.AssignHotbarSlot(slot, sounds[0]);
        e.Handled = true;
    }

    private void ClearHotbarSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotbarSlotViewModel slot)
            _viewModel.ClearHotbarSlotCommand.Execute(slot);
    }

    private void PlayHotbarSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotbarSlotViewModel slot)
            _viewModel.PlayHotbarSlotCommand.Execute(slot);
    }

    private void PreviewHotbarSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotbarSlotViewModel slot)
            _viewModel.PreviewHotbarSlotCommand.Execute(slot);
    }

    private void PlayHotbarSlotOnMic_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotbarSlotViewModel slot)
            _viewModel.PlayHotbarSlotOnMicCommand.Execute(slot);
    }

    private void CategoryItem_DragOver(object sender, DragEventArgs e)
    {
        bool accepts = e.Data.GetDataPresent(SoundsDataFormat) &&
                       (sender as FrameworkElement)?.DataContext is CategoryNodeViewModel { Model: not null };
        e.Effects = accepts ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void CategoryItem_Drop(object sender, DragEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CategoryNodeViewModel node &&
            e.Data.GetData(SoundsDataFormat) is List<SoundItemViewModel> sounds)
            _viewModel.AddSoundsToCategory(node, sounds);
        e.Handled = true;
    }

    private void CategoryTree_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<TreeViewItem>(e.OriginalSource as DependencyObject) is { } item)
        {
            item.IsSelected = true;
            item.Focus();
        }
    }

    private static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element is not null and not T)
        {
            element = element is Visual or Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as T;
    }
}
