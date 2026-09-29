using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AdultZone.Core.Data;
using AdultZone.Core.Library;

namespace AdultZone.Desktop;

/// <summary>Video, performer and studio cards, and the grids they sit in — 1.x's card designs.</summary>
public static class Cards
{
    public const double VideoWidth = 322;
    public const double StarWidth = 152;
    public const double StudioWidth = 210;

    static readonly Duration Quick = new(TimeSpan.FromMilliseconds(220));

    /// <summary>Keeps an element at a fixed shape whatever width it is given.</summary>
    public static void Aspect(FrameworkElement element, double heightPerWidth) =>
        element.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 0.5)
                element.Height = Math.Round(e.NewSize.Width * heightPerWidth);
        };

    static FrameworkElement Placeholder(string text)
    {
        var stripes = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0), EndPoint = new Point(12, 5.6), MappingMode = BrushMappingMode.Absolute,
            SpreadMethod = GradientSpreadMethod.Repeat,
            GradientStops =
            {
                new GradientStop(Color.FromRgb(0x14, 0x14, 0x1A), 0), new GradientStop(Color.FromRgb(0x14, 0x14, 0x1A), 0.5),
                new GradientStop(Color.FromRgb(0x17, 0x17, 0x1E), 0.5), new GradientStop(Color.FromRgb(0x17, 0x17, 0x1E), 1),
            },
        };
        var label = Ui.Caps(text, 10.5, Theme.Faint, 0.12);
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.VerticalAlignment = VerticalAlignment.Center;
        return new Grid { Background = stripes, Children = { label } };
    }

    /// <summary>
    /// .card — the 16:9 shot with its looping preview on hover, a heart for a
    /// favourite, the length in the corner; the title under it and one line of
    /// facts: date · quality · studio / sub-site · two of the cast.
    /// </summary>
    public static Button Video(MainWindow win, Row v, double? width = VideoWidth)
    {
        var id = v.Long("id") ?? 0;
        var shot = new Grid();
        // Rounded like the frame, so the preview playing inside it never shows past its corners.
        shot.SizeChanged += (_, e) => shot.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        var face = new Border { CornerRadius = new CornerRadius(7), Background = Theme.Panel };
        shot.Children.Add(face);
        var thumb = Catalog.ThumbPath(v);
        if (thumb.Length > 0 && File.Exists(thumb)) Ui.Cover(face, thumb, decode: 640, fade: true);
        else shot.Children.Add(Placeholder("No thumbnail"));

        var preview = Catalog.PreviewPath(v);
        var videoLayer = new Border { CornerRadius = new CornerRadius(7), ClipToBounds = true };
        shot.Children.Add(videoLayer);

        if (v.Truthy("favorite"))
            shot.Children.Add(new Border
            {
                Margin = new Thickness(7), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = Icons.Heart(16, Theme.Ember),
            });
        if (Ui.Duration(v.Double("duration")) is { Length: > 0 } length)
            shot.Children.Add(new Border
            {
                Background = Theme.Alpha(Color.FromRgb(4, 4, 6), 0xDB), CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = length, FontFamily = Theme.Mono, FontSize = 11, Foreground = new SolidColorBrush(Color.FromRgb(0xD9, 0xD7, 0xE0)) },
            });

        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1),
            Background = Theme.Panel, Child = shot,
        };
        Aspect(frame, 9.0 / 16);
        // Its size is known up front on a shelf, so it never flashes flat before the first layout.
        if (width is double knownWidth) frame.Height = Math.Round(knownWidth * 9.0 / 16);

        var title = Ui.Clamp(v.Str("title"), 14.5, Theme.Text, 2, 19, FontWeights.SemiBold);
        title.Margin = new Thickness(2, 9, 2, 0);
        var body = new StackPanel();
        body.Children.Add(title);
        body.Children.Add(MetaLine(win, v).Margin(2, 6, 2, 0));

        var stack = new StackPanel();
        stack.Children.Add(frame);
        stack.Children.Add(body);
        var lift = new TranslateTransform();
        stack.RenderTransform = lift;

        var card = Ui.Bare(stack, () => win.Navigate(new Location("video", id)));
        if (width != null) card.Width = width.Value;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, Quick) { EasingFunction = new CubicEase() });
            frame.BorderBrush = Theme.Alpha(Colors.White, 0x38);
            if (preview.Length > 0) HoverPreview.Start(videoLayer, preview);
        };
        card.MouseLeave += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Quick) { EasingFunction = new CubicEase() });
            frame.BorderBrush = Theme.LineSoft;
            HoverPreview.Stop(videoLayer);
        };
        return card;
    }

    public const double MovieWidth = 200;

    /// <summary>A movie: its box cover at 2:3 (a frame from the film until it has one), the title, year and length.</summary>
    public static Button Movie(MainWindow win, Row v, double? width = MovieWidth)
    {
        var id = v.Long("id") ?? 0;
        var shot = new Grid();
        shot.SizeChanged += (_, e) => shot.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        var face = new Border { CornerRadius = new CornerRadius(7), Background = Theme.Panel };
        shot.Children.Add(face);
        var cover = Catalog.CoverPath(v);
        var thumb = Catalog.ThumbPath(v);
        if (cover.Length > 0 && File.Exists(cover)) Ui.Cover(face, cover, 0.5, 0.5, decode: 460, fade: true);
        else if (thumb.Length > 0 && File.Exists(thumb)) Ui.Cover(face, thumb, 0.5, 0.5, decode: 640, fade: true);
        else shot.Children.Add(Placeholder("No cover"));
        if (Catalog.Quality(v) is { Length: > 0 } q)
            shot.Children.Add(new Border
            {
                Background = Theme.Alpha(Color.FromRgb(4, 4, 6), 0xC8), CornerRadius = new CornerRadius(4), Padding = new Thickness(3, 1, 3, 1),
                Margin = new Thickness(7), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
                Child = Ui.Quality(q),
            });
        if (v.Truthy("favorite"))
            shot.Children.Add(new Border
            {
                Margin = new Thickness(7), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
                Child = Icons.Heart(16, Theme.Ember),
            });
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1),
            Background = Theme.Panel, Child = shot,
        };
        Aspect(frame, 1.5);
        if (width is double knownWidth) frame.Height = Math.Round(knownWidth * 1.5);

        var title = Ui.Clamp(v.Str("title"), 14, Theme.Text, 2, 18.5, FontWeights.SemiBold);
        title.Margin = new Thickness(2, 9, 2, 0);
        var facts = string.Join(" · ", new[]
        {
            v.Str("release_date") is { Length: >= 4 } rd ? rd[..4] : "",
            Ui.Duration(v.Double("duration")),
            v.Str("studio_name"),
        }.Where(x => x.Length > 0));
        var sub = Ui.Text(facts, 11.5, Theme.Muted, margin: new Thickness(2, 4, 2, 0));
        sub.TextTrimming = TextTrimming.CharacterEllipsis;
        var stack = Ui.Column(frame, title, sub);
        var lift = new TranslateTransform();
        stack.RenderTransform = lift;
        var card = Ui.Bare(stack, () => win.Navigate(new Location("video", id)));
        if (width != null) card.Width = width.Value;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-4, Quick) { EasingFunction = new CubicEase() });
            frame.BorderBrush = Theme.Ember;
        };
        card.MouseLeave += (_, _) =>
        {
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Quick) { EasingFunction = new CubicEase() });
            frame.BorderBrush = Theme.LineSoft;
        };
        return card;
    }

    /// <summary>date · quality · studio / sub-site · two of the cast (+ the rest as a count).</summary>
    public static WrapPanel MetaLine(MainWindow win, Row v, double size = 11.5)
    {
        var row = new WrapPanel();
        var needDot = false;
        void Dot()
        {
            if (needDot) row.Children.Add(Ui.Dot());
            needDot = true;
        }
        if (v.Str("release_date") is { Length: > 0 } date)
        {
            row.Children.Add(Ui.Text(Ui.Date(date), size, Theme.Muted));
            needDot = true;
        }
        if (Catalog.Quality(v) is { Length: > 0 } q)
        {
            if (row.Children.Count > 0) row.Children.Add(new Border { Width = 7 });
            row.Children.Add(Ui.Quality(q));
            needDot = true;
        }
        if (v.Str("studio_name") is { Length: > 0 } studio)
        {
            Dot();
            var sid = v.Long("studio_id") ?? 0;
            row.Children.Add(Ui.Link(studio, () => win.Navigate(new Location("studio", sid)), size));
        }
        if (v.Str("subsite") is { Length: > 0 } site)
        {
            // Part of the studio name on a card, in the sub-site blue.
            row.Children.Add(Ui.Text("/", size, Theme.Faint, margin: new Thickness(4, 0, 4, 0)));
            row.Children.Add(Ui.Link(site, () => win.Navigate(new Location("videos", Tag: site)), size, Theme.Subsite));
            needDot = true;
        }
        var cast = Catalog.Cast(v);
        if (cast.Count > 0)
        {
            Dot();
            for (var i = 0; i < Math.Min(2, cast.Count); i++)
            {
                if (i > 0) row.Children.Add(Ui.Dot());
                var aid = cast[i].Long("id") ?? 0;
                row.Children.Add(Ui.Link(cast[i].Str("name"), () => win.Navigate(new Location("actor", aid)), size,
                                         new SolidColorBrush(Color.FromRgb(0xA5, 0xA2, 0xB0))));
            }
            if (cast.Count > 2)
            {
                var more = Ui.Text($"+{cast.Count - 2}", size - 1, Theme.Faint, margin: new Thickness(6, 0, 0, 0));
                more.FontFamily = Theme.Mono;
                row.Children.Add(more);
            }
        }
        foreach (UIElement child in row.Children)
            if (child is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        return row;
    }

    // ------------------------------------------------------------------ stars
    /// <summary>A portrait at 435×600, or initials; the name; how many videos.</summary>
    public static FrameworkElement Portrait(Row a, double initialsSize = 30)
    {
        var grid = new Grid { ClipToBounds = true };
        var face = new Border { Background = new LinearGradientBrush(Color.FromRgb(0x16, 0x16, 0x1D), Color.FromRgb(0x1D, 0x1D, 0x26), 70) };
        grid.Children.Add(face);
        var photo = Catalog.ActorPhoto(a);
        var initials = Ui.Title(Names.Initials(a.Str("name")), initialsSize, false);
        initials.Foreground = new SolidColorBrush(Color.FromRgb(0x2E, 0x2E, 0x39));
        initials.HorizontalAlignment = HorizontalAlignment.Center;
        initials.VerticalAlignment = VerticalAlignment.Center;
        if (photo.Length > 0 && File.Exists(photo)) Ui.Cover(face, photo, 0.5, 0.3, decode: 600, fade: true);
        else grid.Children.Add(initials);
        return grid;
    }

    public static Button Star(MainWindow win, Row a, double? width = StarWidth)
    {
        var id = a.Long("id") ?? 0;
        var hidden = a.Truthy("hidden");
        var art = Portrait(a);
        if (hidden)
        {
            art.Opacity = 0.42;
        }
        var frame = new Border
        {
            CornerRadius = new CornerRadius(8), BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1),
            Background = Theme.Panel, Child = art,
        };
        // Rounded like the outline, inside it, so the photo never shows past its corners.
        art.SizeChanged += (_, e) => art.Clip = new RectangleGeometry(new Rect(e.NewSize), 7, 7);
        var host = new Grid();
        host.Children.Add(frame);
        if (hidden)
            host.Children.Add(new Border
            {
                Background = Theme.Alpha(Theme.InkC, 0xD0), BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2), Margin = new Thickness(6),
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
                Child = Ui.Caps("Hidden", 9.5, Theme.Muted, 0.08, FontWeights.Bold),
            });
        Aspect(host, 600.0 / 435);
        if (width is double knownWidth) host.Height = Math.Round(knownWidth * 600.0 / 435);
        var lift = new TranslateTransform();
        host.RenderTransform = lift;
        var nameText = Ui.Text(a.Str("name"), 13.5, Theme.Text, FontWeights.SemiBold);
        var name = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 8, 0, 0) };
        if (Gender.Mark(a.Str("gender"), 16) is { } mark)
        {
            mark.Margin = new Thickness(6, 0, 0, 0);
            DockPanel.SetDock(mark, Dock.Right);
            name.Children.Add(mark);
        }
        name.Children.Add(nameText);
        var count = a.Long("video_count") ?? 0;
        var sub = Ui.Text(Ui.Plural(count, "video", "videos"), 11.5, Theme.Muted);
        sub.FontFamily = Theme.Mono;
        var stack = Ui.Column(host, name, sub);
        var card = Ui.Bare(stack, () => win.Navigate(new Location("actor", id)));
        if (width != null) card.Width = width.Value;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) =>
        {
            frame.BorderBrush = Theme.Ember;
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-3, Quick));
            if (hidden) art.Opacity = 0.75;
        };
        card.MouseLeave += (_, _) =>
        {
            frame.BorderBrush = Theme.LineSoft;
            lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Quick));
            if (hidden) art.Opacity = 0.42;
        };
        return card;
    }

    // ---------------------------------------------------------------- studios
    public sealed record LogoLayout(string Fit, int Zoom, int X, int Y, string Background)
    {
        public static LogoLayout Of(Row s) => new(
            s.Str("logo_fit") is { Length: > 0 } f ? f : "contain",
            s.Int("logo_zoom") is int z and > 0 ? z : 100,
            s.Int("logo_x") ?? 50,
            s.Int("logo_y") ?? 50,
            s.Str("logo_bg") is { Length: > 0 } b ? b : "dark");

        public static readonly LogoLayout Default = new("contain", 100, 50, 50, "dark");
    }

    public static Brush LogoBackground(string key) => key switch
    {
        "black" => Brushes.Black,
        "white" => Brushes.White,
        "light" => new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF2)),
        "none" => Brushes.Transparent,
        _ => Theme.InkSoft,
    };

    /// <summary>A studio's logo in its box, fitted, zoomed and moved the way it was set.</summary>
    /// <summary>
    /// Every logo box has the same shape — on the Studios page, a studio's own
    /// page, the adjust window and search — so a logo set up once looks the same everywhere.
    /// </summary>
    public const double LogoRatio = 2.6;

    /// <summary>A studio's logo in its box, fitted, zoomed and moved the way it was set. No width: it fills its space.</summary>
    public static Border Logo(Row s, LogoLayout layout, double? width = null, string? fallback = null)
    {
        var box = new Border { CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = LogoBackground(layout.Background) };
        if (width is double w)
        {
            box.Width = w;
            box.Height = Math.Round(w / LogoRatio);
        }
        else Aspect(box, 1 / LogoRatio);
        box.SizeChanged += (_, e) => box.Clip = new RectangleGeometry(new Rect(e.NewSize), 4, 4);
        var file = Catalog.StudioLogo(s);
        if (file.Length > 0 && File.Exists(file))
        {
            var image = Images.Lazy(file, 520, layout.Fit switch
            {
                "cover" => Stretch.UniformToFill,
                "fill" => Stretch.Fill,
                "none" => Stretch.None,
                _ => Stretch.Uniform,
            });
            ApplyLayout(image, layout);
            box.Child = new Grid { ClipToBounds = true, Children = { image } };
        }
        else
        {
            var name = Ui.Text(fallback ?? s.Str("name"), 20, new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x3F)), FontWeights.ExtraBold, wrap: true);
            name.FontFamily = Theme.Display;
            name.TextAlignment = TextAlignment.Center;
            name.HorizontalAlignment = HorizontalAlignment.Center;
            name.VerticalAlignment = VerticalAlignment.Center;
            name.Margin = new Thickness(8, 0, 8, 0);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            box.SizeChanged += (_, e) =>
            {
                name.FontSize = Math.Clamp(e.NewSize.Height * 0.24, 9, 22);
                name.MaxHeight = Math.Max(0, e.NewSize.Height - 8);
            };
            box.Child = name;
        }
        return box;
    }

    /// <summary>Zoom about the centre, then a shift by a share of the box, as 1.x's CSS did.</summary>
    public static void ApplyLayout(Image image, LogoLayout layout)
    {
        image.RenderTransformOrigin = new Point(0.5, 0.5);
        var scale = new ScaleTransform(layout.Zoom / 100.0, layout.Zoom / 100.0);
        var shift = new TranslateTransform();
        var group = new TransformGroup();
        group.Children.Add(scale);
        group.Children.Add(shift);
        image.RenderTransform = group;
        void Place()
        {
            shift.X = (layout.X - 50) / 100.0 * image.ActualWidth;
            shift.Y = (layout.Y - 50) / 100.0 * image.ActualHeight;
        }
        image.SizeChanged += (_, _) => Place();
        Place();
    }

    public static Button Studio(MainWindow win, Row s, double? width = StudioWidth)
    {
        var id = s.Long("id") ?? 0;
        var logo = Logo(s, LogoLayout.Of(s));
        var name = Ui.Text(s.Str("name"), 14, Theme.Text, FontWeights.SemiBold, margin: new Thickness(0, 10, 0, 0));
        var count = s.Long("video_count") ?? 0;
        var sub = Ui.Text(Ui.Plural(count, "video", "videos"), 11.5, Theme.Muted);
        sub.FontFamily = Theme.Mono;
        var panel = new Border
        {
            Background = Theme.Panel, BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(14), Child = Ui.Column(logo, name, sub),
        };
        var card = Ui.Bare(panel, () => win.Navigate(new Location("studio", id)));
        if (width != null) card.Width = width.Value;
        card.VerticalAlignment = VerticalAlignment.Top;
        card.Cursor = Cursors.Hand;
        card.MouseEnter += (_, _) => panel.BorderBrush = Theme.Ember;
        card.MouseLeave += (_, _) => panel.BorderBrush = Theme.LineSoft;
        return card;
    }

    // ------------------------------------------------------------------ grids
    /// <summary>
    /// .grid — cards sharing the width, as many to a row as fit at the minimum
    /// width. Built in batches as the page scrolls, so a library of thousands
    /// opens at once.
    /// </summary>
    public static FrameworkElement Grid<T>(IList<T> items, Func<T, UIElement> make, double minWidth, double columnGap, double rowGap)
    {
        var grid = new UniformGrid { Columns = 4, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, -columnGap, 0) };
        var host = new Border { Child = grid };
        var made = 0;
        void More(int count)
        {
            for (var i = 0; i < count && made < items.Count; i++, made++)
                grid.Children.Add(new Border { Margin = new Thickness(0, 0, columnGap, rowGap), Child = make(items[made]) });
        }
        More(60);
        host.SizeChanged += (_, e) =>
        {
            var columns = Math.Max(1, (int)Math.Floor((e.NewSize.Width + columnGap) / (minWidth + columnGap)));
            if (grid.Columns != columns) grid.Columns = columns;
        };
        if (made < items.Count)
        {
            // Rows beyond the first batch arrive when the page nears its end.
            host.Loaded += (_, _) =>
            {
                if (FindScroller(host) is not { } page) return;
                void Check()
                {
                    if (made >= items.Count) return;
                    if (page.VerticalOffset + page.ViewportHeight * 2.5 >= page.ExtentHeight) More(60);
                }
                page.ScrollChanged += (_, _) => Check();
                host.Dispatcher.BeginInvoke(DispatcherPriority.Background, Check);
            };
        }
        return host;
    }

    static ScrollViewer? FindScroller(DependencyObject node)
    {
        for (var at = VisualTreeHelper.GetParent(node); at != null; at = VisualTreeHelper.GetParent(at))
            if (at is ScrollViewer sv && sv.Tag is not Ui.SideScrollTag) return sv;
        return null;
    }

    public static FrameworkElement VideoGrid(MainWindow win, IList<Row> items) =>
        Grid(items, v => Video(win, v, null), 290, 12, 20);

    public static FrameworkElement MovieGrid(MainWindow win, IList<Row> items) =>
        Grid(items, v => Movie(win, v, null), 200, 16, 24);

    /// <summary>Scenes as scene cards, movies as box covers, in one list.</summary>
    public static Button Any(MainWindow win, Row v, double? width = VideoWidth) =>
        Catalog.IsMovie(v) ? Movie(win, v, width == VideoWidth ? MovieWidth : width) : Video(win, v, width);

    public static FrameworkElement StarGrid(MainWindow win, IList<Row> items) =>
        Grid(items, a => Star(win, a, null), 178, 16, 24);

    public static FrameworkElement StudioGrid(MainWindow win, IList<Row> items) =>
        Grid(items, s => Studio(win, s, null), 228, 18, 18);

    // ------------------------------------------------------------------ chips
    /// <summary>.chip — a performer with their photo, round.</summary>
    public static Button PersonChip(MainWindow win, Row a)
    {
        var id = a.Long("id") ?? 0;
        var photo = Catalog.ActorPhoto(a);
        var disc = new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = Theme.Panel3 };
        if (photo.Length > 0 && File.Exists(photo)) Ui.Cover(disc, photo, 0.5, 0.25, decode: 64);
        else
        {
            var initials = Ui.Text(Names.Initials(a.Str("name")), 10, Theme.Muted);
            initials.HorizontalAlignment = HorizontalAlignment.Center;
            initials.VerticalAlignment = VerticalAlignment.Center;
            disc.Child = initials;
        }
        var name = Ui.Text(a.Str("name"), 12.5, Theme.Text, margin: new Thickness(7, 0, 0, 0));
        name.VerticalAlignment = VerticalAlignment.Center;
        return Pill(Ui.Row(disc, name), new Thickness(5, 5, 12, 5), () => win.Navigate(new Location("actor", id)));
    }

    public static Button TagChip(MainWindow win, string tag) =>
        Pill(Ui.Text(tag, 12.5, Theme.Text), new Thickness(12, 7, 12, 7), () => win.Navigate(new Location("videos", Tag: tag)));

    /// <summary>.subsite — the sub-site pill, in its own cool blue.</summary>
    public static Button SubsitePill(MainWindow win, string site)
    {
        var face = new Pill
        {
            Background = Theme.Alpha(Theme.SubsiteC, 0x1F), BorderBrush = Theme.Alpha(Theme.SubsiteC, 0x57), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3),
            Child = Ui.Caps(site, 11, Theme.Subsite, 0.06),
        };
        var b = Ui.Bare(face, () => win.Navigate(new Location("videos", Tag: site)));
        b.Cursor = Cursors.Hand;
        b.Focusable = false;
        b.MouseEnter += (_, _) => { face.Background = Theme.Alpha(Theme.SubsiteC, 0x38); face.BorderBrush = Theme.Subsite; };
        b.MouseLeave += (_, _) => { face.Background = Theme.Alpha(Theme.SubsiteC, 0x1F); face.BorderBrush = Theme.Alpha(Theme.SubsiteC, 0x57); };
        return b;
    }

    static Button Pill(UIElement content, Thickness padding, Action open)
    {
        var face = new Pill
        {
            Background = Theme.Alpha(Colors.White, 0x12), BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Padding = padding, Child = content, Margin = new Thickness(0, 0, 7, 0),
        };
        var b = Ui.Bare(face, open);
        b.Cursor = Cursors.Hand;
        b.Focusable = false;
        b.MouseEnter += (_, _) => { face.BorderBrush = Theme.Ember; face.Background = Theme.EmberWash; };
        b.MouseLeave += (_, _) => { face.BorderBrush = Theme.Line; face.Background = Theme.Alpha(Colors.White, 0x12); };
        return b;
    }
}

/// <summary>
/// The looping preview that plays over a card while the mouse rests on it.
/// One at a time; it starts after a short pause so sweeping across a row
/// does not set every card going.
/// </summary>
public static class HoverPreview
{
    static readonly DispatcherTimer Delay = new() { Interval = TimeSpan.FromMilliseconds(380) };
    static Border? _host;
    static string _file = "";
    static MediaElement? _media;

    static HoverPreview() => Delay.Tick += (_, _) => { Delay.Stop(); Begin(); };

    public static void Start(Border host, string file)
    {
        Stop(_host);
        if (!File.Exists(file)) return;
        _host = host;
        _file = file;
        Delay.Stop();
        Delay.Start();
    }

    static void Begin()
    {
        if (_host == null) return;
        var media = Loop(_file);
        _media = media;
        _host.Child = media;
        media.Play();
    }

    /// <summary>A silent video that plays round and round and fades in when it has a picture.</summary>
    public static MediaElement Loop(string file, Stretch stretch = Stretch.UniformToFill)
    {
        var media = new MediaElement
        {
            Source = new Uri(file),
            IsMuted = true,
            LoadedBehavior = MediaState.Manual,
            UnloadedBehavior = MediaState.Close,
            Stretch = stretch,
            Opacity = 0,
            ScrubbingEnabled = false,
            IsHitTestVisible = false,
        };
        RenderOptions.SetBitmapScalingMode(media, BitmapScalingMode.HighQuality);
        media.MediaOpened += (_, _) => media.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(350)));
        media.MediaEnded += (_, _) =>
        {
            media.Position = TimeSpan.Zero;
            media.Play();
        };
        media.MediaFailed += (_, _) => media.Visibility = Visibility.Collapsed;
        return media;
    }

    static readonly List<MediaElement> Backdrops = new();

    /// <summary>A page's looping backdrop, paused while the player covers it.</summary>
    public static void Register(MediaElement media, bool on)
    {
        if (on) Backdrops.Add(media);
        else Backdrops.Remove(media);
    }

    public static void PauseBackdrops()
    {
        foreach (var m in Backdrops.ToList())
            try { m.Pause(); } catch { }
    }

    public static void PlayBackdrops()
    {
        foreach (var m in Backdrops.ToList())
            try { m.Play(); } catch { }
    }

    public static void Stop(Border? host)
    {
        Delay.Stop();
        host ??= _host;
        if (host == null || !ReferenceEquals(host, _host)) return;
        if (_media != null)
        {
            try
            {
                _media.Stop();
                _media.Close();
            }
            catch { }
        }
        host.Child = null;
        _media = null;
        _host = null;
    }
}
