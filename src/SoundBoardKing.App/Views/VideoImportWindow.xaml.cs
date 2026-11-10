using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using SoundBoardKing.App.Services;
using SoundBoardKing.App.ViewModels;
using SoundBoardKing.Core.Video;

namespace SoundBoardKing.App.Views;

/// <summary>
/// Stays open beside the main window, so several pieces of the same video can be added. The video
/// player is silent and follows the waveform: the frame of the selection's edge while choosing, and
/// the sound being listened to while it plays.
/// </summary>
public partial class VideoImportWindow : Window
{
    // Further apart than this, the playing video is moved back in step with the sound.
    private static readonly TimeSpan MaxDrift = TimeSpan.FromMilliseconds(250);

    private readonly MainViewModel _main;
    private readonly VideoImportViewModel _model;
    private readonly DispatcherTimer _seekTimer;
    private bool _videoOpen;
    private TimeSpan? _pendingSeek;

    public VideoImportWindow(MainViewModel main)
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);
        _main = main;
        _model = new VideoImportViewModel(main, new DialogService(this));
        DataContext = _model;
        _model.PropertyChanged += Model_PropertyChanged;
        _model.EditRequested += (_, sound) => new SoundEditorWindow(_main, sound) { Owner = this }.ShowDialog();
        Wave.ViewChanged += (_, _) => UpdateScrollBar();
        // Dragging along the waveform asks for a frame at every move: the player gets the latest, a few times a second.
        _seekTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _seekTimer.Tick += (_, _) => ApplyPendingSeek();
        Loaded += (_, _) =>
        {
            LinkBox.Focus();
            // A link already copied in the browser is ready to download.
            if (VideoLinkOnClipboard() is { } link)
            {
                _model.Link = link;
                LinkBox.SelectAll();
            }
        };
        Closed += (_, _) =>
        {
            _seekTimer.Stop();
            Video.Close();
            _model.Dispose();
        };
    }

    private void Model_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(VideoImportViewModel.VideoPath):
                OpenVideo(_model.VideoPath);
                break;
            case nameof(VideoImportViewModel.VideoTime) when !_model.IsPreviewing:
                RequestSeek(_model.VideoTime);
                break;
            case nameof(VideoImportViewModel.IsPreviewing):
                if (_model.IsPreviewing)
                {
                    StartVideo();
                }
                else if (_videoOpen)
                {
                    // Back to the frame of the selection's edge, as the waveform drops its playhead.
                    Video.Pause();
                    RequestSeek(_model.VideoTime);
                }
                break;
            case nameof(VideoImportViewModel.Playhead) when _model.Playhead >= 0:
                Wave.EnsureVisible(_model.Playhead);
                KeepVideoInStep();
                break;
        }
    }

    // ---- The player -----------------------------------------------------------------------------

    private void OpenVideo(string? path)
    {
        _videoOpen = false;
        _pendingSeek = null;
        if (path is null)
        {
            Video.Close();
            Video.Source = null;
            return;
        }
        Video.Source = new Uri(path);
        // Paused right away: with scrubbing on, the first frame shows and the player is ready to jump.
        Video.Pause();
    }

    private void Video_MediaOpened(object sender, RoutedEventArgs e)
    {
        _videoOpen = true;
        if (_model.IsPreviewing)
            StartVideo();
        else
            RequestSeek(_model.VideoTime);
    }

    private async void Video_MediaFailed(object? sender, ExceptionRoutedEventArgs e)
    {
        _videoOpen = false;
        await _model.VideoFailedAsync();
    }

    private void RequestSeek(TimeSpan time)
    {
        if (!_videoOpen)
            return;
        _pendingSeek = time;
        if (!_seekTimer.IsEnabled)
        {
            ApplyPendingSeek();
            _seekTimer.Start();
        }
    }

    private void ApplyPendingSeek()
    {
        if (_pendingSeek is not { } time || !_videoOpen)
        {
            _seekTimer.Stop();
            return;
        }
        _pendingSeek = null;
        Video.Position = time;
    }

    private void StartVideo()
    {
        if (!_videoOpen || _model.Clip is not { } clip)
            return;
        _pendingSeek = null;
        Video.Position = clip.FramesToTime((long)Math.Max(0, _model.Playhead));
        Video.Play();
    }

    private void KeepVideoInStep()
    {
        if (!_videoOpen || !_model.IsPreviewing || _model.Clip is not { } clip)
            return;
        var heard = clip.FramesToTime((long)_model.Playhead);
        if ((Video.Position - heard).Duration() > MaxDrift)
            Video.Position = heard;
    }

    private void Video_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_model.IsReady)
            _model.PlayPauseCommand.Execute(null);
    }

    // ---- Waveform -------------------------------------------------------------------------------

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

    // ---- Keys -----------------------------------------------------------------------------------

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem)
            return;

        // Ctrl+V of a link anywhere but in a box downloads it.
        if (e.Key == Key.V && ctrl && _model.IsIdle && VideoLinkOnClipboard() is { } link)
        {
            _model.Link = link;
            _model.LoadLinkCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (!_model.IsReady || _model.Clip is not { } clip)
            return;

        switch (e.Key)
        {
            case Key.Space:
                _model.PlayPauseCommand.Execute(null);
                break;
            // A tenth of a second; with Shift a second, with Ctrl about one frame.
            case Key.Left or Key.Right:
                double step = ctrl ? 1.0 / 30 : shift ? 1 : 0.1;
                _model.StopPreview();
                _model.Nudge(e.Key == Key.Left ? -step : step);
                Wave.EnsureVisible(_model.SelectionStart);
                break;
            case Key.Home when !ctrl:
                _model.SelectionStart = _model.SelectionEnd = 0;
                Wave.EnsureVisible(0);
                break;
            case Key.End when !ctrl:
                _model.SelectionEnd = clip.Frames;
                _model.SelectionStart = clip.Frames;
                Wave.EnsureVisible(clip.Frames);
                break;
            case Key.A when ctrl:
                _model.SelectionStart = 0;
                _model.SelectionEnd = clip.Frames;
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ---- Drop targets ---------------------------------------------------------------------------

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = _model.IsIdle && (e.Data.GetDataPresent(DataFormats.FileDrop) || LinkIn(e.Data) is not null)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (!_model.IsIdle)
            return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths && File.Exists(paths[0]))
        {
            await _model.LoadFileAsync(paths[0]);
        }
        else if (LinkIn(e.Data) is { } link)
        {
            _model.Link = link;
            _model.LoadLinkCommand.Execute(null);
        }
    }

    private static string? LinkIn(IDataObject data) =>
        data.GetData(DataFormats.UnicodeText) is string text && VideoDownloader.IsLink(text) ? text.Trim() : null;

    private static string? VideoLinkOnClipboard()
    {
        try
        {
            return Clipboard.ContainsText() && Clipboard.GetText() is var text && VideoDownloader.IsLink(text) ? text.Trim() : null;
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            return null; // another program holds the clipboard
        }
    }
}
