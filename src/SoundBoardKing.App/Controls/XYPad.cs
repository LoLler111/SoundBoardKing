using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace SoundBoardKing.App.Controls;

/// <summary>
/// Two-axis pad: press and drag to move the point. <see cref="X"/> goes 0..100 left to right,
/// <see cref="Y"/> goes -100..100 bottom to top (0 in the middle). The point stays where it is let go.
/// </summary>
public sealed class XYPad : FrameworkElement
{
    public static readonly DependencyProperty XProperty = DependencyProperty.Register(
        nameof(X), typeof(double), typeof(XYPad),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty YProperty = DependencyProperty.Register(
        nameof(Y), typeof(double), typeof(XYPad),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender | FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(XYPad),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty LineProperty = DependencyProperty.Register(
        nameof(Line), typeof(Brush), typeof(XYPad),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(XYPad),
        new FrameworkPropertyMetadata(Brushes.Orange, FrameworkPropertyMetadataOptions.AffectsRender));

    public XYPad()
    {
        Cursor = Cursors.Cross;
        Focusable = true;
    }

    public double X
    {
        get => (double)GetValue(XProperty);
        set => SetValue(XProperty, value);
    }

    public double Y
    {
        get => (double)GetValue(YProperty);
        set => SetValue(YProperty, value);
    }

    public Brush Background
    {
        get => (Brush)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public Brush Line
    {
        get => (Brush)GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var area = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRoundedRectangle(Background, null, area, 8, 8);

        var grid = new Pen(Line, 1) { DashStyle = DashStyles.Dot };
        for (int i = 1; i < 4; i++)
        {
            double gx = area.Width * i / 4, gy = area.Height * i / 4;
            dc.DrawLine(i == 2 ? new Pen(Line, 1) : grid, new Point(gx, 4), new Point(gx, area.Height - 4));
            dc.DrawLine(i == 2 ? new Pen(Line, 1) : grid, new Point(4, gy), new Point(area.Width - 4, gy));
        }

        var point = ToPoint();
        var cross = new Pen(Fill, 1) { DashStyle = DashStyles.Dash };
        dc.PushOpacity(0.5);
        dc.DrawLine(cross, new Point(point.X, 4), new Point(point.X, area.Height - 4));
        dc.DrawLine(cross, new Point(4, point.Y), new Point(area.Width - 4, point.Y));
        dc.Pop();
        dc.DrawEllipse(Fill, new Pen(Brushes.White, 1.5), point, 7, 7);
    }

    private Point ToPoint() => new(
        Math.Clamp(X, 0, 100) / 100 * ActualWidth,
        (1 - (Math.Clamp(Y, -100, 100) + 100) / 200) * ActualHeight);

    private void MoveTo(Point p)
    {
        X = Math.Round(Math.Clamp(p.X / ActualWidth, 0, 1) * 100);
        Y = Math.Round((1 - Math.Clamp(p.Y / ActualHeight, 0, 1)) * 200 - 100);
    }

    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        CaptureMouse();
        MoveTo(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (IsMouseCaptured)
            MoveTo(e.GetPosition(this));
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (IsMouseCaptured)
            ReleaseMouseCapture();
    }
}
