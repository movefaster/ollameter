using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace Ollameter;

/// <summary>A stack with a gap between visible children, like SwiftUI's stacks.</summary>
internal class Stack : Panel
{
    public Orientation Orientation { get; init; } = Orientation.Vertical;
    public double Spacing { get; init; }

    protected override Size MeasureOverride(Size available)
    {
        double main = 0, cross = 0;
        var visible = 0;
        var vertical = Orientation == Orientation.Vertical;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            child.Measure(vertical ? new Size(available.Width, double.PositiveInfinity) : new Size(double.PositiveInfinity, available.Height));
            var size = child.DesiredSize;
            main += vertical ? size.Height : size.Width;
            cross = Math.Max(cross, vertical ? size.Width : size.Height);
            visible++;
        }
        main += Math.Max(visible - 1, 0) * Spacing;
        return vertical ? new Size(cross, main) : new Size(main, cross);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double position = 0;
        var vertical = Orientation == Orientation.Vertical;
        var first = true;
        foreach (UIElement child in InternalChildren)
        {
            if (child.Visibility == Visibility.Collapsed) continue;
            if (!first) position += Spacing;
            first = false;
            var size = child.DesiredSize;
            if (vertical)
            {
                child.Arrange(new Rect(0, position, final.Width, size.Height));
                position += size.Height;
            }
            else
            {
                child.Arrange(new Rect(position, 0, size.Width, final.Height));
                position += size.Width;
            }
        }
        return final;
    }

    public Stack With(params UIElement[] children)
    {
        foreach (var child in children) Children.Add(child);
        return this;
    }
}

/// <summary>A line chart with a soft fill. With a slot count, values fill in from the right so the time scale stays fixed.</summary>
internal sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> values = [];
    private readonly Color color;
    private readonly Brush hairline;
    private readonly int? slots;
    private readonly double? maximum;

    public Sparkline(Color color, Brush hairline, int? slots = null, double? maximum = null)
    {
        this.color = color;
        this.hairline = hairline;
        this.slots = slots;
        this.maximum = maximum;
        SnapsToDevicePixels = true;
    }

    public IReadOnlyList<double> Values
    {
        get => values;
        set
        {
            if (values.SequenceEqual(value)) return;
            values = [.. value];
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var baseline = height - 1;
        context.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, width, height));
        context.DrawRectangle(hairline, null, new Rect(0, height - 1, width, 1));
        if (values.Count < 2 || width <= 0) return;
        var top = maximum ?? Math.Max(values.Max() * 1.1, 0.001);
        var count = Math.Max(slots ?? values.Count, values.Count);
        var step = width / (count - 1);
        var firstX = width - step * (values.Count - 1);
        var points = values.Select((value, index) =>
            new Point(firstX + step * index, baseline - Math.Min(value / top, 1) * (baseline - 2))).ToList();

        var line = new StreamGeometry();
        using (var stream = line.Open())
        {
            stream.BeginFigure(points[0], false, false);
            stream.PolyLineTo(points.Skip(1).ToList(), true, true);
        }
        line.Freeze();
        var area = new StreamGeometry();
        using (var stream = area.Open())
        {
            stream.BeginFigure(points[0], true, true);
            stream.PolyLineTo(points.Skip(1).Append(new Point(width, baseline)).Append(new Point(firstX, baseline)).ToList(), false, false);
        }
        area.Freeze();
        context.DrawGeometry(new SolidColorBrush(Color.FromArgb(26, color.R, color.G, color.B)), null, area);
        var pen = new Pen(new SolidColorBrush(color), 2) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        context.DrawGeometry(null, pen, line);
    }
}

/// <summary>A rounded bar whose unfilled track is a lighter step of its own color.</summary>
internal sealed class MeterBar : FrameworkElement
{
    private double fraction;
    private Color color;

    public MeterBar(Color color, double height = 8)
    {
        this.color = color;
        Height = height;
        SnapsToDevicePixels = true;
    }

    public void Set(double newFraction, Color newColor)
    {
        if (newFraction == fraction && newColor == color) return;
        fraction = newFraction;
        color = newColor;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext context)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var radius = height / 2;
        context.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(46, color.R, color.G, color.B)), null, new Rect(0, 0, width, height), radius, radius);
        if (fraction <= 0) return;
        var filled = Math.Max(height, width * Math.Min(fraction, 1));
        context.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(0, 0, filled, height), radius, radius);
    }
}

/// <summary>Two capsules split by a share, with a small gap between them.</summary>
internal sealed class SegmentedBar : FrameworkElement
{
    private const double Gap = 2;
    private double share;
    private readonly Color leading;
    private readonly Color trailing;

    public SegmentedBar(Color leading, Color trailing)
    {
        this.leading = leading;
        this.trailing = trailing;
        Height = 8;
    }

    public double Share
    {
        get => share;
        set
        {
            if (value == share) return;
            share = value;
            InvalidateVisual();
        }
    }

    protected override void OnRender(DrawingContext context)
    {
        var height = ActualHeight;
        var usable = Math.Max(ActualWidth - Gap, 0);
        var first = Math.Min(Math.Max(usable * share, height), usable);
        var radius = height / 2;
        context.DrawRoundedRectangle(new SolidColorBrush(leading), null, new Rect(0, 0, first, height), radius, radius);
        var rest = usable - first;
        if (rest > 0)
            context.DrawRoundedRectangle(new SolidColorBrush(trailing), null, new Rect(first + Gap, 0, rest, height), radius, radius);
    }
}

/// <summary>Small vector icons standing in for the SF Symbols the Mac app uses.</summary>
internal static class Glyphs
{
    public static Path ChevronDown(Brush brush) => Stroke("M 0,0 L 4.5,4.5 L 9,0", brush, 9, 5);
    public static Path ChevronUp(Brush brush) => Stroke("M 0,4.5 L 4.5,0 L 9,4.5", brush, 9, 5);
    public static Path Close(Brush brush) => Stroke("M 0,0 L 8,8 M 8,0 L 0,8", brush, 8, 8);

    public static Path Eject(Brush brush) => Fill("M 5,0 L 10,5.5 L 0,5.5 Z M 0,7.5 L 10,7.5 L 10,9 L 0,9 Z", brush, 10, 9);

    public static Path Computer(Brush brush)
    {
        var path = Stroke("M 0.75,0.75 L 12.25,0.75 L 12.25,8.25 L 0.75,8.25 Z M 4,11.25 L 9,11.25 M 6.5,8.25 L 6.5,11.25", brush, 13, 12);
        path.StrokeThickness = 1.3;
        return path;
    }

    /// <summary>A filled triangle with an exclamation mark cut out of it.</summary>
    public static Path Warning(Brush brush, double size = 11)
    {
        var geometry = Geometry.Parse(
            "F0 M 6,0.6 C 6.4,0.6 6.7,0.8 6.9,1.2 L 11.8,10.1 C 12.2,10.8 11.7,11.6 10.9,11.6 L 1.1,11.6 C 0.3,11.6 -0.2,10.8 0.2,10.1 L 5.1,1.2 C 5.3,0.8 5.6,0.6 6,0.6 Z "
            + "M 5.3,4 L 6.7,4 L 6.5,7.8 L 5.5,7.8 Z M 6,8.6 C 6.5,8.6 6.8,9 6.8,9.4 C 6.8,9.9 6.5,10.2 6,10.2 C 5.5,10.2 5.2,9.9 5.2,9.4 C 5.2,9 5.5,8.6 6,8.6 Z");
        return new Path { Data = geometry, Fill = brush, Width = size, Height = size, Stretch = Stretch.Uniform, VerticalAlignment = VerticalAlignment.Center };
    }

    private static Path Stroke(string data, Brush brush, double width, double height) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = 1.5,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
        Width = width,
        Height = height,
        Stretch = Stretch.None,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private static Path Fill(string data, Brush brush, double width, double height) => new()
    {
        Data = Geometry.Parse(data),
        Fill = brush,
        Width = width,
        Height = height,
        Stretch = Stretch.None,
        VerticalAlignment = VerticalAlignment.Center,
    };
}

/// <summary>A flat button drawn entirely by the app, so it matches the window in both themes.</summary>
internal sealed class FlatButton : Border
{
    private readonly Brush rest;
    private readonly Brush hover;
    private bool pressed;
    private bool enabled = true;

    public event Action? Click;

    public FlatButton(UIElement content, Brush rest, Brush hover, CornerRadius corners)
    {
        this.rest = rest;
        this.hover = hover;
        Child = content;
        Background = rest;
        CornerRadius = corners;
        Cursor = Cursors.Arrow;
        Focusable = false;
    }

    public bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            if (!value) Background = rest;
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e)
    {
        if (enabled) Background = hover;
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        Background = rest;
        pressed = false;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        // Handled here, so a press on a button doesn't start dragging the window.
        e.Handled = true;
        if (!enabled) return;
        pressed = true;
        CaptureMouse();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        var wasPressed = pressed;
        pressed = false;
        ReleaseMouseCapture();
        if (wasPressed && enabled && IsMouseOver) Click?.Invoke();
    }
}

internal static class Ui
{
    public static TextBlock Text(
        double size, Brush brush, FontWeight? weight = null, bool tabular = false, bool display = false, string text = "")
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = size,
            FontFamily = display ? Theme.DisplayFont : Theme.Font,
            FontWeight = weight ?? FontWeights.Normal,
            Foreground = brush,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextWrapping = TextWrapping.NoWrap,
        };
        if (tabular) Typography.SetNumeralAlignment(block, FontNumeralAlignment.Tabular);
        return block;
    }

    public static void Set(this TextBlock block, string text)
    {
        if (block.Text != text) block.Text = text;
    }

    public static void Show(this UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

    public static Rectangle Hairline(Theme theme) => new() { Height = 1, Fill = theme.Hairline, SnapsToDevicePixels = true };

    public static Ellipse Dot(Brush brush, double size = 8) =>
        new() { Width = size, Height = size, Fill = brush, VerticalAlignment = VerticalAlignment.Center };

    public static Grid Columns(params (GridLength Width, UIElement Child)[] cells)
    {
        var grid = new Grid();
        for (var index = 0; index < cells.Length; index++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = cells[index].Width });
            Grid.SetColumn(cells[index].Child, index);
            grid.Children.Add(cells[index].Child);
        }
        return grid;
    }

    public static GridLength Star => new(1, GridUnitType.Star);
    public static GridLength Auto => GridLength.Auto;
    public static GridLength Fixed(double width) => new(width);

    public static string Number(double value, string format) => value.ToString(format, CultureInfo.InvariantCulture);
}
