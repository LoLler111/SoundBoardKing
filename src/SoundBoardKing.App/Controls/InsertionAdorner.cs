using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;

namespace SoundBoardKing.App.Controls;

/// <summary>The line that shows where the dragged sounds will land in the list.</summary>
internal sealed class InsertionAdorner : Adorner
{
    private readonly Pen _pen;
    private double _y = double.NaN;

    public InsertionAdorner(UIElement adorned, Brush brush) : base(adorned)
    {
        IsHitTestVisible = false;
        _pen = new Pen(brush, 2);
        _pen.Freeze();
    }

    /// <summary>Height of the line in the adorned element's coordinates; NaN hides it.</summary>
    public double Y
    {
        get => _y;
        set
        {
            if (value.Equals(_y))
                return;
            _y = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        if (double.IsNaN(_y))
            return;
        double width = AdornedElement.RenderSize.Width;
        drawingContext.DrawLine(_pen, new Point(4, _y), new Point(width - 4, _y));
        drawingContext.DrawEllipse(_pen.Brush, null, new Point(4, _y), 3, 3);
    }
}
