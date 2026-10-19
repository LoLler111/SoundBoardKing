using System.Diagnostics;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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
        _viewModel = new MainViewModel(
            new ProfileStorage(AppPaths.Default, appFolder, allowAppFolder: !updates.IsSetupInstall),
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

    // The phone server shuts down asynchronously, so the first close request waits for it.
    protected override async void OnClosing(CancelEventArgs e)
    {
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
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed)
            return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _dragArmed = false;
        var sounds = SoundGrid.SelectedItems.OfType<SoundItemViewModel>().ToList();
        if (sounds.Count > 0)
            DragDrop.DoDragDrop(SoundGrid, new DataObject(SoundsDataFormat, sounds), DragDropEffects.Copy);
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
