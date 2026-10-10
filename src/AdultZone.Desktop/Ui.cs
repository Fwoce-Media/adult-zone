using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using AdultZone.Core;
using Path = System.Windows.Shapes.Path;

namespace AdultZone.Desktop;

/// <summary>Small builders so every screen is put together the same way.</summary>
public static class Ui
{
    public const double Gutter = 48;

    public static TextBlock Text(string text, double size = 14, Brush? color = null, FontWeight? weight = null,
                                 bool wrap = false, Thickness? margin = null)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = size,
            Foreground = color ?? Theme.Text,
            FontWeight = weight ?? FontWeights.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            Margin = margin ?? new Thickness(0),
        };
    }

    /// <summary>A display-face heading, tight like 1.x's.</summary>
    public static TextBlock Title(string text, double size, bool wrap = true)
    {
        var t = Text(text, size, Theme.Text, FontWeights.ExtraBold, wrap);
        t.FontFamily = Theme.Display;
        t.LineHeight = Math.Round(size * 1.06);
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        return t;
    }

    /// <summary>A block of text cut off after a number of lines.</summary>
    public static TextBlock Clamp(string text, double size, Brush color, int lines, double lineHeight, FontWeight? weight = null)
    {
        var t = Text(text, size, color, weight, wrap: true);
        t.LineHeight = lineHeight;
        t.LineStackingStrategy = LineStackingStrategy.BlockLineHeight;
        t.MaxHeight = lineHeight * lines;
        t.TextTrimming = TextTrimming.WordEllipsis;
        return t;
    }

    /// <summary>Upper-case, letter-spaced text.</summary>
    public static Tracked Caps(string text, double size, Brush color, double em, FontWeight? weight = null) =>
        new Tracked { Size = size, Em = em }.Add(text.ToUpperInvariant(), color, weight ?? FontWeights.SemiBold);

    /// <summary>.eyebrow — the small ember line above a heading.</summary>
    public static Tracked Eyebrow(string text, Brush? color = null) => Caps(text, 11, color ?? Theme.Ember, 0.16);

    /// <summary>The ADULT ZONE wordmark.</summary>
    public static Tracked Wordmark(double size) =>
        new Tracked { Size = size, Em = -0.035, Family = Theme.Display }
            .Add("ADULT", Theme.Text, FontWeights.ExtraBold).Add("ZONE", Theme.Ember, FontWeights.ExtraBold);

    /// <summary>A button's face: an optional icon, then its label.</summary>
    public static StackPanel Label(string text, FrameworkElement? icon = null, double size = 14, Brush? color = null)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        if (icon != null)
        {
            icon.Margin = new Thickness(0, 0, text.Length > 0 ? 8 : 0, 0);
            row.Children.Add(icon);
        }
        if (text.Length > 0)
        {
            var t = Text(text, size, color, FontWeights.SemiBold);
            t.VerticalAlignment = VerticalAlignment.Center;
            if (color == null) t.ClearValue(TextBlock.ForegroundProperty);
            row.Children.Add(t);
        }
        return row;
    }

    public enum Look { Plain, Ember, Ghost, Play, Warn }

    public static Button Button(string label, Action onClick, Look look = Look.Ghost, FrameworkElement? icon = null, bool small = false)
    {
        var b = new Button { Content = Label(label, icon, small ? 13 : 14) };
        var style = look switch { Look.Ember or Look.Play => Theme.Style("Primary"), Look.Ghost or Look.Warn => Theme.Style("Ghost"), _ => null };
        if (style != null) b.Style = style;
        if (look == Look.Play)
        {
            b.Background = Theme.Text;
            b.Foreground = new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0E));
        }
        if (look == Look.Warn) b.Foreground = Theme.Warn;
        if (small) b.Padding = new Thickness(14, 7, 14, 7);
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public static Button Bare(UIElement content, Action onClick, string? tip = null)
    {
        var b = new Button { Content = content };
        if (Theme.Style("Bare") is { } style) b.Style = style;
        else
        {
            b.Background = Theme.Clear;
            b.BorderThickness = new Thickness(0);
            b.Padding = new Thickness(0);
        }
        if (tip != null) b.ToolTip = tip;
        b.Click += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    /// <summary>A text link that turns ember under the mouse.</summary>
    public static Button Link(string text, Action onClick, double size = 12.5, Brush? color = null, FontWeight? weight = null)
    {
        var rest = color ?? Theme.Muted;
        var t = Text(text, size, rest, weight);
        var b = Bare(t, onClick);
        b.Focusable = false;
        b.Cursor = Cursors.Hand;
        b.MouseEnter += (_, _) => t.Foreground = Theme.EmberHi;
        b.MouseLeave += (_, _) => t.Foreground = rest;
        return b;
    }

    /// <summary>.q — the HD / SD / 2K / 4K badge.</summary>
    public static Border Quality(string label)
    {
        var colour = label switch { "HD" => Theme.Hd, "SD" => Theme.Faint, _ => Theme.Ember };
        return new Border
        {
            BorderBrush = colour,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = label, FontSize = 10, FontWeight = FontWeights.Bold, FontFamily = Theme.Mono, Foreground = colour },
        };
    }

    /// <summary>.dot — the small separator between facts.</summary>
    public static Ellipse Dot() => new()
    {
        Width = 3, Height = 3, Fill = Theme.Faint, Margin = new Thickness(7, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Facts in a line with dots between them.</summary>
    public static WrapPanel Facts(IEnumerable<UIElement?> items, double size = 13, Brush? color = null)
    {
        var row = new WrapPanel();
        var first = true;
        foreach (var item in items)
        {
            if (item == null) continue;
            if (!first) row.Children.Add(Dot());
            first = false;
            if (item is TextBlock t)
            {
                t.FontSize = size;
                t.Foreground = color ?? Theme.Muted;
            }
            if (item is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(item);
        }
        return row;
    }

    public static ComboBox Select(IEnumerable<(string Key, string Label)> options, string current, Action<string> picked, double width = 180)
    {
        var box = new ComboBox { Width = width, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, label) in options)
        {
            box.Items.Add(new ComboBoxItem { Content = label, Tag = key });
            if (key == current) box.SelectedIndex = box.Items.Count - 1;
        }
        if (box.SelectedIndex < 0 && box.Items.Count > 0) box.SelectedIndex = 0;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedItem is ComboBoxItem { Tag: string key }) picked(key);
        };
        return box;
    }

    public static StackPanel Row(params UIElement[] children)
    {
        var s = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static StackPanel Column(params UIElement[] children)
    {
        var s = new StackPanel();
        foreach (var c in children) s.Children.Add(c);
        return s;
    }

    public static T Margin<T>(this T element, double left, double top, double right, double bottom) where T : FrameworkElement
    {
        element.Margin = new Thickness(left, top, right, bottom);
        return element;
    }

    public static WrapPanel Actions(params UIElement[] buttons)
    {
        var w = new WrapPanel();
        foreach (var b in buttons)
        {
            if (b is FrameworkElement fe) fe.Margin = new Thickness(0, 0, 10, 10);
            w.Children.Add(b);
        }
        return w;
    }

    /// <summary>.page-head — eyebrow, big title, a muted line under it, controls on the right.</summary>
    public static FrameworkElement PageHead(string eyebrow, string title, string? sub = null, UIElement? controls = null)
    {
        var dock = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 22) };
        if (controls != null)
        {
            DockPanel.SetDock(controls, Dock.Right);
            if (controls is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Bottom;
            dock.Children.Add(controls);
        }
        var left = new StackPanel();
        if (eyebrow.Length > 0) left.Children.Add(Eyebrow(eyebrow));
        left.Children.Add(Title(title, 36, false).Margin(0, 4, 0, 0));
        if (!string.IsNullOrEmpty(sub)) left.Children.Add(Text(sub, 13.5, Theme.Muted, margin: new Thickness(0, 4, 0, 0)));
        dock.Children.Add(left);
        return dock;
    }

    /// <summary>A scrolling page with the gutters, clear of the top bar.</summary>
    public static ScrollViewer Page(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        Content = new Border { Padding = new Thickness(Gutter, MainWindow.BarHeight + 26, Gutter, 90), Child = content },
    };

    /// <summary>.empty — the dashed box shown when there is nothing to list.</summary>
    public static FrameworkElement Empty(string title, UIElement? action = null)
    {
        var s = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        var h = Title(title, 20);
        h.HorizontalAlignment = HorizontalAlignment.Center;
        s.Children.Add(h);
        if (action is FrameworkElement fe)
        {
            fe.Margin = new Thickness(0, 18, 0, 0);
            fe.HorizontalAlignment = HorizontalAlignment.Center;
            s.Children.Add(fe);
        }
        var grid = new Grid();
        grid.Children.Add(new Rectangle
        {
            Stroke = Theme.Line, StrokeThickness = 1, StrokeDashArray = new DoubleCollection { 4, 3 },
            RadiusX = 14, RadiusY = 14, SnapsToDevicePixels = true,
        });
        grid.Children.Add(new Border { Padding = new Thickness(20, 70, 20, 70), Child = s });
        return grid;
    }

    /// <summary>.panel — a settings group.</summary>
    public static Border Panel(string title, params UIElement[] children)
    {
        var stack = new StackPanel();
        stack.Children.Add(Text(title, 15, Theme.Text, FontWeights.Bold, margin: new Thickness(0, 0, 0, 14)));
        foreach (var c in children) stack.Children.Add(c);
        return new Border
        {
            Background = Theme.Panel,
            BorderBrush = Theme.LineSoft,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20),
            Margin = new Thickness(0, 0, 0, 18),
            Child = stack,
        };
    }

    /// <summary>label.field — a small spaced label over its control.</summary>
    public static StackPanel Field(string label, UIElement control, double width = double.NaN)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 14, 14), Width = width };
        s.Children.Add(Caps(label, 10.5, Theme.Faint, 0.13).Margin(0, 0, 0, 6));
        if (control is FrameworkElement fe && fe is TextBox or ComboBox or PasswordBox) fe.HorizontalAlignment = HorizontalAlignment.Stretch;
        s.Children.Add(control);
        return s;
    }

    /// <summary>A thin ember bar.</summary>
    public static FrameworkElement ProgressBar(double fraction, double height = 5)
    {
        fraction = Math.Clamp(fraction, 0, 1);
        var grid = new Grid { Height = height };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(fraction, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1 - fraction, GridUnitType.Star) });
        var track = new Border { Background = Theme.Panel2, CornerRadius = new CornerRadius(height / 2) };
        Grid.SetColumnSpan(track, 2);
        grid.Children.Add(track);
        grid.Children.Add(new Border { Background = Theme.Ember, CornerRadius = new CornerRadius(height / 2) });
        return grid;
    }

    /// <summary>Paints a picture over an element like CSS background-size: cover, anchored at a point.</summary>
    /// <summary>
    /// The part of a picture that fills a frame: as a share of the picture's width and height,
    /// placed by the point kept in view and narrowed by the zoom.
    /// </summary>
    public static Rect CoverBox(double picture, double box, double anchorX, double anchorY, double zoom = 1)
    {
        double w = 1, h = 1;
        if (picture > box) w = box / picture;
        else h = picture / box;
        zoom = Math.Max(1, zoom);
        w /= zoom;
        h /= zoom;
        return new Rect((1 - w) * anchorX, (1 - h) * anchorY, w, h);
    }

    public static void Cover(Border host, string? path, double anchorX = 0.5, double anchorY = 0.5, int decode = 1920, bool fade = false, double zoom = 1)
    {
        if (string.IsNullOrEmpty(path)) return;
        host.Tag = path;
        _ = Images.LoadAsync(path, decode).ContinueWith(t =>
        {
            var bmp = t.Result;
            if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelHeight == 0 || !Equals(host.Tag, path)) return;
            var brush = new ImageBrush(bmp) { Stretch = Stretch.Fill, ViewboxUnits = BrushMappingMode.RelativeToBoundingBox };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.HighQuality);
            void Fit()
            {
                if (host.ActualWidth <= 0 || host.ActualHeight <= 0) return;
                brush.Viewbox = CoverBox((double)bmp.PixelWidth / bmp.PixelHeight, host.ActualWidth / host.ActualHeight, anchorX, anchorY, zoom);
            }
            Fit();
            host.SizeChanged += (_, _) => { if (ReferenceEquals(host.Background, brush)) Fit(); };
            host.Background = brush;
            if (fade) brush.BeginAnimation(Brush.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    /// <summary>
    /// A match's picture shown whole inside its frame, from the first of its addresses that still answers.
    /// </summary>
    public static async void Whole(Border host, IEnumerable<string> addresses, int decode, bool fitShape = false)
    {
        var list = addresses.Where(a => !string.IsNullOrEmpty(a)).Distinct().ToList();
        if (list.Count == 0) return;
        host.Tag = list[0];
        foreach (var address in list)
        {
            BitmapSource? bmp;
            try { bmp = await Images.LoadAsync(address, decode); }
            catch { bmp = null; }
            if (!Equals(host.Tag, list[0])) return;
            if (bmp == null || bmp.PixelWidth == 0 || bmp.PixelHeight == 0) continue;
            var image = new Image { Source = bmp, Stretch = Stretch.Uniform };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            // The frame takes the picture's own shape: same height, as wide as the picture is.
            if (fitShape) FitShape(host, (double)bmp.PixelWidth / bmp.PixelHeight, image);
            host.Child = image;
            return;
        }
    }

    /// <summary>Sizes a picture's frame to the picture: its height kept, its width following the picture's shape.</summary>
    public static void FitShape(Border host, double aspect, Image image)
    {
        if (double.IsNaN(host.Height) || host.Height <= 0 || aspect <= 0) return;
        host.Width = Math.Round(Math.Clamp(host.Height * aspect, host.Height * 0.45, host.Height * 2.6));
        image.Stretch = Stretch.UniformToFill;
    }

    /// <summary>A small scroller inside a page: it takes the wheel while it can move, then hands it on.</summary>
    public static void ChainWheel(ScrollViewer inner)
    {
        inner.PreviewMouseWheel += (_, e) =>
        {
            // Only one of the two moves: this one while it can, then the page.
            e.Handled = true;
            var up = e.Delta > 0;
            var atEnd = inner.ScrollableHeight <= 0.5 ||
                        (up ? inner.VerticalOffset <= 0 : inner.VerticalOffset >= inner.ScrollableHeight - 0.5);
            if (!atEnd)
            {
                inner.ScrollToVerticalOffset(Math.Clamp(inner.VerticalOffset - e.Delta * 0.5, 0, inner.ScrollableHeight));
                return;
            }
            (VisualTreeHelper.GetParent(inner) as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = inner,
            });
        };
        inner.Tag ??= InnerScrollTag;
    }

    /// <summary>Marks a scroller inside a page that a finger moves before the page.</summary>
    public const string InnerScrollTag = "inner-scroll";

    /// <summary>A sideways scroller lets the wheel through to the page; Shift+wheel moves it sideways.</summary>
    public static void PassWheelToParent(ScrollViewer inner)
    {
        inner.PreviewMouseWheel += (_, e) =>
        {
            e.Handled = true;
            if (Keyboard.Modifiers == ModifierKeys.Shift)
            {
                inner.ScrollToHorizontalOffset(inner.HorizontalOffset - e.Delta);
                return;
            }
            (VisualTreeHelper.GetParent(inner) as UIElement)?.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta)
            {
                RoutedEvent = UIElement.MouseWheelEvent,
                Source = inner,
            });
        };
    }

    /// <summary>Marks a row that sideways wheels (tilt wheel, touchpad) move.</summary>
    public const string SideScrollTag = "side-scroll";

    /// <summary>Press and drag a row sideways to move through it. A drag never counts as a click.</summary>
    public static void DragToScroll(ScrollViewer scroller)
    {
        Point? start = null;
        var origin = 0.0;
        var dragging = false;
        scroller.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.GetPosition(scroller);
            origin = scroller.HorizontalOffset;
            dragging = false;
        };
        scroller.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || e.LeftButton != MouseButtonState.Pressed) { start = null; return; }
            var at = e.GetPosition(scroller);
            var dx = at.X - from.X;
            var dy = at.Y - from.Y;
            if (!dragging)
            {
                if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8) return;
                if (Math.Abs(dy) >= Math.Abs(dx)) { start = null; return; }
                dragging = true;
                scroller.CaptureMouse();
                scroller.Cursor = Cursors.SizeWE;
            }
            scroller.ScrollToHorizontalOffset(Math.Max(0, origin - dx));
            e.Handled = true;
        };
        scroller.PreviewMouseLeftButtonUp += (_, e) =>
        {
            start = null;
            if (!dragging) return;
            dragging = false;
            scroller.ReleaseMouseCapture();
            scroller.Cursor = null;
            e.Handled = true;
        };
        scroller.LostMouseCapture += (_, _) =>
        {
            dragging = false;
            scroller.Cursor = null;
        };
    }

    /// <summary>A finger swiped up or down anywhere on a page scrolls it. Only touch does this.</summary>
    public static void TouchScroll(ScrollViewer page)
    {
        Point? start = null;
        var origin = 0.0;
        var innerOrigin = 0.0;
        ScrollViewer? inner = null;
        var dragging = false;
        page.PreviewMouseLeftButtonDown += (_, e) =>
        {
            start = e.StylusDevice != null ? e.GetPosition(page) : null;
            origin = page.VerticalOffset;
            dragging = false;
            inner = null;
            if (start == null) return;
            // A swipe that starts on a scrolling box inside the page moves that box.
            for (var at = e.OriginalSource as DependencyObject; at != null && !ReferenceEquals(at, page);
                 at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
                if (at is ScrollViewer { Tag: InnerScrollTag } box && box.ScrollableHeight > 0.5)
                {
                    inner = box;
                    innerOrigin = box.VerticalOffset;
                    break;
                }
        };
        page.PreviewMouseMove += (_, e) =>
        {
            if (start is not { } from || e.LeftButton != MouseButtonState.Pressed) { start = null; return; }
            var at = e.GetPosition(page);
            var dx = at.X - from.X;
            var dy = at.Y - from.Y;
            if (!dragging)
            {
                if (Math.Abs(dx) < 8 && Math.Abs(dy) < 8) return;
                if (Math.Abs(dx) > Math.Abs(dy)) { start = null; return; }
                dragging = true;
                page.CaptureMouse();
            }
            if (inner != null)
            {
                // The box takes the swipe; what it cannot use goes to the page.
                var wanted = innerOrigin - dy;
                var used = Math.Clamp(wanted, 0, inner.ScrollableHeight);
                inner.ScrollToVerticalOffset(used);
                page.ScrollToVerticalOffset(Math.Max(0, origin + (wanted - used)));
            }
            else page.ScrollToVerticalOffset(Math.Max(0, origin - dy));
            e.Handled = true;
        };
        page.PreviewMouseLeftButtonUp += (_, e) =>
        {
            start = null;
            if (!dragging) return;
            dragging = false;
            page.ReleaseMouseCapture();
            e.Handled = true;
        };
        page.LostMouseCapture += (_, _) => dragging = false;
    }

    /// <summary>A sideways wheel turn over a row moves that row. True when one took it.</summary>
    public static bool SideWheel(int delta)
    {
        if (Mouse.DirectlyOver is not DependencyObject node) return false;
        for (var at = node; at != null; at = at is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(at) : LogicalTreeHelper.GetParent(at))
            if (at is ScrollViewer { Tag: SideScrollTag } row)
            {
                row.ScrollToHorizontalOffset(Math.Max(0, row.HorizontalOffset + delta));
                return true;
            }
        return false;
    }

    /// <summary>
    /// .row — a heading, then a track of cards that scrolls sideways, with
    /// arrows at either end that show on hover.
    /// </summary>
    public static FrameworkElement Shelf(string title, IList<UIElement> items, double gap, Action? seeAll = null)
    {
        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(Gutter, 0, Gutter, 12) };
        var h2 = Title(title, 19, false);
        h2.VerticalAlignment = VerticalAlignment.Center;
        if (seeAll != null)
        {
            var link = Link("See all ›", seeAll, 12.5);
            link.VerticalAlignment = VerticalAlignment.Center;
            DockPanel.SetDock(link, Dock.Right);
            head.Children.Add(link);
        }
        head.Children.Add(h2);

        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(Gutter, 4, Gutter, 14) };
        foreach (var item in items)
        {
            if (item is FrameworkElement fe) fe.Margin = new Thickness(0, 0, gap, 0);
            panel.Children.Add(item);
        }
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = panel,
            Focusable = false,
            CanContentScroll = false,
        };
        PassWheelToParent(scroller);
        DragToScroll(scroller);
        scroller.Tag = SideScrollTag;

        Button Arrow(int direction)
        {
            var fade = new LinearGradientBrush
            {
                StartPoint = new Point(direction < 0 ? 0 : 1, 0),
                EndPoint = new Point(direction < 0 ? 1 : 0, 0),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0xF0, 0x08, 0x08, 0x0B), 0.3),
                    new GradientStop(Color.FromArgb(0x00, 0x08, 0x08, 0x0B), 1),
                },
            };
            var mark = new Border { Child = Icons.Chevron(direction > 0, 26, Brushes.White) };
            var face = new Grid { Background = fade };
            face.Children.Add(mark);
            var b = Bare(face, () =>
            {
                var step = Math.Max(260, scroller.ViewportWidth * 0.82);
                scroller.ScrollToHorizontalOffset(Math.Max(0, scroller.HorizontalOffset + direction * step));
            });
            b.Width = 60;
            b.Margin = new Thickness(0, 4, 0, 14);
            b.HorizontalAlignment = direction < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            b.Visibility = Visibility.Collapsed;
            b.Focusable = false;
            b.MouseEnter += (_, _) => mark.Child = Icons.Chevron(direction > 0, 26, Theme.Ember);
            b.MouseLeave += (_, _) => mark.Child = Icons.Chevron(direction > 0, 26, Brushes.White);
            return b;
        }
        var prev = Arrow(-1);
        var next = Arrow(1);
        var track = new Grid();
        track.Children.Add(scroller);
        track.Children.Add(prev);
        track.Children.Add(next);
        void Update()
        {
            var slack = scroller.ExtentWidth - scroller.ViewportWidth;
            var hover = track.IsMouseOver;
            prev.Visibility = hover && slack > 4 && scroller.HorizontalOffset > 4 ? Visibility.Visible : Visibility.Collapsed;
            next.Visibility = hover && slack > 4 && scroller.HorizontalOffset < slack - 4 ? Visibility.Visible : Visibility.Collapsed;
        }
        scroller.ScrollChanged += (_, _) => Update();
        track.MouseEnter += (_, _) => Update();
        track.MouseLeave += (_, _) => Update();

        var row = Column(head, track);
        row.Margin = new Thickness(0, 0, 0, 34);
        return row;
    }

    // ---------------------------------------------------------------- formats
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Clock(double seconds)
    {
        var s = (long)Math.Max(0, Math.Round(seconds));
        var h = s / 3600;
        var m = s % 3600 / 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }

    /// <summary>fmtDuration: blank for nothing.</summary>
    public static string Duration(double? seconds) => seconds is > 0.5 ? Clock(seconds.Value) : "";

    static DateTime? ParseDate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        text = text.Trim();
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", Inv, DateTimeStyles.None, out var day)) return day;
        // datetime('now') values are UTC.
        if (DateTime.TryParse(text.Replace(' ', 'T'), Inv, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc))
            return utc.ToLocalTime();
        return null;
    }

    /// <summary>"27 Jul 2021", in the reader's own date order.</summary>
    public static string Date(string? text) => ParseDate(text) is { } d ? d.ToString("d MMM yyyy", CultureInfo.CurrentCulture) : text ?? "";

    public static int? AgeFrom(string? birthdate)
    {
        if (ParseDate(birthdate) is not { } dob) return null;
        var now = DateTime.Today;
        var age = now.Year - dob.Year;
        if (now.Month < dob.Month || (now.Month == dob.Month && now.Day < dob.Day)) age--;
        return age is >= 0 and < 130 ? age : null;
    }

    public static string Views(long? n) => (n ?? 0) == 1 ? "1 view" : $"{(n ?? 0).ToString("N0", CultureInfo.CurrentCulture)} views";

    public static string Size(long? bytes)
    {
        if (bytes is not > 0) return "";
        string[] units = { "B", "KB", "MB", "GB", "TB" };
        double v = bytes.Value;
        var i = 0;
        while (v >= 1024 && i < units.Length - 1) { v /= 1024; i++; }
        return v.ToString(v < 10 && i > 1 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[i];
    }

    public static string Plural(long n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString("N0", CultureInfo.CurrentCulture)} {many}";
}

/// <summary>
/// A capsule: its ends stay perfect half-circles at any height. WPF draws a
/// corner radius larger than the box as a lens, so the radius follows the height.
/// </summary>
public sealed class Pill : Border
{
    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        var r = info.NewSize.Height / 2;
        if (Math.Abs(CornerRadius.TopLeft - r) > 0.1) CornerRadius = new CornerRadius(r);
    }
}

/// <summary>Text with letter-spacing, which WPF's own text cannot do.</summary>
public sealed class Tracked : FrameworkElement
{
    readonly List<(string Text, Brush Brush, FontWeight Weight)> _parts = new();
    readonly List<(FormattedText Glyph, double X)> _laid = new();
    double _width, _height, _baseline;

    public FontFamily Family { get; set; } = Theme.Sans;
    public double Size { get; set; } = 12;
    public double Em { get; set; } = 0.1;

    public Tracked Add(string text, Brush brush, FontWeight? weight = null)
    {
        _parts.Add((text, brush, weight ?? FontWeights.Normal));
        InvalidateMeasure();
        InvalidateVisual();
        return this;
    }

    public void Set(string text, Brush brush, FontWeight? weight = null)
    {
        _parts.Clear();
        Add(text, brush, weight);
    }

    void Layout()
    {
        _laid.Clear();
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        double x = 0, h = 0, b = 0;
        var gap = Size * Em;
        var first = true;
        foreach (var (text, brush, weight) in _parts)
        {
            var face = new Typeface(Family, FontStyles.Normal, weight, FontStretches.Normal);
            var e = StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext())
            {
                var ch = e.GetTextElement();
                if (!first) x += gap;
                first = false;
                var ft = new FormattedText(ch, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, Size, brush, dpi);
                _laid.Add((ft, x));
                x += ft.WidthIncludingTrailingWhitespace;
                h = Math.Max(h, ft.Height);
                b = Math.Max(b, ft.Baseline);
            }
        }
        _width = Math.Max(0, x);
        _height = h > 0 ? h : Size * 1.3;
        _baseline = b;
    }

    protected override Size MeasureOverride(Size available)
    {
        Layout();
        return new Size(_width, _height);
    }

    protected override void OnRender(DrawingContext dc)
    {
        foreach (var (glyph, x) in _laid)
            dc.DrawText(glyph, new Point(x, _baseline - glyph.Baseline));
    }
}

/// <summary>Pictures, loaded off the UI thread and kept in memory at the size shown.</summary>
public static class Images
{
    static readonly ConcurrentDictionary<string, BitmapSource?> Cache = new();
    static readonly System.Threading.SemaphoreSlim Throttle = new(6);

    static string KeyFor(string path, int width)
    {
        // A replaced file gets a new key, so an edited picture shows at once.
        var stamp = 0L;
        try { if (File.Exists(path)) stamp = File.GetLastWriteTimeUtc(path).Ticks; } catch { }
        return $"{width}|{stamp}|{path}";
    }

    public static async Task<BitmapSource?> LoadAsync(string? path, int decodeWidth)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var key = KeyFor(path, decodeWidth);
        if (Cache.TryGetValue(key, out var hit)) return hit;
        await Throttle.WaitAsync();
        try
        {
            var image = await Task.Run(() => Decode(path, decodeWidth));
            if (image != null) Cache[key] = image;
            return image;
        }
        finally
        {
            Throttle.Release();
        }
    }

    static BitmapSource? Decode(string path, int decodeWidth)
    {
        try
        {
            var bytes = path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? WebImages.Bytes(path) : File.Exists(path) ? File.ReadAllBytes(path) : null;
            if (bytes == null || bytes.Length < 64) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            if (decodeWidth > 0) bmp.DecodePixelWidth = decodeWidth;
            bmp.StreamSource = new MemoryStream(bytes);
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    public static void Clear() => Cache.Clear();

    /// <summary>An Image that fades itself in when its picture arrives.</summary>
    public static Image Lazy(string? path, int decodeWidth, Stretch stretch = Stretch.UniformToFill)
    {
        var image = new Image { Stretch = stretch, Opacity = 0 };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
        if (string.IsNullOrEmpty(path)) return image;
        if (Cache.TryGetValue(KeyFor(path, decodeWidth), out var ready) && ready != null)
        {
            image.Source = ready;
            image.Opacity = 1;
            return image;
        }
        _ = Fill(image, path, decodeWidth);
        return image;
    }

    static async Task Fill(Image image, string path, int width)
    {
        var bmp = await LoadAsync(path, width);
        if (bmp == null) return;
        image.Source = bmp;
        image.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }
}

/// <summary>Search results' pictures, fetched once and kept while the app runs.</summary>
public static class WebImages
{
    static readonly ConcurrentDictionary<string, byte[]?> Fetched = new();

    public static byte[]? Bytes(string url) => Fetched.GetOrAdd(url, u =>
    {
        try { return Core.Providers.Http.GetImage(u).Data; }
        catch { return null; }
    });
}

/// <summary>1.x's line icons, drawn on the 24×24 grid they were designed on.</summary>
public static class Icons
{
    static Path Stroke(string data, Brush brush, double thickness = 1.7) => new()
    {
        Data = Geometry.Parse(data),
        Stroke = brush,
        StrokeThickness = thickness,
        StrokeStartLineCap = PenLineCap.Round,
        StrokeEndLineCap = PenLineCap.Round,
        StrokeLineJoin = PenLineJoin.Round,
    };

    static Path Fill(string data, Brush brush) => new() { Data = Geometry.Parse(data), Fill = brush };

    static string Circle(double cx, double cy, double r) =>
        FormattableString.Invariant($"M{cx - r},{cy} A{r},{r} 0 1 1 {cx + r},{cy} A{r},{r} 0 1 1 {cx - r},{cy} Z");

    static Viewbox Box(double size, params UIElement[] parts)
    {
        var canvas = new Grid { Width = 24, Height = 24 };
        foreach (var p in parts) canvas.Children.Add(p);
        return new Viewbox
        {
            Width = size, Height = size, Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            SnapsToDevicePixels = true,
        };
    }

    public static Viewbox Back(double size, Brush b) => Box(size, Stroke("M15,5 L8,12 L15,19", b, 2.1));

    public static Viewbox Search(double size, Brush b) => Box(size, Stroke(Circle(11, 11, 7) + " M20,20 L16.5,16.5", b));

    public static Viewbox Scan(double size, Brush b) =>
        Box(size, Stroke("M20.5,12 A8.5,8.5 0 1 1 17.9,5.9 M20.6,4.4 L20.6,8.8 L16.2,8.8", b));

    public static Viewbox Settings(double size, Brush b) =>
        Box(size, Stroke(Circle(12, 12, 3.2) + " M12,2.8 L12,5.2 M12,18.8 L12,21.2 M4.5,12 L2.1,12 M21.9,12 L19.5,12 M6.2,6.2 L4.5,4.5 M19.5,19.5 L17.8,17.8 M17.8,6.2 L19.5,4.5 M4.5,19.5 L6.2,17.8", b));

    public static Viewbox Lock(double size, Brush b) =>
        Box(size, Stroke("M6,11 L18,11 L18,20 L6,20 Z M8.5,11 L8.5,8 A3.5,3.5 0 0 1 15.5,8 L15.5,11", b));

    public static Viewbox Play(double size, Brush b) => Box(size, Fill("M7,4.5 L7,19.5 L20,12 Z", b));

    public static Viewbox Heart(double size, Brush b, bool filled = true)
    {
        const string d = "M12,21 C12,21 4.5,16.3 2.5,12 A5.3,5.3 0 0 1 12,6.5 A5.3,5.3 0 0 1 21.5,12 C19.5,16.3 12,21 12,21 Z";
        return filled ? Box(size, Fill(d, b)) : Box(size, Stroke(d, b));
    }

    public static Viewbox Check(double size, Brush b) => Box(size, Stroke("M5,12.5 L10,17.5 L19,6.5", b, 2.2));

    public static Viewbox Chevron(bool right, double size, Brush b) =>
        Box(size, Stroke(right ? "M9,5 L16,12 L9,19" : "M15,5 L8,12 L15,19", b, 2.2));

    public static Viewbox Close(double size, Brush b) => Box(size, Stroke("M6,6 L18,18 M18,6 L6,18", b));

    public static Viewbox Folder(double size, Brush b) =>
        Box(size, Stroke("M3,7 L9,7 L11,9 L21,9 L21,18 A2,2 0 0 1 19,20 L3,20 Z", b));

    public static Viewbox Up(double size, Brush b) => Box(size, Stroke("M12,19 L12,5 M5,12 L12,5 L19,12", b));

    public static Viewbox Delete(double size, Brush b) =>
        Box(size, Stroke("M9,5 L20,5 L20,19 L9,19 L3,12 Z M13,10 L17,14 M17,10 L13,14", b));
}
