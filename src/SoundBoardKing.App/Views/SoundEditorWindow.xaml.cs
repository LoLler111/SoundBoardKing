using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;

namespace SoundBoardKing.App.Views;

/// <summary>The sound editor; modal, so the library does not change under it while it is open.</summary>
public partial class SoundEditorWindow : Window
{
    private readonly SoundEditorViewModel _model;
    private bool _closeConfirmed;

    public SoundEditorWindow(MainViewModel main, SoundItemViewModel sound)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _model = new SoundEditorViewModel(main, sound, new DialogService(this));
        DataContext = _model;
        _model.CloseRequested += (_, _) =>
        {
            _closeConfirmed = true;
            Close();
        };
        _model.PropertyChanged += Model_PropertyChanged;
        Wave.ViewChanged += (_, _) => UpdateScrollBar();
        Loaded += async (_, _) =>
        {
            await _model.LoadAsync();
            Wave.Focus();
        };
        Closed += (_, _) => _model.Dispose();
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The view follows the playhead as it moves past the right edge.
        if (e.PropertyName == nameof(SoundEditorViewModel.Playhead) && _model.Playhead >= 0)
            Wave.EnsureVisible(_model.Playhead);
    }

    private void UpdateScrollBar()
    {
        double total = Wave.TotalFrames;
        double view = Wave.ViewFrames;
        bool needed = total > 0 && view < total;
        WaveScroll.Visibility = needed ? Visibility.Visible : Visibility.Collapsed;
        if (!needed)
            return;
        WaveScroll.Minimum = 0;
        WaveScroll.Maximum = total - view;
        WaveScroll.ViewportSize = view;
        WaveScroll.LargeChange = view * 0.9;
        WaveScroll.SmallChange = view / 10;
        WaveScroll.Value = Wave.ViewStart;
    }

    private void WaveScroll_Scroll(object sender, ScrollEventArgs e) => Wave.ScrollTo(e.NewValue);

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Wave.Zoom(2, _model.HasSelection ? null : _model.SelectionStart);

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Wave.Zoom(0.5);

    private void ZoomSelection_Click(object sender, RoutedEventArgs e) => Wave.ZoomToSelection();

    private void ZoomFit_Click(object sender, RoutedEventArgs e) => Wave.ZoomToFit();

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // Enter confirms a typed time at once, without leaving the box.
    private void TimeBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && sender is TextBox box)
        {
            box.GetBindingExpression(TextBox.TextProperty)?.UpdateSource();
            box.SelectAll();
            e.Handled = true;
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Typing in a box or choosing in a list keeps its keys.
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem || !_model.IsReady)
            return;
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        ICommand? command = (e.Key, ctrl) switch
        {
            (Key.Space, false) => _model.PlayPauseCommand,
            (Key.Z, true) when shift => _model.RedoCommand,
            (Key.Z, true) => _model.UndoCommand,
            (Key.Y, true) => _model.RedoCommand,
            (Key.X, true) => _model.CutCommand,
            (Key.C, true) => _model.CopyCommand,
            (Key.V, true) => _model.PasteCommand,
            (Key.A, true) => _model.SelectAllCommand,
            (Key.Delete, false) => _model.DeleteSelectionCommand,
            _ => null,
        };
        if (command is null)
        {
            if (e.Key == Key.Home && !ctrl)
            {
                _model.SelectionStart = _model.SelectionEnd = 0;
                Wave.EnsureVisible(0);
                e.Handled = true;
            }
            else if (e.Key == Key.End && !ctrl && _model.Clip is { } clip)
            {
                _model.SelectionEnd = clip.Frames;
                _model.SelectionStart = clip.Frames;
                Wave.EnsureVisible(clip.Frames);
                e.Handled = true;
            }
            return;
        }
        if (command.CanExecute(null))
            command.Execute(null);
        e.Handled = true;
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
            return;
        if (_model.IsBusy)
        {
            // Saving is quick and must not be cut; a separation can be stopped.
            if (_model.IsBusyCancellable)
                _model.CancelBusyCommand.Execute(null);
            e.Cancel = true;
            return;
        }
        if (_model.IsDirty && !MessageDialog.Confirm(this, "Chiudere senza salvare le modifiche?", yes: "Chiudi", no: "Continua a modificare"))
        {
            e.Cancel = true;
            return;
        }
        _model.StopPreview();
    }
}
