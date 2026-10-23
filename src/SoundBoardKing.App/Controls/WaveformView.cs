using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SoundBoardKing.Audio.Editing;

namespace SoundBoardKing.App.Controls;

/// <summary>
/// The sound editor's waveform: one lane per channel, a time ruler, the selection and the playhead.
/// Drag to select, click to place the cursor, Shift+click to stretch the selection; the wheel scrolls
/// and Ctrl+wheel zooms around the mouse. Positions are frames of <see cref="Audio"/>.
/// </summary>
public sealed class WaveformView : FrameworkElement
{
    private const double RulerHeight = 20;
    private const int PeakBlock = 256;

    public static readonly DependencyProperty AudioProperty = DependencyProperty.Register(
        nameof(Audio), typeof(AudioClip), typeof(WaveformView),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, (d, e) => ((WaveformView)d).OnClipChanged((AudioClip?)e.OldValue)));

    public static readonly DependencyProperty SelectionStartProperty = DependencyProperty.Register(
        nameof(SelectionStart), typeof(int), typeof(WaveformView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty SelectionEndProperty = DependencyProperty.Register(
        nameof(SelectionEnd), typeof(int), typeof(WaveformView),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    /// <summary>Where playback is, in frames; negative when nothing plays.</summary>
    public static readonly DependencyProperty PlayheadProperty = DependencyProperty.Register(
        nameof(Playhead), typeof(double), typeof(WaveformView),
        new FrameworkPropertyMetadata(-1.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Brush BackgroundBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x17, 0x17, 0x1B)));
    private static readonly Brush WaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x62, 0xC6, 0xD9)));
    private static readonly Brush SelectedWaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xF2, 0xB3, 0x3D)));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x50, 0xF2, 0xB3, 0x3D)));
    private static readonly Brush TextBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x9A, 0x9A, 0xA6)));
    private static readonly Pen LinePen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0x34, 0x34, 0x3D)), 1));
    private static readonly Pen CursorPen = Frozen(new Pen(new SolidColorBrush(Color.FromRgb(0xF2, 0xB3, 0x3D)), 1));
    private static readonly Pen PlayheadPen = Frozen(new Pen(Brushes.White, 1.5));

    // Min and max of every block of PeakBlock frames, per channel: drawing a zoomed-out view reads these.
    private float[][] _peakMin = [];
    private float[][] _peakMax = [];
    private double _viewStart;
    private double _framesPerPixel;
    private bool _fitWhole = true;
    private int _dragAnchor = -1;

    public WaveformView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = Cursors.IBeam;
    }

    public AudioClip? Audio
    {
        get => (AudioClip?)GetValue(AudioProperty);
        set => SetValue(AudioProperty, value);
    }

    public int SelectionStart
    {
        get => (int)GetValue(SelectionStartProperty);
        set => SetValue(SelectionStartProperty, value);
    }

    public int SelectionEnd
    {
        get => (int)GetValue(SelectionEndProperty);
        set => SetValue(SelectionEndProperty, value);
    }

    public double Playhead
    {
        get => (double)GetValue(PlayheadProperty);
        set => SetValue(PlayheadProperty, value);
    }

    /// <summary>The visible part changed (zoom or scroll): the window moves its scroll bar.</summary>
    public event EventHandler? ViewChanged;

    /// <summary>First frame on the left edge.</summary>
    public double ViewStart => _viewStart;

    /// <summary>How many frames fit in the width.</summary>
    public double ViewFrames => _framesPerPixel * Math.Max(1, ActualWidth);

    public int TotalFrames => Audio?.Frames ?? 0;

    /// <summary>Shows the whole sound, and keeps doing so as the window resizes.</summary>
    public void ZoomToFit()
    {
        _fitWhole = true;
        UpdateFit();
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Zooms by <paramref name="factor"/> (2 = twice as close) around frame <paramref name="anchor"/>, or the middle.</summary>
    public void Zoom(double factor, double? anchor = null)
    {
        if (Audio is null || ActualWidth <= 0)
            return;
        double focus = anchor ?? _viewStart + ViewFrames / 2;
        double offset = (focus - _viewStart) / _framesPerPixel;
        double fit = (double)Audio.Frames / ActualWidth;
        _framesPerPixel = Math.Clamp(_framesPerPixel / factor, 1.0 / 16, Math.Max(fit, 1.0 / 16));
        _fitWhole = _framesPerPixel >= fit;
        ScrollTo(focus - offset * _framesPerPixel);
    }

    /// <summary>Zooms so the selection fills the view.</summary>
    public void ZoomToSelection()
    {
        int start = Math.Min(SelectionStart, SelectionEnd), end = Math.Max(SelectionStart, SelectionEnd);
        if (Audio is null || end - start < 2 || ActualWidth <= 0)
            return;
        double margin = (end - start) * 0.05;
        _framesPerPixel = Math.Max(1.0 / 16, (end - start + 2 * margin) / ActualWidth);
        _fitWhole = false;
        ScrollTo(start - margin);
    }

    public void ScrollTo(double frame)
    {
        double max = Math.Max(0, TotalFrames - ViewFrames);
        _viewStart = Math.Clamp(frame, 0, max);
        InvalidateVisual();
        ViewChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Scrolls just enough to show <paramref name="frame"/>, e.g. the playhead leaving the view.</summary>
    public void EnsureVisible(double frame)
    {
        if (frame < _viewStart || frame > _viewStart + ViewFrames)
            ScrollTo(frame - ViewFrames * 0.1);
    }

    private void OnClipChanged(AudioClip? old)
    {
        var clip = Audio;
        BuildPeaks(clip);
        // A new sound shows whole; an edit of the same one keeps the zoom where it is.
        if (old is null)
            _fitWhole = true;
        UpdateFit();
        ScrollTo(_viewStart);
    }

    private void UpdateFit()
    {
        if (Audio is null || ActualWidth <= 0)
            return;
        double fit = (double)Audio.Frames / ActualWidth;
        if (_fitWhole || _framesPerPixel <= 0 || _framesPerPixel > fit)
        {
            _framesPerPixel = Math.Max(fit, 1.0 / 16);
            _viewStart = 0;
        }
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        UpdateFit();
        ScrollTo(_viewStart);
    }

    private void BuildPeaks(AudioClip? clip)
    {
        if (clip is null)
        {
            _peakMin = _peakMax = [];
            return;
        }
        int ch = clip.Channels;
        int blocks = (clip.Frames + PeakBlock - 1) / PeakBlock;
        _peakMin = new float[ch][];
        _peakMax = new float[ch][];
        for (int c = 0; c < ch; c++)
        {
            var min = _peakMin[c] = new float[blocks];
            var max = _peakMax[c] = new float[blocks];
            for (int b = 0; b < blocks; b++)
            {
                float lo = 0, hi = 0;
                int end = Math.Min(clip.Frames, (b + 1) * PeakBlock);
                for (int f = b * PeakBlock; f < end; f++)
                {
                    float s = clip.Samples[f * ch + c];
                    if (s < lo) lo = s;
                    if (s > hi) hi = s;
                }
                min[b] = lo;
                max[b] = hi;
            }
        }
    }

    private double FrameToX(double frame) => (frame - _viewStart) / _framesPerPixel;

    private int XToFrame(double x) => (int)Math.Clamp(Math.Round(_viewStart + x * _framesPerPixel), 0, TotalFrames);

    protected override void OnRender(DrawingContext dc)
    {
        double width = ActualWidth, height = ActualHeight;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, width, height));
        var clip = Audio;
        if (clip is null || width <= 0 || _framesPerPixel <= 0)
            return;

        int selStart = Math.Min(SelectionStart, SelectionEnd), selEnd = Math.Max(SelectionStart, SelectionEnd);
        double selX0 = FrameToX(selStart), selX1 = FrameToX(selEnd);
        if (selEnd > selStart)
            dc.DrawRectangle(SelectionBrush, null, new Rect(Math.Max(0, selX0), RulerHeight, Math.Max(1, Math.Min(width, selX1) - Math.Max(0, selX0)), height - RulerHeight));

        DrawRuler(dc, clip, width);

        int ch = clip.Channels;
        double laneHeight = (height - RulerHeight) / ch;
        for (int c = 0; c < ch; c++)
        {
            double top = RulerHeight + c * laneHeight;
            double mid = top + laneHeight / 2;
            double scale = laneHeight / 2 * 0.95;
            dc.DrawLine(LinePen, new Point(0, mid), new Point(width, mid));
            if (c > 0)
                dc.DrawLine(LinePen, new Point(0, top), new Point(width, top));

            // One vertical line per pixel from min to max: as fast zoomed out as in.
            var normal = new StreamGeometry();
            var selected = new StreamGeometry();
            using (var plain = normal.Open())
            using (var lit = selected.Open())
            {
                for (int x = 0; x < (int)Math.Ceiling(width); x++)
                {
                    double a = _viewStart + x * _framesPerPixel;
                    double b = a + _framesPerPixel;
                    if (a >= clip.Frames)
                        break;
                    var (lo, hi) = Range(clip, c, (int)a, Math.Max((int)a + 1, (int)Math.Min(b, clip.Frames)));
                    double y0 = mid - hi * scale, y1 = mid - lo * scale;
                    if (y1 - y0 < 1)
                        y1 = y0 + 1;
                    var ctx = selEnd > selStart && a >= selStart && a < selEnd ? lit : plain;
                    ctx.BeginFigure(new Point(x + 0.5, y0), false, false);
                    ctx.LineTo(new Point(x + 0.5, y1), true, false);
                }
            }
            normal.Freeze();
            selected.Freeze();
            dc.DrawGeometry(null, new Pen(WaveBrush, 1), normal);
            dc.DrawGeometry(null, new Pen(SelectedWaveBrush, 1), selected);
        }

        if (selEnd == selStart && selX0 >= 0 && selX0 <= width)
            dc.DrawLine(CursorPen, new Point(selX0, RulerHeight), new Point(selX0, height));
        if (Playhead >= 0)
        {
            double px = FrameToX(Playhead);
            if (px >= 0 && px <= width)
                dc.DrawLine(PlayheadPen, new Point(px, 0), new Point(px, height));
        }
    }

    private (float Lo, float Hi) Range(AudioClip clip, int channel, int start, int end)
    {
        float lo = 0, hi = 0;
        int ch = clip.Channels;
        int f = start;
        // Raw samples up to the next block edge, whole blocks from the cache, raw samples after.
        while (f < end && (f % PeakBlock != 0 || f + PeakBlock > end))
        {
            float s = clip.Samples[f * ch + channel];
            if (s < lo) lo = s;
            if (s > hi) hi = s;
            f++;
        }
        while (f + PeakBlock <= end)
        {
            int b = f / PeakBlock;
            if (_peakMin[channel][b] < lo) lo = _peakMin[channel][b];
            if (_peakMax[channel][b] > hi) hi = _peakMax[channel][b];
            f += PeakBlock;
        }
        for (; f < end; f++)
        {
            float s = clip.Samples[f * ch + channel];
            if (s < lo) lo = s;
            if (s > hi) hi = s;
        }
        return (lo, hi);
    }

    private void DrawRuler(DrawingContext dc, AudioClip clip, double width)
    {
        dc.DrawLine(LinePen, new Point(0, RulerHeight - 0.5), new Point(width, RulerHeight - 0.5));
        double secondsPerPixel = _framesPerPixel / clip.SampleRate;
        // The smallest step that leaves room for a label.
        double[] steps = [0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10, 15, 30, 60, 120, 300];
        double step = steps.FirstOrDefault(s => s / secondsPerPixel >= 70, 600);
        double first = Math.Floor(_viewStart / clip.SampleRate / step) * step;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        for (double t = first; ; t += step)
        {
            double x = FrameToX(t * clip.SampleRate);
            if (x > width)
                break;
            if (x < 0)
                continue;
            dc.DrawLine(LinePen, new Point(x, RulerHeight - 6), new Point(x, RulerHeight));
            var label = new FormattedText(FormatTime(t, step), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 10, TextBrush, dpi);
            dc.DrawText(label, new Point(x + 3, 2));
        }
    }

    private static string FormatTime(double seconds, double step)
    {
        var time = TimeSpan.FromSeconds(Math.Round(seconds, 3));
        string whole = time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
        return step >= 1 ? whole
            : step >= 0.1 ? whole + "." + (time.Milliseconds / 100)
            : step >= 0.01 ? whole + "." + (time.Milliseconds / 10).ToString("00")
            : whole + "." + time.Milliseconds.ToString("000");
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (Audio is null)
            return;
        Focus();
        int frame = XToFrame(e.GetPosition(this).X);
        if (e.ClickCount == 2)
        {
            SelectionStart = 0;
            SelectionEnd = TotalFrames;
            return;
        }
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            // Stretch from the far end of the current selection.
            int start = Math.Min(SelectionStart, SelectionEnd), end = Math.Max(SelectionStart, SelectionEnd);
            _dragAnchor = Math.Abs(frame - start) > Math.Abs(frame - end) ? start : end;
        }
        else
        {
            _dragAnchor = frame;
        }
        SetSelection(_dragAnchor, frame);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragAnchor < 0 || !IsMouseCaptured)
            return;
        double x = e.GetPosition(this).X;
        // Dragging past an edge scrolls the view along.
        if (x < 0)
            ScrollTo(_viewStart + x * _framesPerPixel);
        else if (x > ActualWidth)
            ScrollTo(_viewStart + (x - ActualWidth) * _framesPerPixel);
        SetSelection(_dragAnchor, XToFrame(Math.Clamp(x, 0, ActualWidth)));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        _dragAnchor = -1;
        ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Audio is null)
            return;
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            Zoom(e.Delta > 0 ? 1.5 : 1 / 1.5, _viewStart + e.GetPosition(this).X * _framesPerPixel);
        else
            ScrollTo(_viewStart - e.Delta / 120.0 * ViewFrames / 8);
        e.Handled = true;
    }

    private void SetSelection(int a, int b)
    {
        SelectionStart = Math.Min(a, b);
        SelectionEnd = Math.Max(a, b);
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
