using System.Windows;
using System.Windows.Media;

namespace PcMaintenance.App.Controls;

/// <summary>A small line chart of recent values, newest on the right, like Task Manager's performance graphs.</summary>
public sealed class Sparkline : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(
        nameof(Values), typeof(IReadOnlyList<double>), typeof(Sparkline), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(Sparkline), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(
        nameof(Capacity), typeof(int), typeof(Sparkline), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
        nameof(Stroke), typeof(Color), typeof(Sparkline), new FrameworkPropertyMetadata(Colors.SteelBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GridBrushProperty = DependencyProperty.Register(
        nameof(GridBrush), typeof(Brush), typeof(Sparkline), new FrameworkPropertyMetadata(Brushes.Gainsboro, FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>Replace the list (rather than changing it) so the chart redraws.</summary>
    public IReadOnlyList<double>? Values
    {
        get => (IReadOnlyList<double>?)GetValue(ValuesProperty);
        set => SetValue(ValuesProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    /// <summary>How many values fit across the width; fewer values start from the right.</summary>
    public int Capacity
    {
        get => (int)GetValue(CapacityProperty);
        set => SetValue(CapacityProperty, value);
    }

    public Color Stroke
    {
        get => (Color)GetValue(StrokeProperty);
        set => SetValue(StrokeProperty, value);
    }

    public Brush GridBrush
    {
        get => (Brush)GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0) return;

        var gridPen = new Pen(GridBrush, 1);
        dc.DrawRectangle(null, gridPen, new Rect(0.5, 0.5, width - 1, height - 1));
        for (var i = 1; i < 4; i++) dc.DrawLine(gridPen, new Point(0, Math.Round(height * i / 4) + 0.5), new Point(width, Math.Round(height * i / 4) + 0.5));

        var values = Values;
        if (values is null || values.Count < 2) return;
        var capacity = Math.Max(Capacity, values.Count);
        var step = width / (capacity - 1);
        var max = Maximum > 0 ? Maximum : 1;
        Point At(int i) => new((capacity - values.Count + i) * step, height - Math.Clamp(values[i] / max, 0, 1) * (height - 2) - 1);

        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (StreamGeometryContext l = line.Open(), a = area.Open())
        {
            l.BeginFigure(At(0), false, false);
            a.BeginFigure(new Point(At(0).X, height), true, true);
            a.LineTo(At(0), false, false);
            for (var i = 1; i < values.Count; i++)
            {
                l.LineTo(At(i), true, true);
                a.LineTo(At(i), false, false);
            }
            a.LineTo(new Point(At(values.Count - 1).X, height), false, false);
        }
        dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(40, Stroke.R, Stroke.G, Stroke.B)), null, area);
        dc.DrawGeometry(null, new Pen(new SolidColorBrush(Stroke), 1.5), line);
    }
}
