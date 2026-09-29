using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace AdultZone.Desktop;

/// <summary>
/// Nationalities, with flags drawn as pictures: Windows has no flag emoji, so
/// one would show as two plain letters. Flags from flag-icons (MIT).
/// </summary>
public static class Countries
{
    public static readonly (string Code, string Name)[] All =
    {
        ("al", "Albania"),
        ("ar", "Argentina"),
        ("am", "Armenia"),
        ("au", "Australia"),
        ("at", "Austria"),
        ("by", "Belarus"),
        ("be", "Belgium"),
        ("bo", "Bolivia"),
        ("ba", "Bosnia and Herzegovina"),
        ("br", "Brazil"),
        ("bg", "Bulgaria"),
        ("ca", "Canada"),
        ("cl", "Chile"),
        ("cn", "China"),
        ("co", "Colombia"),
        ("cr", "Costa Rica"),
        ("hr", "Croatia"),
        ("cu", "Cuba"),
        ("cz", "Czechia"),
        ("dk", "Denmark"),
        ("do", "Dominican Republic"),
        ("ec", "Ecuador"),
        ("eg", "Egypt"),
        ("ee", "Estonia"),
        ("fi", "Finland"),
        ("fr", "France"),
        ("ge", "Georgia"),
        ("de", "Germany"),
        ("gr", "Greece"),
        ("hu", "Hungary"),
        ("is", "Iceland"),
        ("in", "India"),
        ("id", "Indonesia"),
        ("iq", "Iraq"),
        ("ie", "Ireland"),
        ("il", "Israel"),
        ("it", "Italy"),
        ("jm", "Jamaica"),
        ("jp", "Japan"),
        ("ke", "Kenya"),
        ("lv", "Latvia"),
        ("lt", "Lithuania"),
        ("mx", "Mexico"),
        ("md", "Moldova"),
        ("ma", "Morocco"),
        ("nl", "Netherlands"),
        ("nz", "New Zealand"),
        ("ng", "Nigeria"),
        ("mk", "North Macedonia"),
        ("no", "Norway"),
        ("pk", "Pakistan"),
        ("pa", "Panama"),
        ("py", "Paraguay"),
        ("pe", "Peru"),
        ("ph", "Philippines"),
        ("pl", "Poland"),
        ("pt", "Portugal"),
        ("pr", "Puerto Rico"),
        ("ro", "Romania"),
        ("ru", "Russia"),
        ("rs", "Serbia"),
        ("sk", "Slovakia"),
        ("si", "Slovenia"),
        ("za", "South Africa"),
        ("kr", "South Korea"),
        ("es", "Spain"),
        ("se", "Sweden"),
        ("ch", "Switzerland"),
        ("tw", "Taiwan"),
        ("th", "Thailand"),
        ("tt", "Trinidad and Tobago"),
        ("tr", "Turkey"),
        ("ua", "Ukraine"),
        ("ae", "United Arab Emirates"),
        ("gb", "United Kingdom"),
        ("us", "United States"),
        ("uy", "Uruguay"),
        ("ve", "Venezuela"),
        ("vn", "Vietnam"),
    };

    public static string Name(string code) =>
        All.FirstOrDefault(c => c.Code == code).Name ?? code.ToUpperInvariant();

    public static Image? Flag(string code, double width = 26)
    {
        try
        {
            var bmp = new BitmapImage(new Uri($"pack://application:,,,/flags/{code.ToLowerInvariant()}.png"));
            var image = new Image { Source = bmp, Width = width, Height = Math.Round(width * 0.73), Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            return image;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>♀ ♂ ⚧ — a performer's gender as a symbol, a colour and a small tag.</summary>
public static class Gender
{
    public static string Symbol(string key) => key switch { "female" => "\u2640", "male" => "\u2642", "trans" => "\u26A7", _ => "" };

    public static Brush Colour(string key) => key switch
    {
        "female" => new SolidColorBrush(Color.FromRgb(0xFF, 0x7A, 0xB6)),
        "male" => new SolidColorBrush(Color.FromRgb(0x7F, 0xB4, 0xFF)),
        _ => new SolidColorBrush(Color.FromRgb(0xC9, 0xA7, 0xFF)),
    };

    static string Circle(double cx, double cy, double r) =>
        FormattableString.Invariant($"M{cx - r},{cy} A{r},{r} 0 1 1 {cx + r},{cy} A{r},{r} 0 1 1 {cx - r},{cy} Z");

    /// <summary>
    /// Just the symbol, for beside a name. Drawn rather than typed: the font's
    /// ♀ ♂ ⚧ are small for their size and differ from one another.
    /// </summary>
    public static FrameworkElement? Mark(string key, double size)
    {
        var data = key switch
        {
            "female" => Circle(12, 9, 5.5) + " M12,14.5 L12,22.5 M8.5,19 L15.5,19",
            "male" => Circle(10, 14, 5.5) + " M14,10 L20.5,3.5 M15,3.5 L20.5,3.5 L20.5,9",
            "trans" => Circle(12, 13, 4.6) + " M15.3,9.7 L20.5,4.5 M16.5,4.5 L20.5,4.5 L20.5,8.5" +
                       " M8.7,9.7 L3.5,4.5 M7.5,4.5 L3.5,4.5 L3.5,8.5 M4.6,8.9 L8.9,4.6" +
                       " M12,17.6 L12,23 M9.5,20.6 L14.5,20.6",
            _ => null,
        };
        if (data == null) return null;
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data), Stroke = Colour(key), StrokeThickness = 2.3,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
        };
        var canvas = new Grid { Width = 24, Height = 24, Children = { path } };
        return new Viewbox { Width = size, Height = size, Child = canvas, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The symbol and the word, for lists.</summary>
    public static StackPanel Labelled(string key, string label, Brush? text = null, double size = 16)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (Mark(key, size) is { } mark)
        {
            mark.Margin = new Thickness(0, 0, 7, 0);
            row.Children.Add(mark);
        }
        var word = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
        if (text != null) word.Foreground = text;
        row.Children.Add(word);
        return row;
    }

    /// <summary>The symbol and the word, as a pill.</summary>
    public static Border? Tag(string key)
    {
        var label = AdultZone.Core.Library.Catalog.Genders.FirstOrDefault(g => g.Key == key).Label;
        if (label == null) return null;
        var colour = Colour(key);
        var mark = Mark(key, 16)!;
        mark.Margin = new Thickness(0, 0, 6, 0);
        var word = new TextBlock { Text = label, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = colour, VerticalAlignment = VerticalAlignment.Center };
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mark);
        row.Children.Add(word);
        var c = ((SolidColorBrush)colour).Color;
        return new Pill
        {
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, c.R, c.G, c.B)), BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(0x1C, c.R, c.G, c.B)),
            Padding = new Thickness(10, 3, 11, 4), VerticalAlignment = VerticalAlignment.Center, Child = row,
        };
    }
}
