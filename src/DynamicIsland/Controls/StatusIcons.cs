using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DynamicIsland.Controls;

/// <summary>
/// Menu bar icons drawn in the iOS / macOS style (our own vector drawings, not Apple's artwork):
/// rounded, even strokes, a filled fan for Wi‑Fi, a pill battery with a nub.
/// </summary>
public static class StatusIcons
{
    private static Brush Dim(Brush fg, double opacity)
    {
        var c = ((SolidColorBrush)fg).Color;
        var b = new SolidColorBrush(Color.FromArgb((byte)(c.A * opacity), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    private static Point OnCircle(Point c, double r, double degrees)
    {
        double a = degrees * Math.PI / 180;
        return new Point(c.X + r * Math.Cos(a), c.Y + r * Math.Sin(a));
    }

    /// <summary>Wi‑Fi fan: a wedge and two arcs; <paramref name="level"/> (0..3) of them lit. Offline adds a slash.</summary>
    public static FrameworkElement Wifi(Brush fg, int level, bool offline)
    {
        var canvas = new Canvas { Width = 18, Height = 14 };
        var center = new Point(9, 13);
        var lit = fg;
        var off = Dim(fg, 0.3);

        // Inner wedge (a small filled pie, pointing down).
        var wedge = new StreamGeometry();
        using (var ctx = wedge.Open())
        {
            ctx.BeginFigure(center, true, true);
            ctx.LineTo(OnCircle(center, 3.6, -135), true, true);
            ctx.ArcTo(OnCircle(center, 3.6, -45), new Size(3.6, 3.6), 0, false, SweepDirection.Clockwise, true, true);
        }
        wedge.Freeze();
        canvas.Children.Add(new Path { Data = wedge, Fill = !offline && level >= 1 ? lit : off, StrokeThickness = 1.2, Stroke = !offline && level >= 1 ? lit : off, StrokeLineJoin = PenLineJoin.Round });

        // Two arcs with round ends.
        foreach (var (radius, needed) in new[] { (7.2, 2), (11.0, 3) })
        {
            var arc = new StreamGeometry();
            using (var ctx = arc.Open())
            {
                ctx.BeginFigure(OnCircle(center, radius, -135), false, false);
                ctx.ArcTo(OnCircle(center, radius, -45), new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }
            arc.Freeze();
            canvas.Children.Add(new Path
            {
                Data = arc,
                Stroke = !offline && level >= needed ? lit : off,
                StrokeThickness = 2.3,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
            });
        }

        if (offline)
            canvas.Children.Add(new Line { X1 = 3, Y1 = 1.5, X2 = 15, Y2 = 13, Stroke = fg, StrokeThickness = 1.6, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round });
        return canvas;
    }

    /// <summary>Wired connection: an Ethernet port.</summary>
    public static FrameworkElement Ethernet(Brush fg) => new Path
    {
        Data = Geometry.Parse("M 2.5,3.5 H 13.5 A 1,1 0 0 1 14.5,4.5 V 11 A 1,1 0 0 1 13.5,12 H 11 V 14 H 5 V 12 H 2.5 A 1,1 0 0 1 1.5,11 V 4.5 A 1,1 0 0 1 2.5,3.5 Z M 5,6.5 V 8.5 M 8,6.5 V 8.5 M 11,6.5 V 8.5"),
        Stroke = fg,
        StrokeThickness = 1.4,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 16,
        Height = 16,
        Stretch = Stretch.None,
    };

    /// <summary>Bluetooth rune; dimmed when Bluetooth is off.</summary>
    public static FrameworkElement Bluetooth(Brush fg, bool on) => new Path
    {
        Data = Geometry.Parse("M 4.5,4.8 L 11.3,10.6 L 7.8,13.6 V 2.4 L 11.3,5.4 L 4.5,11.2"),
        Stroke = on ? fg : Dim(fg, 0.35),
        StrokeThickness = 1.6,
        StrokeLineJoin = PenLineJoin.Round,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 16,
        Height = 16,
        Stretch = Stretch.None,
    };

    public static FrameworkElement Search(Brush fg) => new Path
    {
        Data = Geometry.Parse("M 7,2.3 A 4.7,4.7 0 1 0 7,11.7 A 4.7,4.7 0 1 0 7,2.3 Z M 10.5,10.5 L 14,14"),
        Stroke = fg,
        StrokeThickness = 1.7,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        Width = 16,
        Height = 16,
        Stretch = Stretch.None,
    };

    /// <summary>Two toggle switches stacked (the Control Center look) for Windows' quick settings.</summary>
    public static FrameworkElement QuickSettings(Brush fg)
    {
        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 4,2 H 12 A 2.6,2.6 0 0 1 12,7.2 H 4 A 2.6,2.6 0 0 1 4,2 Z"), Stroke = fg, StrokeThickness = 1.3 });
        canvas.Children.Add(new Ellipse { Width = 3.6, Height = 3.6, Fill = fg, Margin = new Thickness(10.2, 2.8, 0, 0) });
        canvas.Children.Add(new Path { Data = Geometry.Parse("M 4,8.8 H 12 A 2.6,2.6 0 0 1 12,14 H 4 A 2.6,2.6 0 0 1 4,8.8 Z"), Stroke = fg, StrokeThickness = 1.3 });
        canvas.Children.Add(new Ellipse { Width = 3.6, Height = 3.6, Fill = fg, Margin = new Thickness(2.2, 9.6, 0, 0) });
        return canvas;
    }

    /// <summary>An icon for a kind of Bluetooth device (headphones, keyboard…), same stroke style as the rest.</summary>
    public static FrameworkElement Device(Services.BtKind kind, Brush fg)
    {
        string data = kind switch
        {
            Services.BtKind.Headphones => "M 2.5,10 V 8 A 5.5,5.5 0 0 1 13.5,8 V 10 M 2.5,10 H 4.5 V 14 H 3.5 A 1,1 0 0 1 2.5,13 Z M 13.5,10 H 11.5 V 14 H 12.5 A 1,1 0 0 0 13.5,13 Z",
            Services.BtKind.Speaker => "M 4.5,1.5 H 11.5 A 1,1 0 0 1 12.5,2.5 V 13.5 A 1,1 0 0 1 11.5,14.5 H 4.5 A 1,1 0 0 1 3.5,13.5 V 2.5 A 1,1 0 0 1 4.5,1.5 Z M 8,7.3 A 2.7,2.7 0 1 0 8,12.7 A 2.7,2.7 0 1 0 8,7.3 Z M 8,3.6 V 4.4",
            Services.BtKind.Keyboard => "M 1.5,4.5 H 14.5 A 1,1 0 0 1 15.5,5.5 V 11.5 A 1,1 0 0 1 14.5,12.5 H 1.5 A 1,1 0 0 1 0.5,11.5 V 5.5 A 1,1 0 0 1 1.5,4.5 Z M 3.5,7 H 4 M 6,7 H 6.5 M 8.5,7 H 9 M 11,7 H 11.5 M 4.5,10 H 11.5",
            Services.BtKind.Mouse => "M 8,1.5 A 4.5,4.5 0 0 1 12.5,6 V 10 A 4.5,4.5 0 0 1 3.5,10 V 6 A 4.5,4.5 0 0 1 8,1.5 Z M 8,1.5 V 6",
            Services.BtKind.Phone => "M 5.5,1 H 10.5 A 1.5,1.5 0 0 1 12,2.5 V 13.5 A 1.5,1.5 0 0 1 10.5,15 H 5.5 A 1.5,1.5 0 0 1 4,13.5 V 2.5 A 1.5,1.5 0 0 1 5.5,1 Z M 7,13 H 9",
            Services.BtKind.Computer => "M 2.5,3 H 13.5 A 1,1 0 0 1 14.5,4 V 10.5 H 1.5 V 4 A 1,1 0 0 1 2.5,3 Z M 0.5,10.5 H 15.5 V 11.5 A 1,1 0 0 1 14.5,12.5 H 1.5 A 1,1 0 0 1 0.5,11.5 Z",
            Services.BtKind.Gamepad => "M 4.5,4.5 H 11.5 A 4,4 0 0 1 15,9.5 L 14.4,12 A 1.6,1.6 0 0 1 11.6,12.6 L 10.5,11 H 5.5 L 4.4,12.6 A 1.6,1.6 0 0 1 1.6,12 L 1,9.5 A 4,4 0 0 1 4.5,4.5 Z M 4.5,7 V 9 M 3.5,8 H 5.5 M 11,7.5 H 11.2 M 12,8.7 H 12.2",
            _ => "M 4.5,4.8 L 11.3,10.6 L 7.8,13.6 V 2.4 L 11.3,5.4 L 4.5,11.2",
        };
        return new Path
        {
            Data = Geometry.Parse(data),
            Stroke = fg,
            StrokeThickness = 1.35,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = 16,
            Height = 16,
            Stretch = Stretch.None,
        };
    }

    private static readonly Brush ChargingGreen = Frozen(Color.FromRgb(0x34, 0xC7, 0x59));
    private static readonly Brush LowRed = Frozen(Color.FromRgb(0xFF, 0x3B, 0x30));

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    /// <summary>The pill battery: fill follows the exact level; green with a bolt while charging, red when low.</summary>
    public static FrameworkElement Battery(Brush fg, int percent, bool charging, bool plugged)
    {
        bool low = !plugged && percent <= 20;
        var fillBrush = charging ? ChargingGreen : low ? LowRed : fg;
        const double innerWidth = 20;
        var body = new Border
        {
            Width = 25,
            Height = 12.5,
            CornerRadius = new CornerRadius(3.8),
            BorderBrush = Dim(fg, 0.42),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(1.5),
            Child = new Border
            {
                CornerRadius = new CornerRadius(2.2),
                Background = fillBrush,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(2, innerWidth * Math.Clamp(percent, 0, 100) / 100.0),
            },
        };
        var nub = new Border
        {
            Width = 1.8,
            Height = 4.5,
            CornerRadius = new CornerRadius(0, 1, 1, 0),
            Background = Dim(fg, 0.42),
            Margin = new Thickness(1, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var grid = new Grid();
        grid.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { body, nub } });
        if (charging || (plugged && percent >= 100))
        {
            grid.Children.Add(new Path
            {
                Data = Geometry.Parse("M 6.2,0 L 0.5,7 H 4.4 L 3.3,12 L 9,5 H 5.1 Z"),
                Fill = Brushes.White,
                Stroke = Frozen(Color.FromArgb(0x80, 0, 0, 0)),
                StrokeThickness = 0.6,
                Width = 9,
                Height = 12,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            });
        }
        return grid;
    }
}
