using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace AdultZone.Desktop;

/// <summary>
/// Line icons for the player controls. Every one is drawn on the same 20×20
/// square with its weight in the middle, so any row of them lines up.
/// </summary>
public static class Glyphs
{
    const double Size = 20;

    static Path Line(string data, Brush? stroke = null, double thickness = 1.8) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = stroke ?? Theme.Text,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    static Path Solid(string data, Brush? fill = null) => new()
    {
        Data = Geometry.Parse(data),
        Fill = fill ?? Theme.Text,
    };

    static Grid Box(params UIElement[] parts) => Box(Size, parts);

    static Grid Box(double size, params UIElement[] parts)
    {
        var g = new Grid
        {
            Width = size, Height = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
        foreach (var p in parts) g.Children.Add(p);
        return g;
    }

    public static FrameworkElement Speaker(bool silent) => Box(
        Solid("M2.5,7.5 L6,7.5 L10,4 L10,16 L6,12.5 L2.5,12.5 Z"),
        Line(silent ? "M13,7.5 L17.5,12 M17.5,7.5 L13,12" : "M12.8,7.6 A3,3 0 0 1 12.8,12.4 M15,5.3 A6,6 0 0 1 15,14.7",
             silent ? Theme.EmberHi : Theme.Text, 1.6));

    /// <summary>The CC box; an ember bar under it while subtitles are showing.</summary>
    public static FrameworkElement Captions(bool on)
    {
        var box = new Border
        {
            Width = 17, Height = 12, CornerRadius = new CornerRadius(2),
            BorderBrush = on ? Theme.EmberHi : Theme.Text, BorderThickness = new Thickness(1.5),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = "CC", FontSize = 7, FontWeight = FontWeights.Bold, Foreground = on ? Theme.EmberHi : Theme.Text,
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, -1, 0, 0),
            },
        };
        var bar = new Border
        {
            Height = 2, Width = 13, CornerRadius = new CornerRadius(1), Background = Theme.EmberHi,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Visibility = on ? Visibility.Visible : Visibility.Hidden,
        };
        return Box(box, bar);
    }

    public static FrameworkElement PopOut() => Box(
        Line("M17,8.5 L17,4 L3,4 L3,16 L8.5,16", thickness: 1.6),
        Solid("M10.5,10.5 L17.5,10.5 L17.5,16 L10.5,16 Z"));

    public static FrameworkElement FullScreen() => Box(
        Line("M3,7.5 L3,3 L7.5,3 M12.5,3 L17,3 L17,7.5 M17,12.5 L17,17 L12.5,17 M7.5,17 L3,17 L3,12.5", thickness: 1.7));

    public static FrameworkElement Previous() => Box(Line("M5,4.5 L5,15.5", thickness: 2), Solid("M15.5,4.5 L7.5,10 L15.5,15.5 Z"));

    public static FrameworkElement Next() => Box(Line("M15,4.5 L15,15.5", thickness: 2), Solid("M4.5,4.5 L12.5,10 L4.5,15.5 Z"));

    public static FrameworkElement Minimise() => Box(Line("M4.5,10 L15.5,10"));

    public static FrameworkElement BackToApp() => Box(Line("M11.5,3.5 L16.5,3.5 L16.5,8.5 M16.5,3.5 L11,9 M8.5,16.5 L3.5,16.5 L3.5,11.5 M3.5,16.5 L9,11"));

    public static FrameworkElement Close() => Box(Line("M5,5 L15,15 M15,5 L5,15"));

    /// <summary>A chevron centred on its box, for leaving the player.</summary>
    public static FrameworkElement Back() => Box(Line("M12.5,4 L6.5,10 L12.5,16", thickness: 2));

    public static FrameworkElement Play(Brush? colour = null) => Box(Solid("M6,3.5 L16.5,10 L6,16.5 Z", colour));

    public static FrameworkElement Pause(Brush? colour = null) => Box(Solid("M5,4 L8.5,4 L8.5,16 L5,16 Z M11.5,4 L15,4 L15,16 L11.5,16 Z", colour));

    /// <summary>The playback settings cog.</summary>
    public static FrameworkElement Gear() => Box(
        Line("M10,7.4 A2.6,2.6 0 1 1 9.99,7.4 Z M10,2.5 L10,4.5 M10,15.5 L10,17.5 M2.5,10 L4.5,10 M15.5,10 L17.5,10 " +
             "M4.7,4.7 L6.1,6.1 M13.9,13.9 L15.3,15.3 M15.3,4.7 L13.9,6.1 M6.1,13.9 L4.7,15.3", thickness: 1.7));

    /// <summary>
    /// A circular arrow with the skip size inside. Drawn round a circle centred
    /// in the square: forward runs clockwise to point right at the top, back is
    /// its mirror image.
    /// </summary>
    public static FrameworkElement Skip(bool forward, int seconds, double size = 24)
    {
        var arc = forward
            ? "M18.93,8 A8,8 0 1 1 12,4 M12,4 L9.4,1.6 M12,4 L9.4,6.4"
            : "M5.07,8 A8,8 0 1 0 12,4 M12,4 L14.6,1.6 M12,4 L14.6,6.4";
        var number = new TextBlock
        {
            Text = seconds.ToString(CultureInfo.InvariantCulture), FontSize = 7.5, FontWeight = FontWeights.Bold, Foreground = Theme.Text,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 1, 0, 0),
        };
        var drawing = Box(24, Line(arc, thickness: 1.7), number);
        if (Math.Abs(size - 24) > 0.1) drawing.LayoutTransform = new ScaleTransform(size / 24, size / 24);
        return drawing;
    }

    /// <summary>A turning ember arc while a file opens.</summary>
    public static FrameworkElement Spinner()
    {
        var ring = new Ellipse { Width = 40, Height = 40, Stroke = Theme.Alpha(Colors.White, 0x33), StrokeThickness = 3 };
        var arc = new Path
        {
            Data = Geometry.Parse("M20,1.5 A18.5,18.5 0 0 1 38.5,20"),
            Stroke = Theme.Ember, StrokeThickness = 3, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Width = 40, Height = 40,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var turn = new RotateTransform();
        arc.RenderTransform = turn;
        turn.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(0.9)) { RepeatBehavior = RepeatBehavior.Forever });
        var g = new Grid { Width = 40, Height = 40, IsHitTestVisible = false };
        g.Children.Add(ring);
        g.Children.Add(arc);
        return g;
    }
}
