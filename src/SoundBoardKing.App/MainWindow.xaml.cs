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
        _viewModel = new MainViewModel(
            new ProfileStorage(AppPaths.Default, AppContext.BaseDirectory),
            new AudioEngine(micFeed),
            micFeed,
            new HotkeyService(),
            new DialogService(this));
        DataContext = _viewModel;
        _viewModel.RestartRequested += (_, _) =>
        {
            // The new instance waits for this one to exit before taking the single-instance lock.
            Process.Start(Environment.ProcessPath!, $"--after {Environment.ProcessId}");
            Close();
        };

        Loaded += OnLoaded;
        Closed += (_, _) => _viewModel.Dispose();
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _viewModel.Initialize();
        _remote = new RemoteHost(_viewModel, Dispatcher);
        if (_viewModel.RemoteEnabled)
            await _remote.StartAsync();
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
