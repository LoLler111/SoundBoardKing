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
using SoundBoardKing.Core.Myinstants;
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
        RestoreHotbarLayout();
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
        _viewModel.EditorRequested += (_, sound) => new SoundEditorWindow(_viewModel, sound) { Owner = this }.ShowDialog();
        _viewModel.HotbarImported += OnHotbarImported;
        PreviewMouseDown += ClosePadOnClickElsewhere;
        Deactivated += ClosePad;
        LocationChanged += ClosePad;
        SizeChanged += ClosePad;
        _viewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.HasArtists))
                ArtistColumn.Visibility = _viewModel.HasArtists ? Visibility.Visible : Visibility.Collapsed;
            else if (e.PropertyName == nameof(MainViewModel.IsAllSounds))
                CategoryColumn.Visibility = _viewModel.IsAllSounds ? Visibility.Visible : Visibility.Collapsed;
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
        await _viewModel.ScanSoundFolderAsync(atStartup: true);
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
        _storage.Local.HotbarRows = _hotbarRows;
        _storage.Local.HotbarHeight = HotbarScroll.Height;
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
            // Queued: with the server off the await finishes at once, still inside this close.
            Dispatcher.BeginInvoke(Close);
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

    // Something closed the pad: un-press its button too.
    private void PadPopup_Closed(object? sender, EventArgs e) => PadToggle.IsChecked = false;

    // The pad stays open on its own (a popup that closes itself on outside clicks swallows them,
    // and the clicks on its button are needed while it is open): close it here instead, on a click
    // elsewhere in the window, when the window moves or another one comes forward.
    private void ClosePadOnClickElsewhere(object sender, MouseButtonEventArgs e)
    {
        if (PadToggle.IsChecked == true && _padPress is null && !PadToggle.IsMouseOver)
            PadToggle.IsChecked = false;
    }

    private void ClosePad(object? sender, EventArgs e)
    {
        if (_padPress is null)
            PadToggle.IsChecked = false;
    }

    // ---- Effect pad held from its button ----------------------------------------------------

    // A click opens or closes the pad, a double click resets it. Pressed and dragged (the pad open or
    // not), the pad shows and its point follows the mouse from where it was; letting go puts strength
    // and speed back as they were and closes it, unless the right button was pressed meanwhile (the
    // phone's second finger): then they stay. Right again unlocks.
    private Point? _padPress;
    private bool _padHolding, _padLocked, _padDoubleClick, _padOpenBeforeClick;
    private double _padStartAmount, _padStartSpeed;

    private void PadToggle_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true; // the toggle opens on the release instead, if it was not a drag
        _padPress = e.GetPosition(PadToggle);
        _padHolding = _padLocked = false;
        _padDoubleClick = e.ClickCount == 2;
        if (_padDoubleClick)
            _viewModel.ResetPadCommand.Execute(null);
        else
            _padOpenBeforeClick = PadToggle.IsChecked == true;
        PadToggle.CaptureMouse();
    }

    private void PadToggle_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_padPress is not { } start)
            return;
        var delta = e.GetPosition(PadToggle) - start;
        if (!_padHolding)
        {
            if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            StartPadHold();
        }
        // The mouse moves the point as if it were on the pad, starting from where the point was.
        _viewModel.EffectAmount = Math.Round(Math.Clamp(_padStartAmount + delta.X / Pad.Width * 100, 0, 100));
        _viewModel.PadSpeed = Math.Round(Math.Clamp(_padStartSpeed - delta.Y / Pad.Height * 200, -100, 100));
    }

    private void PadToggle_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_padPress is null)
            return;
        e.Handled = true;
        EndPadPress(click: true);
    }

    private void PadToggle_PreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_padPress is null)
            return;
        e.Handled = true;
        if (!_padHolding)
            StartPadHold();
        _padLocked = !_padLocked;
        UpdatePadHoldHint();
    }

    private void PadToggle_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e) => e.Handled = _padPress is not null;

    // Taken away by another window or Alt+Tab: as if the button was let go.
    private void PadToggle_LostMouseCapture(object sender, MouseEventArgs e) => EndPadPress(click: false);

    private void StartPadHold()
    {
        _padHolding = true;
        _padStartAmount = _viewModel.EffectAmount;
        _padStartSpeed = _viewModel.PadSpeed;
        PadToggle.IsChecked = true;
        UpdatePadHoldHint();
    }

    private void UpdatePadHoldHint()
    {
        PadHoldHint.Text = _padLocked ? "resta così al rilascio" : "tasto destro: resta così";
        PadHoldHint.Visibility = _padHolding ? Visibility.Visible : Visibility.Collapsed;
    }

    private void EndPadPress(bool click)
    {
        if (_padPress is null)
            return;
        _padPress = null; // first: releasing the capture comes back here
        PadToggle.ReleaseMouseCapture();
        if (!_padHolding)
        {
            // The first click of a double click opened or closed the pad: the second puts it back.
            if (click)
                PadToggle.IsChecked = _padDoubleClick ? _padOpenBeforeClick : !_padOpenBeforeClick;
            return;
        }
        _padHolding = false;
        if (!_padLocked)
        {
            _viewModel.EffectAmount = _padStartAmount;
            _viewModel.PadSpeed = _padStartSpeed;
        }
        PadToggle.IsChecked = false;
        UpdatePadHoldHint();
    }

    private void PhoneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_remote is not null)
            new PhoneWindow(_remote) { Owner = this }.ShowDialog();
    }

    private MyinstantsWindow? _myinstants;

    private void MyinstantsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_myinstants is null)
        {
            _myinstants = new MyinstantsWindow(new MyinstantsViewModel(_viewModel)) { Owner = this };
            _myinstants.Closed += (_, _) => _myinstants = null;
            _myinstants.Show();
        }
        else
        {
            _myinstants.Activate();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
        else if (e.Key == Key.E && Keyboard.Modifiers == ModifierKeys.Control && Keyboard.FocusedElement is not TextBox)
        {
            _viewModel.EditSoundCommand.Execute(null);
            e.Handled = true;
        }
        // Ctrl+V of a Myinstants link outside a text box adds that sound.
        else if (e.Key == Key.V && Keyboard.Modifiers == ModifierKeys.Control &&
                 Keyboard.FocusedElement is not TextBox && MyinstantsLinkOnClipboard() is { } link)
        {
            e.Handled = true;
            _ = _viewModel.AddMyinstantsLinkAsync(link);
        }
    }

    private static string? MyinstantsLinkOnClipboard()
    {
        try
        {
            return Clipboard.ContainsText() && Clipboard.GetText() is var text && MyinstantsClient.IsSoundLink(text) ? text : null;
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            return null; // another app holds the clipboard
        }
    }

    private static string? MyinstantsLinkIn(IDataObject data) =>
        !data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.UnicodeText) is string text &&
        MyinstantsClient.IsSoundLink(text) ? text : null;

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
        else if (e.Column == AddedOnColumn)
            _viewModel.CurrentSort = SoundSort.Newest;
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
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) || MyinstantsLinkIn(e.Data) is not null
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths)
            await _viewModel.AddPathsAsync(paths);
        // A link dragged from the browser.
        else if (MyinstantsLinkIn(e.Data) is { } link)
            await _viewModel.AddMyinstantsLinkAsync(link);
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

    // ---- Favourites layout -------------------------------------------------------------------

    // The columns grow with the rows, so the buttons shrink both ways and keep their shape:
    // 1 row of 4 up to 5 rows of 20, the whole page of 100 slots in view like SoundPad's 5 x 20 hotbar.
    private const int ColumnsPerRow = 4, MaxHotbarRows = 5;
    private const double MinHotbarHeight = 40, MinLibraryHeight = 160;

    private UniformGrid? _hotbarGrid;
    private int _hotbarRows = 2;

    private void RestoreHotbarLayout()
    {
        _hotbarRows = Math.Clamp(_storage.Local.HotbarRows, 1, MaxHotbarRows);
        HotbarScroll.Height = Math.Max(_storage.Local.HotbarHeight, MinHotbarHeight);
        ApplyHotbarLayout();
    }

    private void SetHotbarRows(int rows)
    {
        rows = Math.Clamp(rows, 1, MaxHotbarRows);
        if (rows == _hotbarRows)
            return;
        _hotbarRows = rows;
        ApplyHotbarLayout();
    }

    // Each row takes an equal share of the area; titles get as big and as many lines as the button allows.
    private void ApplyHotbarLayout()
    {
        int columns = ColumnsPerRow * _hotbarRows;
        int totalRows = (Hotbar.SlotsPerPage + columns - 1) / columns;
        double cell = HotbarScroll.Height / _hotbarRows;
        if (_hotbarGrid is not null)
            _hotbarGrid.Columns = columns;
        HotbarItems.Height = cell * totalRows;

        HotbarRowsText.Text = _hotbarRows.ToString();
        FewerRowsButton.IsEnabled = _hotbarRows > 1;
        MoreRowsButton.IsEnabled = _hotbarRows < MaxHotbarRows;

        const double margin = 6; // the button's Margin, both sides
        double scrollBar = SystemParameters.VerticalScrollBarWidth;
        double width = HotbarScroll.ActualWidth > 0 ? (HotbarScroll.ActualWidth - scrollBar) / columns - margin : 110;
        double height = cell - margin;
        // Too small for number and category: only the title, with less padding around it.
        bool compact = height < 44 || width < 70;
        var padding = compact ? new Thickness(3, 1, 3, 1) : new Thickness(6, 4, 6, 4);
        var titleMargin = compact ? new Thickness(1, 0, 1, 0) : new Thickness(4, 10, 4, 0);

        double size = Math.Clamp(Math.Round(Math.Min(height / 4.8, width / 8)), 9, 14);
        double line = Math.Round(size * 1.3);
        double room = height - padding.Top - padding.Bottom - titleMargin.Top - 2; // 2: the button's border
        int lines = Math.Max(1, (int)(room / line));

        var resources = HotbarItems.Resources;
        resources["SlotPadding"] = padding;
        resources["SlotHeaderVisibility"] = compact ? Visibility.Collapsed : Visibility.Visible;
        resources["SlotTitleSize"] = size;
        resources["SlotTitleLine"] = line;
        resources["SlotTitleMaxHeight"] = lines * line;
        resources["SlotTitleMargin"] = titleMargin;
    }

    private void HotbarGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _hotbarGrid = (UniformGrid)sender;
        ApplyHotbarLayout();
    }

    // The width decides how much room each button's title has.
    private void HotbarScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged)
            ApplyHotbarLayout();
    }

    // After a SoundPad import the favourites are laid out as wide as they were there, when a row count gives that width.
    private void OnHotbarImported(object? sender, int columns)
    {
        if (columns > 0 && columns % ColumnsPerRow == 0)
            SetHotbarRows(columns / ColumnsPerRow);
    }

    private void FewerRows_Click(object sender, RoutedEventArgs e) => SetHotbarRows(_hotbarRows - 1);

    private void MoreRows_Click(object sender, RoutedEventArgs e) => SetHotbarRows(_hotbarRows + 1);

    // Ctrl + wheel zooms the favourites like a browser page: down = more rows, smaller buttons.
    private void HotbarScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control)
            return;
        SetHotbarRows(_hotbarRows + (e.Delta < 0 ? 1 : -1));
        e.Handled = true;
    }

    // Dragging the top edge up makes the favourites taller, as long as the sound list keeps some room.
    private void HotbarResize_DragDelta(object sender, DragDeltaEventArgs e)
    {
        double max = HotbarScroll.Height + Math.Max(0, LibraryArea.ActualHeight - MinLibraryHeight);
        HotbarScroll.Height = Math.Clamp(HotbarScroll.Height - e.VerticalChange, MinHotbarHeight, max);
        ApplyHotbarLayout();
    }

    // A smaller window (or a saved height from a bigger screen) takes room from the favourites, not from the list.
    private void LibraryArea_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double missing = MinLibraryHeight - LibraryArea.ActualHeight;
        if (missing <= 0 || HotbarScroll.Height <= MinHotbarHeight)
            return;
        HotbarScroll.Height = Math.Max(HotbarScroll.Height - missing, MinHotbarHeight);
        ApplyHotbarLayout();
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

    private void EditHotbarSlot_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is HotbarSlotViewModel { Sound: { } sound })
            _viewModel.EditSoundCommand.Execute(sound);
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
