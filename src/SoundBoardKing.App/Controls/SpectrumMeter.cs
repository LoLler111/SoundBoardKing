using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using SoundBoardKing.Audio;

namespace SoundBoardKing.App.Controls;

/// <summary>
/// Spectrum bars drawn behind a volume slider. Polls <see cref="Reader"/> about 30 times a second
/// while visible; bars jump up at once and fall back gently.
/// </summary>
public sealed class SpectrumMeter : FrameworkElement
{
    public static readonly DependencyProperty ReaderProperty = DependencyProperty.Register(
        nameof(Reader), typeof(Action<float[]>), typeof(SpectrumMeter));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SpectrumMeter),
        new FrameworkPropertyMetadata(Brushes.Goldenrod, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double Gap = 2;
    private const float Fall = 0.86f;

    private readonly float[] _raw = new float[SpectrumTap.BandCount];
    private readonly float[] _shown = new float[SpectrumTap.BandCount];
    private readonly DispatcherTimer _timer;

    public SpectrumMeter()
    {
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => Update();
        Loaded += (_, _) => _timer.Start();
        Unloaded += (_, _) => _timer.Stop();
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
    }

    /// <summary>Fills the array with the current levels, 0..1 per band.</summary>
    public Action<float[]>? Reader
    {
        get => (Action<float[]>?)GetValue(ReaderProperty);
        set => SetValue(ReaderProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    private void Update()
    {
        if (Reader is not { } reader || !IsVisible)
            return;
        reader(_raw);

        bool changed = false;
        for (int i = 0; i < _shown.Length; i++)
        {
            float next = _raw[i] >= _shown[i] ? _raw[i] : Math.Max(_raw[i], _shown[i] * Fall);
            if (next < 0.01f)
                next = 0;
            changed |= Math.Abs(next - _shown[i]) > 0.002f;
            _shown[i] = next;
        }
        if (changed)
            InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        double width = ActualWidth, height = ActualHeight;
        int count = _shown.Length;
        double barWidth = (width - Gap * (count - 1)) / count;
        if (barWidth <= 0 || height <= 0)
            return;

        for (int i = 0; i < count; i++)
        {
            double barHeight = Math.Round(_shown[i] * height);
            if (barHeight < 1)
                continue;
            var rect = new Rect(i * (barWidth + Gap), height - barHeight, barWidth, barHeight);
            context.DrawRoundedRectangle(Fill, null, rect, 1.5, 1.5);
        }
    }
}
