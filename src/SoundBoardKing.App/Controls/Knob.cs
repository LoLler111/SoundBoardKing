using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundBoardKing.App.Controls;

/// <summary>
/// Round knob over <see cref="RangeBase.Value"/>: drag up or down (or use the mouse wheel / arrow keys)
/// to turn it; double click goes back to <see cref="DefaultValue"/>. The arc covers 270 degrees.
/// </summary>
public sealed class Knob : RangeBase
{
    private const double StartAngle = 135;   // degrees, clockwise from the x axis (WPF y points down)
    private const double Sweep = 270;
    private const double DragPixels = 150;   // a drag this long goes from Minimum to Maximum

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(Knob),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty DefaultValueProperty = DependencyProperty.Register(
        nameof(DefaultValue), typeof(double), typeof(Knob), new PropertyMetadata(0.0));

    private Point _dragStart;
    private double _dragValue;

    static Knob()
    {
        ValueProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
        FocusableProperty.OverrideMetadata(typeof(Knob), new FrameworkPropertyMetadata(true));
    }

    public Knob()
    {
        SmallChange = 1;
        LargeChange = 10;
        Cursor = Cursors.SizeNS;
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public double DefaultValue
    {
        get => (double)GetValue(DefaultValueProperty);
        set => SetValue(DefaultValueProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 4)
            return;
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        double thickness = Math.Max(2.5, size * 0.12);
        double radius = size / 2 - thickness / 2;
        double range = Maximum - Minimum;
        double fraction = range > 0 ? (Value - Minimum) / range : 0;
        var opacity = IsEnabled ? 1.0 : 0.4;

        dc.PushOpacity(opacity);
        dc.DrawGeometry(null, new Pen(Track, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            Arc(center, radius, StartAngle, Sweep));
        if (fraction > 0.001)
            dc.DrawGeometry(null, new Pen(Fill, thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
                Arc(center, radius, StartAngle, Sweep * fraction));

        // Pointer from the middle towards the current value.
        double angle = (StartAngle + Sweep * fraction) * Math.PI / 180;
        var tip = new Point(center.X + Math.Cos(angle) * radius * 0.55, center.Y + Math.Sin(angle) * radius * 0.55);
        dc.DrawLine(new Pen(Foreground ?? Brushes.White, Math.Max(1.5, size * 0.07)) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round },
            center, tip);
        dc.Pop();
    }

    private static Geometry Arc(Point center, double radius, double startDegrees, double sweepDegrees)
    {
        static Point On(Point c, double r, double degrees)
        {
            double a = degrees * Math.PI / 180;
            return new Point(c.X + Math.Cos(a) * r, c.Y + Math.Sin(a) * r);
        }

        var figure = new PathFigure { StartPoint = On(center, radius, startDegrees), IsClosed = false };
        figure.Segments.Add(new ArcSegment(On(center, radius, startDegrees + sweepDegrees), new Size(radius, radius), 0,
            sweepDegrees > 180, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }

    protected override System.Windows.Automation.Peers.AutomationPeer OnCreateAutomationPeer() => new KnobAutomationPeer(this);

    // Transparent background so the whole square reacts to the mouse, not only the drawn arc.
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        if (e.ClickCount == 2)
        {
            Value = DefaultValue;
            e.Handled = true;
            return;
        }
        Focus();
        _dragStart = e.GetPosition(this);
        _dragValue = Value;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured)
            return;
        double delta = (_dragStart.Y - e.GetPosition(this).Y) / DragPixels * (Maximum - Minimum);
        Value = Math.Clamp(Math.Round(_dragValue + delta), Minimum, Maximum);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        Value = Math.Clamp(Value + Math.Sign(e.Delta) * LargeChange / 2, Minimum, Maximum);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        double step = e.Key switch
        {
            Key.Up or Key.Right => SmallChange,
            Key.Down or Key.Left => -SmallChange,
            Key.PageUp => LargeChange,
            Key.PageDown => -LargeChange,
            _ => 0,
        };
        if (step != 0)
        {
            Value = Math.Clamp(Value + step, Minimum, Maximum);
            e.Handled = true;
        }
    }
}

/// <summary>Shows the knob to screen readers (and UI tests) as a slider with a value.</summary>
internal sealed class KnobAutomationPeer(Knob owner) : System.Windows.Automation.Peers.RangeBaseAutomationPeer(owner)
{
    protected override string GetClassNameCore() => nameof(Knob);

    protected override System.Windows.Automation.Peers.AutomationControlType GetAutomationControlTypeCore() =>
        System.Windows.Automation.Peers.AutomationControlType.Slider;
}
