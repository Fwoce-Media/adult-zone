using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using AdultZone.Core;
using AdultZone.Core.Data;
using AdultZone.Core.Library;
using AdultZone.Core.Media;

namespace AdultZone.Desktop;

/// <summary>Every page: Home, Videos, a video, Pornstars, a performer, Studios, a studio.</summary>
public static class Pages
{
    public static Task<FrameworkElement> Build(MainWindow win, Location where) => where.Kind switch
    {
        "home" => Home(win),
        "videos" => Videos(win, where),
        "movies" => Videos(win, where),
        "video" => Video(win, where),
        "stars" => Stars(win, where),
        "actor" => Actor(win, where),
        "studios" => Studios(win),
        "studio" => Studio(win, where),
        "settings" => SettingsPage.Build(win),
        _ => Home(win),
    };

    static ScrollViewer Scroller(UIElement content) => new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Focusable = false,
        Content = content,
    };

    static LinearGradientBrush Fade(Point start, Point end, params (byte Alpha, double At)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var (a, at) in stops) brush.GradientStops.Add(new GradientStop(Color.FromArgb(a, 0x08, 0x08, 0x0B), at));
        return brush;
    }

    /// <summary>A backdrop: the poster frame, then the looping preview fading in over it.</summary>
    /// <summary>Fills whatever space it is given without asking for any, so a video never sizes the page.</summary>
    sealed class Backing : Grid
    {
        protected override Size MeasureOverride(Size available)
        {
            var fit = new Size(double.IsInfinity(available.Width) ? 0 : available.Width, double.IsInfinity(available.Height) ? 0 : available.Height);
            base.MeasureOverride(fit);
            return new Size(0, 0);
        }
    }

    static Grid Backdrop(Row v, int delay)
    {
        var grid = new Backing { ClipToBounds = true };
        var still = new Border();
        var thumb = Catalog.ThumbPath(v);
        if (thumb.Length > 0 && File.Exists(thumb)) Ui.Cover(still, thumb, 0.5, 0.35, 1920, fade: true);
        grid.Children.Add(still);
        var preview = Catalog.PreviewPath(v);
        if (preview.Length > 0 && File.Exists(preview))
        {
            var host = new Border();
            grid.Children.Add(host);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(delay) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                var loop = HoverPreview.Loop(preview);
                host.Child = loop;
                HoverPreview.Register(loop, true);
                if (Lock.Locked || Window.GetWindow(grid) is MainWindow { Player.Active: true }) return;
                loop.Play();
            };
            grid.Loaded += (_, _) => timer.Start();
            grid.Unloaded += (_, _) =>
            {
                timer.Stop();
                if (host.Child is MediaElement m) { HoverPreview.Register(m, false); m.Stop(); m.Close(); }
                host.Child = null;
            };
        }
        return grid;
    }

    // --------------------------------------------------------------------- home
    static async Task<FrameworkElement> Home(MainWindow win)
    {
        var d = await Task.Run(Catalog.Home);
        if (d.Stats.Videos == 0)
            return Ui.Page(Ui.Empty("Your library is empty",
                Ui.Button("Add a storage folder", () => win.Navigate(new Location("settings")), Ui.Look.Ember)));

        var stack = new StackPanel();
        var page = Scroller(stack);
        var pool = d.Latest.Concat(d.Movies).Concat(d.Random).ToList();
        var featured = pool.FirstOrDefault(v => v.Str("preview").Length > 0) ?? pool.FirstOrDefault();
        if (featured == null) return Ui.Page(Ui.Empty("Your library is empty"));
        stack.Children.Add(Hero(win, featured, page));

        var rows = new StackPanel { Margin = new Thickness(0, -40, 0, 70) };
        Panel.SetZIndex(rows, 3);
        // Mixed rows keep one shape: a movie shows as a frame from the film there, its cover on the Movies row.
        List<UIElement> Videos(IEnumerable<Row> items) => items.Select(v => (UIElement)Cards.Video(win, v)).ToList();
        List<UIElement> Covers(IEnumerable<Row> items) => items.Select(v => (UIElement)Cards.Movie(win, v)).ToList();
        // The rows chosen in Settings, in the order chosen.
        foreach (var key in Catalog.HomeRowKeys())
        {
            switch (key)
            {
                case "latest" when d.Latest.Count > 0:
                    rows.Children.Add(Ui.Shelf("Recently added scenes", Videos(d.Latest), 10, () => win.Navigate(new Location("videos", Sort: "added"))));
                    break;
                case "movies" when d.Movies.Count > 0:
                    rows.Children.Add(Ui.Shelf("Movies", Covers(d.Movies), 16, () => win.Navigate(new Location("movies", Sort: "added"))));
                    break;
                case "popular" when d.Popular.Count > 0:
                    rows.Children.Add(Ui.Shelf("Most viewed", Videos(d.Popular), 10, () => win.Navigate(new Location("videos", Sort: "views"))));
                    break;
                case "favorites" when d.Favorites.Count > 0:
                    rows.Children.Add(Ui.Shelf("Favourites", Videos(d.Favorites), 10));
                    break;
                case "stars" when d.Stars.Count > 0:
                    rows.Children.Add(Ui.Shelf("Top stars", d.Stars.Select(a => (UIElement)Cards.Star(win, a)).ToList(), 12,
                        () => win.Navigate(new Location("stars"))));
                    break;
                case "studios" when d.Studios.Count > 0:
                    rows.Children.Add(Ui.Shelf("Studios", d.Studios.Select(s => (UIElement)Cards.Studio(win, s)).ToList(), 12,
                        () => win.Navigate(new Location("studios"))));
                    break;
                case "random" when d.Random.Count > 0:
                    rows.Children.Add(Ui.Shelf("Pick something at random", Videos(d.Random), 10));
                    break;
                default:
                    if (key.StartsWith("tag:", StringComparison.Ordinal) && d.Tagged.FirstOrDefault(t => t.Tag == key[4..]) is { Items: not null } tagged)
                    {
                        var tag = tagged.Tag;
                        rows.Children.Add(Ui.Shelf(tag, Videos(tagged.Items), 10, () => win.Navigate(new Location("videos", Tag: tag))));
                    }
                    break;
            }
        }
        stack.Children.Add(rows);
        return page;
    }

    /// <summary>.hero — the newest video, its preview looping behind the title.</summary>
    static FrameworkElement Hero(MainWindow win, Row v, ScrollViewer page)
    {
        var id = v.Long("id") ?? 0;
        var hero = new Grid { ClipToBounds = true };
        void Size()
        {
            var h = page.ActualHeight > 0 ? page.ActualHeight : win.ActualHeight;
            hero.Height = Math.Clamp(Math.Min(h * 0.78, 660), 420, 660);
        }
        page.SizeChanged += (_, _) => Size();
        Size();
        hero.Children.Add(Backdrop(v, 900));
        hero.Children.Add(new Border { Background = Fade(new Point(0, 0), new Point(1, 0), (0xF0, 0), (0x99, 0.42), (0x26, 0.72)) });
        hero.Children.Add(new Border { Background = Fade(new Point(0, 1), new Point(0, 0), (0xFF, 0.02), (0x40, 0.42), (0x8C, 1)) });

        var body = new StackPanel
        {
            MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Ui.Gutter, 0, Ui.Gutter, 74),
        };
        body.Children.Add(Ui.Eyebrow("Latest"));
        var title = Ui.Title(v.Str("title"), 56);
        title.Margin = new Thickness(0, 10, 0, 14);
        title.MaxHeight = title.LineHeight * 2;
        title.TextTrimming = TextTrimming.WordEllipsis;
        void Fit()
        {
            var size = Math.Clamp(win.ActualWidth * 0.05, 30, 62);
            title.FontSize = size;
            title.LineHeight = Math.Round(size * 1.02);
            title.MaxHeight = title.LineHeight * 2;
        }
        hero.SizeChanged += (_, _) => Fit();
        Fit();
        body.Children.Add(title);
        body.Children.Add(Ui.Facts(new UIElement?[]
        {
            v.Str("studio_name") is { Length: > 0 } s ? Ui.Text(s) : null,
            v.Str("release_date") is { Length: > 0 } rd ? Ui.Text(Ui.Date(rd)) : null,
            Catalog.Quality(v) is { Length: > 0 } q ? Ui.Quality(q) : null,
            Ui.Duration(v.Double("duration")) is { Length: > 0 } dur ? Ui.Text(dur) : null,
        }));
        if (v.Str("description") is { Length: > 0 } desc)
        {
            var text = Ui.Clamp(desc, 14.5, new SolidColorBrush(Color.FromRgb(0xC9, 0xC7, 0xD2)), 3, 22);
            text.Margin = new Thickness(0, 12, 0, 0);
            text.MaxWidth = 560;
            text.HorizontalAlignment = HorizontalAlignment.Left;
            body.Children.Add(text);
        }
        var actions = Ui.Actions(
            Ui.Button("Play", () => win.Play(id), Ui.Look.Play, Icons.Play(16, new SolidColorBrush(Color.FromRgb(0x0A, 0x0A, 0x0E)))),
            Ui.Button("More info", () => win.Navigate(new Location("video", id))));
        actions.Margin = new Thickness(0, 22, 0, 0);
        body.Children.Add(actions);
        hero.Children.Add(body);
        return hero;
    }

    // ------------------------------------------------------------------- videos
    static async Task<FrameworkElement> Videos(MainWindow win, Location where)
    {
        var sort = where.Sort.Length > 0 ? where.Sort : "added";
        var movies = where.Kind == "movies";
        // Scenes and Movies each list their own; a search or a tag reaches both.
        var kind = movies ? "movie" : where.Query.Length > 0 || where.Tag.Length > 0 ? "" : "scene";
        var (data, tags) = await Task.Run(() => (
            Catalog.Videos(new VideoQuery(Search: where.Query, Tag: where.Tag, Quality: where.Quality, Sort: sort, Kind: kind)),
            Catalog.Tags()));
        var heading = where.Query.Length > 0 ? $"Results for “{where.Query}”"
                    : where.Tag.Length > 0 ? $"Tagged “{where.Tag}”" : movies ? "Movies" : "Scenes";

        void Go(Location next)
        {
            win.Replace(next);
            win.Refresh();
        }
        var filters = Ui.Row(
            Ui.Select(Catalog.VideoSorts, sort, s => Go(where with { Sort = s }), 220),
            Ui.Select(new[] { ("", "Any quality"), ("4K", "4K only"), ("2K", "2K only"), ("HD", "HD only"), ("SD", "SD only") },
                      where.Quality, q => Go(where with { Quality = q }), 150).Margin(10, 0, 0, 0),
            Ui.Select(new[] { ("", "Any tag") }
                          .Concat(where.Tag.Length > 0 && !tags.Any(t => string.Equals(t.Str("name"), where.Tag, StringComparison.OrdinalIgnoreCase))
                              ? new[] { (where.Tag, where.Tag) } : Array.Empty<(string, string)>())
                          .Concat(tags.Where(t => (t.Long("video_count") ?? 0) > 0)
                          .Select(t => (t.Str("name"), $"{t.Str("name")} ({t.Long("video_count")})"))),
                      where.Tag, t => Go(where with { Tag = t }), 220).Margin(10, 0, 0, 0));
        filters.Margin = new Thickness(0, 0, 0, 22);

        var stack = new StackPanel();
        stack.Children.Add(Ui.PageHead("Library", heading,
            movies ? Ui.Plural(data.Total, "movie", "movies") : kind == "scene" ? Ui.Plural(data.Total, "scene", "scenes") : Ui.Plural(data.Total, "video", "videos")));
        stack.Children.Add(filters);
        var grid = movies ? Cards.MovieGrid(win, data.Items)
                 : kind == "" && data.Items.Any(Catalog.IsMovie) && data.Items.All(Catalog.IsMovie) ? Cards.MovieGrid(win, data.Items)
                 : Cards.VideoGrid(win, data.Items);
        stack.Children.Add(data.Items.Count > 0 ? grid : Ui.Empty(movies ? "No movies yet" : "Nothing matched"));
        return Ui.Page(stack);
    }

    // -------------------------------------------------------------------- video
    static async Task<FrameworkElement> Video(MainWindow win, Location where)
    {
        var id = where.Id;
        var loaded = await Task.Run(() =>
        {
            var v = Catalog.Video(id);
            if (v == null) return null;
            return new
            {
                Video = v,
                ByCast = Catalog.Related(id, "cast"),
                ByStudio = Catalog.Related(id, "studio"),
                Exists = File.Exists(v.Str("path")),
            };
        });
        if (loaded == null) return Ui.Page(Ui.Empty("That video is no longer in the library"));
        var v = loaded.Video;
        if (where.Play)
        {
            win.Replace(where with { Play = false });
            win.Play(id);
        }

        var stack = new StackPanel();
        var page = Scroller(stack);

        // .detail-hero
        var hero = new Grid { ClipToBounds = true };
        void Size()
        {
            var h = page.ActualHeight > 0 ? page.ActualHeight : win.ActualHeight;
            hero.MinHeight = Math.Min(h * 0.72, 620);
        }
        page.SizeChanged += (_, _) => Size();
        Size();
        hero.Children.Add(Backdrop(v, 700));
        hero.Children.Add(new Border { Background = Fade(new Point(0, 0), new Point(1, 0), (0xF2, 0), (0x9E, 0.46), (0x33, 0.78)) });
        hero.Children.Add(new Border { Background = Fade(new Point(0, 1), new Point(0, 0), (0xFF, 0.01), (0x4D, 0.46), (0x99, 1)) });

        var play = new Border
        {
            Width = 86, Height = 86, CornerRadius = new CornerRadius(43), Background = Theme.Alpha(Color.FromRgb(10, 10, 14), 0x6B),
            BorderBrush = Theme.Alpha(Colors.White, 0xD1), BorderThickness = new Thickness(2),
            Child = Icons.Play(36, Brushes.White).Margin(7, 0, 0, 0),
        };
        var playScale = new ScaleTransform(1, 1);
        play.RenderTransformOrigin = new Point(0.5, 0.5);
        play.RenderTransform = playScale;
        var playButton = Ui.Bare(play, () => win.Play(id), "Play");
        playButton.Cursor = System.Windows.Input.Cursors.Hand;
        playButton.HorizontalAlignment = HorizontalAlignment.Center;
        playButton.VerticalAlignment = VerticalAlignment.Top;
        void PlaceButton() => playButton.Margin = new Thickness(0, Math.Max(MainWindow.BarHeight + 20, hero.ActualHeight * 0.44 - 43), 0, 0);
        hero.SizeChanged += (_, _) => PlaceButton();
        playButton.MouseEnter += (_, _) =>
        {
            play.Background = Theme.Ember;
            play.BorderBrush = Theme.Ember;
            play.Child = Icons.Play(36, Theme.OnEmber).Margin(7, 0, 0, 0);
            playScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(180)));
            playScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.08, TimeSpan.FromMilliseconds(180)));
        };
        playButton.MouseLeave += (_, _) =>
        {
            play.Background = Theme.Alpha(Color.FromRgb(10, 10, 14), 0x6B);
            play.BorderBrush = Theme.Alpha(Colors.White, 0xD1);
            play.Child = Icons.Play(36, Brushes.White).Margin(7, 0, 0, 0);
            playScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            playScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
        };
        hero.Children.Add(playButton);

        var body = new StackPanel
        {
            MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Ui.Gutter, MainWindow.BarHeight + 150, Ui.Gutter, 34),
        };
        var studioLine = new WrapPanel();
        if (v.Str("studio_name") is { Length: > 0 } studio)
        {
            var sid = v.Long("studio_id") ?? 0;
            var eyebrow = Ui.Bare(Ui.Eyebrow(studio), () => win.Navigate(new Location("studio", sid)));
            eyebrow.Cursor = System.Windows.Input.Cursors.Hand;
            eyebrow.VerticalAlignment = VerticalAlignment.Center;
            eyebrow.Margin = new Thickness(0, 0, 10, 0);
            studioLine.Children.Add(eyebrow);
        }
        if (v.Str("subsite") is { Length: > 0 } site) studioLine.Children.Add(Cards.SubsitePill(win, site));
        if (studioLine.Children.Count > 0) body.Children.Add(studioLine);
        var title = Ui.Title(v.Str("title"), v.Str("title").Length > 34 ? 38 : 46);
        title.Margin = new Thickness(0, 8, 0, 12);
        // The text column stops short of the play button in the middle.
        hero.SizeChanged += (_, _) =>
            body.MaxWidth = Math.Clamp(hero.ActualWidth / 2 - 43 - 32 - Ui.Gutter, 300, 720);
        body.Children.Add(title);
        body.Children.Add(Ui.Facts(new UIElement?[]
        {
            v.Str("release_date") is { Length: > 0 } rd ? Ui.Text(Ui.Date(rd)) : null,
            Catalog.Quality(v) is { Length: > 0 } q ? Ui.Quality(q) : null,
            Ui.Duration(v.Double("duration")) is { Length: > 0 } dur ? Ui.Text(dur) : null,
            Ui.Text(Ui.Views(v.Long("views"))),
            (v.Long("width") ?? 0) > 0 ? Ui.Text($"{v.Long("width")}x{v.Long("height")}") : null,
        }));
        if (v.Str("description") is { Length: > 0 } desc)
        {
            var text = Ui.Text(desc, 14.5, Theme.Body, wrap: true);
            text.LineHeight = 22;
            var scroll = new ScrollViewer
            {
                Content = text, MaxHeight = Math.Clamp(win.ActualHeight * 0.19, 96, 210), MaxWidth = 600,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Margin = new Thickness(0, 14, 0, 0),
                Padding = new Thickness(0, 0, 12, 0),
            };
            Ui.ChainWheel(scroll);
            body.Children.Add(scroll);
        }
        var cast = Catalog.Cast(v);
        if (cast.Count > 0) body.Children.Add(ChipStrip(cast.Select(a => (UIElement)Cards.PersonChip(win, a))).Margin(0, 14, 0, 0));
        var tags = Catalog.TagsOf(v).Where(t => !string.Equals(t, v.Str("subsite"), StringComparison.OrdinalIgnoreCase)).ToList();
        if (tags.Count > 0) body.Children.Add(ChipStrip(tags.Select(t => (UIElement)Cards.TagChip(win, t))).Margin(0, cast.Count > 0 ? 4 : 14, 0, 0));
        var favorite = v.Truthy("favorite");
        var actions = Ui.Actions(
            Ui.Button("Edit details", () => Dialogs.EditVideo(win, v)),
            Ui.Button("Find info", () => ImportDialog.Show(win, "video", id, v.Str("title"),
                new ImportDialog.Hints(v.Str("studio_name"), v.Str("subsite"), cast.FirstOrDefault()?.Str("name") ?? ""))),
            Ui.Button(favorite ? "Remove from favourites" : "Add to favourites", () =>
            {
                Catalog.SetFavorite(id, !favorite);
                win.Refresh(keepScroll: true);
            }, icon: Icons.Heart(15, favorite ? Theme.Ember : Theme.Text, favorite)));
        actions.Margin = new Thickness(0, 20, 0, 0);
        body.Children.Add(actions);
        if (!loaded.Exists) body.Children.Add(Ui.Text("The file is no longer at its recorded location.", 13, Theme.Warn, margin: new Thickness(0, 6, 0, 0)));
        if (Catalog.IsMovie(v) && Catalog.CoverPath(v) is { Length: > 0 } coverFile && File.Exists(coverFile))
        {
            // A movie's box cover stands beside its details.
            var poster = new Border
            {
                Width = 230, Height = 345, CornerRadius = new CornerRadius(10), Background = Theme.Panel,
                BorderBrush = Theme.Alpha(Colors.White, 0x1F), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 32, 0),
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 8, Opacity = 0.55, Color = Colors.Black },
            };
            var art = new Border { CornerRadius = new CornerRadius(9) };
            Ui.Cover(art, coverFile, decode: 700, fade: true);
            art.SizeChanged += (_, e) => art.Clip = new RectangleGeometry(new Rect(e.NewSize), 9, 9);
            poster.Child = art;
            var side = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Bottom, HorizontalAlignment = HorizontalAlignment.Left };
            var margin = body.Margin;
            body.Margin = new Thickness(0);
            side.Margin = margin;
            side.Children.Add(poster);
            side.Children.Add(body);
            hero.SizeChanged += (_, _) =>
                body.MaxWidth = Math.Clamp(hero.ActualWidth / 2 - 43 - 32 - Ui.Gutter - 262, 280, 720);
            hero.Children.Add(side);
        }
        else hero.Children.Add(body);
        stack.Children.Add(hero);

        // tabs: more with this cast, from the same studio, file details
        var panes = new Dictionary<string, FrameworkElement>
        {
            ["cast"] = loaded.ByCast.Count > 0 ? Cards.VideoGrid(win, loaded.ByCast) : Ui.Empty("Nothing else with this cast"),
            ["details"] = FileDetails(win, v),
        };
        var tabs = new List<(string Key, string Label, int Count)> { ("cast", "More with this cast", loaded.ByCast.Count) };
        if (v.Long("studio_id") != null)
        {
            panes["studio"] = loaded.ByStudio.Count > 0 ? Cards.VideoGrid(win, loaded.ByStudio) : Ui.Empty("Nothing else from this studio");
            tabs.Add(("studio", "From same studio", loaded.ByStudio.Count));
        }
        tabs.Add(("details", "File details", 0));
        var section = new StackPanel { Margin = new Thickness(Ui.Gutter, 36, Ui.Gutter, 90) };
        var paneHost = new ContentControl { Focusable = false };
        section.Children.Add(TabBar(tabs, key => paneHost.Content = panes[key]));
        section.Children.Add(paneHost);
        paneHost.Content = panes["cast"];
        stack.Children.Add(section);
        return page;
    }

    /// <summary>A single line of chips that scrolls sideways.</summary>
    static FrameworkElement ChipStrip(IEnumerable<UIElement> chips)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in chips) row.Children.Add(c);
        var scroll = new ScrollViewer
        {
            Content = row, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false, MaxWidth = 600, HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 2, 0, 6),
            Tag = Ui.SideScrollTag,
        };
        Ui.PassWheelToParent(scroll);
        Ui.DragToScroll(scroll);
        // Chips that run past the edge fade out there, so a cut chip reads as "more this way".
        void Fade()
        {
            var more = scroll.ScrollableWidth > 0.5 && scroll.HorizontalOffset < scroll.ScrollableWidth - 0.5;
            var earlier = scroll.HorizontalOffset > 0.5;
            if (!more && !earlier) { scroll.OpacityMask = null; return; }
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(earlier ? Colors.Transparent : Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, earlier ? 0.1 : 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, more ? 0.86 : 1));
            mask.GradientStops.Add(new GradientStop(more ? Colors.Transparent : Colors.Black, 1));
            scroll.OpacityMask = mask;
        }
        scroll.ScrollChanged += (_, _) => Fade();
        scroll.SizeChanged += (_, _) => Fade();
        return scroll;
    }

    /// <summary>.tabbar — labels with a count, an ember line under the chosen one.</summary>
    public static FrameworkElement TabBar(IList<(string Key, string Label, int Count)> tabs, Action<string> pick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var entries = new List<(string Key, TextBlock Text, Border Line, Border? Badge, TextBlock? Count)>();
        void Select(string key)
        {
            foreach (var (k, text, line, badge, count) in entries)
            {
                var on = k == key;
                text.Foreground = on ? Theme.Text : Theme.Muted;
                line.Background = on ? Theme.Ember : Theme.Clear;
                if (badge != null && count != null)
                {
                    badge.Background = on ? Theme.EmberWash : Theme.Alpha(Colors.White, 0x12);
                    count.Foreground = on ? Theme.Ember : Theme.Faint;
                }
            }
            pick(key);
        }
        foreach (var (key, label, n) in tabs)
        {
            var text = Ui.Text(label, 13.5, Theme.Muted, FontWeights.SemiBold);
            var head = Ui.Row(text);
            Border? badge = null;
            TextBlock? count = null;
            if (n > 0)
            {
                count = Ui.Text(n.ToString(System.Globalization.CultureInfo.InvariantCulture), 10.5, Theme.Faint, FontWeights.SemiBold);
                count.FontFamily = Theme.Mono;
                badge = new Pill { CornerRadius = new CornerRadius(12), Padding = new Thickness(6, 0, 6, 1), Margin = new Thickness(6, 0, 0, 0), Child = count, VerticalAlignment = VerticalAlignment.Center };
                head.Children.Add(badge);
            }
            var line = new Border { Height = 2, Background = Theme.Clear, Margin = new Thickness(0, 10, 0, 0) };
            var b = Ui.Bare(Ui.Column(head, line), () => Select(key));
            b.Focusable = false;
            b.Cursor = System.Windows.Input.Cursors.Hand;
            b.Margin = new Thickness(0, 0, 22, 0);
            entries.Add((key, text, line, badge, count));
            row.Children.Add(b);
        }
        var bar = new Border { BorderBrush = Theme.Line, BorderThickness = new Thickness(0, 0, 0, 1), Margin = new Thickness(0, 0, 0, 20), Child = row };
        bar.Loaded += (_, _) => { if (entries.Count > 0 && entries.All(e => e.Line.Background == Theme.Clear)) Select(entries[0].Key); };
        return bar;
    }

    static FrameworkElement Fact(string label, UIElement value)
    {
        var s = new StackPanel { Margin = new Thickness(0, 0, 26, 12) };
        s.Children.Add(Ui.Caps(label, 11, Theme.Faint, 0.14));
        if (value is TextBlock t)
        {
            t.FontSize = 15;
            t.FontWeight = FontWeights.SemiBold;
            t.Foreground = Theme.Text;
        }
        if (value is FrameworkElement fe) fe.Margin = new Thickness(0, 3, 0, 0);
        s.Children.Add(value);
        return s;
    }

    static FrameworkElement FileDetails(MainWindow win, Row v)
    {
        var id = v.Long("id") ?? 0;
        var quality = Catalog.Quality(v);
        var facts = new WrapPanel();
        facts.Children.Add(Fact("Resolution", Ui.Text($"{v.Long("width") ?? 0}x{v.Long("height") ?? 0}" + (quality.Length > 0 ? $" ({quality})" : ""))));
        facts.Children.Add(Fact("Duration", Ui.Text(Ui.Duration(v.Double("duration")) is { Length: > 0 } d ? d : "—")));
        facts.Children.Add(Fact("File size", Ui.Text(Ui.Size(v.Long("filesize")) is { Length: > 0 } s ? s : "—")));
        facts.Children.Add(Fact("Added", Ui.Text(Ui.Date(v.Str("added_at")))));
        facts.Children.Add(Fact("Preview loop", Ui.Text((v.Long("preview_width") ?? 0) > 0 ? $"{v.Long("preview_width")}px wide" : v.Str("preview").Length > 0 ? "Yes" : "None")));
        var path = Ui.Text(v.Str("path"), 12.5, Theme.Text, wrap: true);
        path.FontFamily = Theme.Mono;
        var buttons = Ui.Actions(
            Ui.Button("Open containing folder", () => Reveal(win, v.Str("path")), small: true),
            Ui.Button("Rebuild thumbnail & preview", () =>
            {
                Scanner.Rebuild(id);
                win.WatchScan();
            }, small: true),
            Ui.Button("Remove from library", () =>
            {
                if (!Dialogs.Confirm(win, "Remove this video from the library?", "Remove")) return;
                Catalog.RemoveVideo(id);
                win.GoBack();
            }, Ui.Look.Warn, small: true));
        buttons.Margin = new Thickness(0, 16, 0, 0);
        var panel = new Border
        {
            Background = Theme.Panel, BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(20),
            Child = Ui.Column(facts, Ui.Caps("Path", 11, Theme.Faint, 0.14).Margin(0, 2, 0, 4), path, buttons),
        };
        return panel;
    }

    public static void Reveal(MainWindow win, string path)
    {
        try
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else if (Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
            else win.Toast("That folder is not there", true);
        }
        catch (Exception ex) { win.Toast("Could not open the folder: " + ex.Message, true); }
    }

    // -------------------------------------------------------------------- stars
    static async Task<FrameworkElement> Stars(MainWindow win, Location where)
    {
        var sort = where.Sort.Length > 0 ? where.Sort : "count";
        var (items, hidden) = await Task.Run(() => Catalog.Actors("", sort, where.Hidden));
        var gender = where.Tag;
        if (gender.Length > 0) items = items.Where(a => a.Str("gender") == gender).ToList();
        var empty = items.Count(a => (a.Long("video_count") ?? 0) == 0);
        var sub = $"{items.Count} shown";
        if (hidden > 0) sub += $" · {hidden} hidden";
        if (empty > 0) sub += $" · {empty} with no videos";

        void Go(Location next)
        {
            win.Replace(next);
            win.Refresh();
        }
        var controls = new WrapPanel { VerticalAlignment = VerticalAlignment.Bottom };
        if (hidden > 0 || where.Hidden)
            controls.Children.Add(Ui.Button(where.Hidden ? "Hide them again" : $"Show {hidden} hidden", () => Go(where with { Hidden = !where.Hidden }), small: true).Margin(0, 0, 10, 0));
        if (empty > 0)
            controls.Children.Add(Ui.Button($"Remove {empty} with no videos", () =>
            {
                if (!Dialogs.Confirm(win, $"Remove {Ui.Plural(empty, "profile", "profiles")} with no videos?", "Remove")) return;
                win.Toast($"Removed {Catalog.PruneActors()}");
                win.Refresh();
            }, small: true).Margin(0, 0, 10, 0));
        var genders = new ComboBox { Width = 160, Margin = new Thickness(0, 0, 10, 0) };
        genders.Items.Add(new ComboBoxItem { Content = "All genders", Tag = "" });
        foreach (var (key, label) in Catalog.Genders) genders.Items.Add(new ComboBoxItem { Content = Gender.Labelled(key, label), Tag = key });
        genders.SelectedIndex = Math.Max(0, Array.FindIndex(Catalog.Genders, g => g.Key == gender) + 1);
        genders.SelectionChanged += (_, _) => { if (genders.SelectedItem is ComboBoxItem { Tag: string k } && k != gender) Go(where with { Tag = k }); };
        controls.Children.Add(genders);
        controls.Children.Add(Ui.Select(new[] { ("count", "Most videos"), ("name", "Name A–Z") }, sort, s => Go(where with { Sort = s }), 170));

        var stack = new StackPanel();
        stack.Children.Add(Ui.PageHead("Cast", "Pornstars", sub, controls));
        stack.Children.Add(items.Count > 0 ? Cards.StarGrid(win, items) : Ui.Empty("No stars yet"));
        return Ui.Page(stack);
    }

    // -------------------------------------------------------------------- actor
    static async Task<FrameworkElement> Actor(MainWindow win, Location where)
    {
        var sort = where.Sort.Length > 0 ? where.Sort : "added";
        var a = await Task.Run(() => Catalog.Actor(where.Id, sort));
        if (a == null) return Ui.Page(Ui.Empty("That profile is gone"));
        var id = a.Long("id") ?? 0;
        var videos = Catalog.VideosOf(a);
        var count = a.Long("video_count") ?? 0;

        var head = new Grid();
        var banner = Catalog.ActorBanner(a);
        if (banner.Length > 0 && File.Exists(banner))
        {
            // Behind the right-hand side, fading out before it reaches the text.
            var art = new Border { HorizontalAlignment = HorizontalAlignment.Right, Opacity = 0.78 };
            Ui.Cover(art, banner, 0.5, 0.3, 1600, fade: true);
            art.OpacityMask = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0), EndPoint = new Point(1, 0),
                GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Color.FromArgb(0xA6, 0, 0, 0), 0.42), new GradientStop(Colors.Black, 1) },
            };
            head.SizeChanged += (_, _) => art.Width = Math.Min(head.ActualWidth * 0.46, 760);
            var wrap = new Grid();
            wrap.Children.Add(art);
            wrap.Children.Add(new Border
            {
                HorizontalAlignment = HorizontalAlignment.Right,
                Background = Fade(new Point(0, 1), new Point(0, 0), (0xFF, 0), (0x00, 0.38)),
            });
            head.SizeChanged += (_, _) => ((Border)wrap.Children[1]).Width = art.Width;
            head.Children.Add(wrap);
        }

        var portrait = new Border
        {
            CornerRadius = new CornerRadius(14), BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
            ClipToBounds = true, Child = Cards.Portrait(a, 48), VerticalAlignment = VerticalAlignment.Top,
        };
        void SizePortrait()
        {
            var w = Math.Clamp(win.ActualWidth * 0.22, 200, 290);
            portrait.Width = w;
            portrait.Height = Math.Round(w * 600 / 435);
        }
        head.SizeChanged += (_, _) => SizePortrait();
        SizePortrait();

        var info = new StackPanel { Margin = new Thickness(36, 0, 0, 0), MinWidth = 260 };
        info.Children.Add(Ui.Eyebrow("Pornstar"));
        var nameRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        var name = Ui.Title(a.Str("name"), 44, false);
        name.VerticalAlignment = VerticalAlignment.Center;
        name.Margin = new Thickness(0, 0, 12, 0);
        nameRow.Children.Add(name);
        var badgeText = Ui.Text(Ui.Plural(count, "video", "videos"), 12, Theme.Ember, FontWeights.SemiBold);
        badgeText.FontFamily = Theme.Mono;
        nameRow.Children.Add(new Pill
        {
            BorderBrush = Theme.EmberDim, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
            Padding = new Thickness(10, 3, 10, 4), VerticalAlignment = VerticalAlignment.Center, Child = badgeText,
        });
        if (Gender.Tag(a.Str("gender")) is { } genderTag)
        {
            genderTag.Margin = new Thickness(8, 0, 0, 0);
            nameRow.Children.Add(genderTag);
        }
        info.Children.Add(nameRow);

        var facts = new WrapPanel { Margin = new Thickness(0, 16, 0, 4) };
        var age = a.Long("age") is long stored and > 0 ? (int)stored : Ui.AgeFrom(a.Str("birthdate"));
        if (age != null) facts.Children.Add(Fact("Age", Ui.Text(age.Value.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (a.Str("birthdate") is { Length: > 0 } born) facts.Children.Add(Fact("Born", Ui.Text(Ui.Date(born))));
        facts.Children.Add(Fact("Videos", Ui.Text(count.ToString(System.Globalization.CultureInfo.InvariantCulture))));
        if (a.Str("country") is { Length: > 0 } country)
        {
            var from = new StackPanel { Orientation = Orientation.Horizontal };
            if (Countries.Flag(country) is { } flag)
            {
                flag.Margin = new Thickness(0, 0, 8, 0);
                from.Children.Add(new Border { BorderBrush = Theme.Alpha(Colors.White, 0x29), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(2), Child = flag, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center });
                flag.Margin = new Thickness(0);
            }
            from.Children.Add(Ui.Text(Countries.Name(country), 15, Theme.Text, FontWeights.SemiBold));
            facts.Children.Add(Fact("From", from));
        }
        if (Dialogs.StatusLabel(a.Str("status")) is { Length: > 0 } status)
        {
            var colour = a.Str("status") switch
            {
                "active" => Theme.Hd,
                "inactive" => new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27)),
                "died" => Theme.Warn,
                _ => new SolidColorBrush(Color.FromRgb(0x8E, 0x8C, 0x99)),
            };
            var dot = new System.Windows.Shapes.Ellipse { Width = 9, Height = 9, Fill = colour, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center };
            if (a.Str("status") == "active") dot.Effect = new System.Windows.Media.Effects.DropShadowEffect { Color = Theme.HdC, BlurRadius = 8, ShadowDepth = 0, Opacity = 0.6 };
            facts.Children.Add(Fact("Status", Ui.Row(dot, Ui.Text(status, 15, Theme.Text, FontWeights.SemiBold))));
        }
        var details = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), MaxWidth = 700, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (column, label, _) in Catalog.PerformerFacts)
            if (a.Str(column) is { Length: > 0 } value)
            {
                var text = Ui.Text(value, 15, Theme.Text, FontWeights.SemiBold, wrap: true);
                text.TextTrimming = TextTrimming.None;
                text.MaxWidth = 660;
                details.Children.Add(Fact(label, text));
            }
        if (details.Children.Count > 0)
        {
            var open = Db.SettingOn("actor_details");
            details.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
            var caption = Ui.Text("Details", 13, Theme.Muted, FontWeights.SemiBold);
            var chevron = Ui.Text(open ? "\u25B4" : "\u25BE", 12, Theme.Muted, margin: new Thickness(6, 0, 0, 0));
            var toggle = new Pill
            {
                Padding = new Thickness(12, 5, 11, 6), BorderThickness = new Thickness(1), Cursor = System.Windows.Input.Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 0, 12),
                Child = Ui.Row(caption, chevron),
            };
            void Paint(bool hover)
            {
                var on = details.Visibility == Visibility.Visible;
                toggle.BorderBrush = on || hover ? Theme.Alpha(Theme.EmberC, 0x8C) : Theme.Alpha(Colors.White, 0x24);
                toggle.Background = on ? Theme.Alpha(Theme.EmberC, 0x1F) : hover ? Theme.Alpha(Colors.White, 0x0F) : Theme.Alpha(Colors.White, 0x08);
                caption.Foreground = chevron.Foreground = on ? Theme.Ember : hover ? Theme.Text : Theme.Muted;
                chevron.Text = on ? "\u25B4" : "\u25BE";
            }
            Paint(false);
            toggle.MouseEnter += (_, _) => Paint(true);
            toggle.MouseLeave += (_, _) => Paint(false);
            toggle.MouseLeftButtonUp += (_, _) =>
            {
                var show = details.Visibility != Visibility.Visible;
                details.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                Db.SetSetting("actor_details", show);
                Paint(toggle.IsMouseOver);
            };
            facts.Children.Add(toggle);
        }
        info.Children.Add(facts);
        if (a.Str("description") is { Length: > 0 } bio)
        {
            var text = Ui.Text(bio, 14.5, new SolidColorBrush(Color.FromRgb(0xC2, 0xC0, 0xCB)), wrap: true);
            text.LineHeight = 22;
            var scroll = new ScrollViewer
            {
                Content = text, MaxHeight = Math.Clamp(win.ActualHeight * 0.3, 150, 300), MaxWidth = 640,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 12, 0, 0),
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(0, 0, 12, 0),
            };
            Ui.ChainWheel(scroll);
            info.Children.Add(scroll);
        }
        if (details.Children.Count > 0) info.Children.Add(details);
        var hidden = a.Truthy("hidden");
        var actions = Ui.Actions(
            Ui.Button("Edit profile", () => Dialogs.EditActor(win, a), small: true),
            Ui.Button("Photo", () =>
            {
                if (Dialogs.PickImage(win) is not { } file) return;
                Catalog.SetActorPicture(id, "image", file);
                win.Toast("Photo updated");
                win.Refresh(keepScroll: true);
            }, small: true),
            Ui.Button("Wide photo", () =>
            {
                if (Dialogs.PickImage(win) is not { } file) return;
                Catalog.SetActorPicture(id, "banner", file);
                win.Toast("Wide photo updated");
                win.Refresh(keepScroll: true);
            }, small: true),
            Ui.Button("Find info", () => ImportDialog.Show(win, "actor", id, a.Str("name")), small: true),
            Ui.Button(hidden ? "Show on Pornstars" : "Hide from Pornstars", () =>
            {
                Catalog.HideActor(id, !hidden);
                win.Toast(hidden ? "Showing on Pornstars" : "Hidden from Pornstars");
                win.Refresh(keepScroll: true);
            }, small: true),
            Ui.Button("Delete profile", () =>
            {
                if (!Dialogs.Confirm(win, $"Delete “{a.Str("name")}”?", "Delete")) return;
                Catalog.DeleteActor(id);
                win.Toast("Profile deleted");
                win.GoBack();
            }, Ui.Look.Warn, small: true));
        actions.Margin = new Thickness(0, 20, 0, 0);
        info.Children.Add(actions);

        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(Ui.Gutter, MainWindow.BarHeight + 34, Ui.Gutter, 0) };
        DockPanel.SetDock(portrait, Dock.Left);
        top.Children.Add(portrait);
        top.Children.Add(info);
        head.Children.Add(top);

        var section = new StackPanel { Margin = new Thickness(Ui.Gutter, 40, Ui.Gutter, 90) };
        var sectionHead = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 14) };
        if (videos.Count > 1)
        {
            var pick = Ui.Select(Catalog.VideoSorts, sort, s => { win.Replace(where with { Sort = s }); win.Refresh(keepScroll: true); }, 220);
            DockPanel.SetDock(pick, Dock.Right);
            sectionHead.Children.Add(pick);
        }
        var h2 = Ui.Title("Appears in", 18, false);
        h2.VerticalAlignment = VerticalAlignment.Center;
        sectionHead.Children.Add(h2);
        section.Children.Add(sectionHead);
        section.Children.Add(videos.Count > 0 ? Cards.VideoGrid(win, videos) : Ui.Empty("No videos linked"));

        var stack = new StackPanel();
        stack.Children.Add(head);
        stack.Children.Add(section);
        return Scroller(stack);
    }

    // ------------------------------------------------------------------ studios
    static async Task<FrameworkElement> Studios(MainWindow win)
    {
        var items = await Task.Run(() => Catalog.Studios("count"));
        var empty = items.Count(s => (s.Long("video_count") ?? 0) == 0);
        var sub = $"{items.Count} in your library" + (empty > 0 ? $" · {empty} with no videos" : "");
        UIElement? controls = empty > 0
            ? Ui.Button($"Remove {empty} empty {(empty == 1 ? "studio" : "studios")}", () =>
            {
                if (!Dialogs.Confirm(win, $"Remove {Ui.Plural(empty, "studio", "studios")} with no videos?", "Remove")) return;
                win.Toast($"Removed {Catalog.PruneStudios()}");
                win.Refresh();
            }, small: true)
            : null;
        var stack = new StackPanel();
        stack.Children.Add(Ui.PageHead("Labels", "Studios", sub, controls));
        stack.Children.Add(items.Count > 0 ? Cards.StudioGrid(win, items) : Ui.Empty("No studios yet"));
        return Ui.Page(stack);
    }

    static async Task<FrameworkElement> Studio(MainWindow win, Location where)
    {
        var sort = where.Sort.Length > 0 ? where.Sort : "added";
        var s = await Task.Run(() => Catalog.Studio(where.Id, sort));
        if (s == null) return Ui.Page(Ui.Empty("That studio is gone"));
        var id = s.Long("id") ?? 0;
        var videos = Catalog.VideosOf(s);
        var hasLogo = Catalog.StudioLogo(s) is { Length: > 0 } logoFile && File.Exists(logoFile);

        var titleBlock = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        titleBlock.Children.Add(Ui.Eyebrow("Studio"));
        titleBlock.Children.Add(Ui.Title(s.Str("name"), 36, false).Margin(0, 4, 0, 0));
        titleBlock.Children.Add(Ui.Text(Ui.Plural(videos.Count, "video", "videos"), 13.5, Theme.Muted, margin: new Thickness(0, 4, 0, 0)));
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        if (hasLogo)
        {
            var logo = Cards.Logo(s, Cards.LogoLayout.Of(s), 200);
            logo.Margin = new Thickness(0, 0, 20, 0);
            logo.VerticalAlignment = VerticalAlignment.Center;
            left.Children.Add(logo);
        }
        left.Children.Add(titleBlock);

        var actions = Ui.Actions(
            Ui.Button("Edit studio", () => Dialogs.EditStudio(win, s), small: true),
            Ui.Button("Logo", () =>
            {
                if (Dialogs.PickImage(win) is not { } file) return;
                Catalog.SetStudioLogo(id, file);
                win.Toast("Logo updated");
                win.Refresh(keepScroll: true);
            }, small: true));
        if (hasLogo) actions.Children.Add(Ui.Button("Adjust logo", () => Dialogs.AdjustLogo(win, s), small: true).Margin(0, 0, 10, 10));
        actions.Children.Add(Ui.Button("Find info", () => ImportDialog.Show(win, "studio", id, s.Str("name")), small: true).Margin(0, 0, 10, 10));
        actions.Children.Add(Ui.Button("Delete studio", () =>
        {
            if (!Dialogs.Confirm(win, $"Delete “{s.Str("name")}”?", "Delete")) return;
            Catalog.DeleteStudio(id);
            win.Toast("Studio deleted");
            win.GoBack();
        }, Ui.Look.Warn, small: true).Margin(0, 0, 10, 10));
        actions.VerticalAlignment = VerticalAlignment.Bottom;

        var head = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 0, 0, 22) };
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        head.Children.Add(left);

        var stack = new StackPanel();
        stack.Children.Add(head);
        if (s.Str("description") is { Length: > 0 } desc)
        {
            var text = Ui.Text(desc, 14.5, new SolidColorBrush(Color.FromRgb(0xC2, 0xC0, 0xCB)), wrap: true, margin: new Thickness(0, 0, 0, 22));
            text.LineHeight = 22;
            text.MaxWidth = 700;
            text.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(text);
        }
        if (videos.Count > 1)
            stack.Children.Add(Ui.Select(Catalog.VideoSorts, sort, k => { win.Replace(where with { Sort = k }); win.Refresh(keepScroll: true); }, 220)
                .Margin(0, 0, 0, 20));
        stack.Children.Add(videos.Count > 0 ? Cards.VideoGrid(win, videos) : Ui.Empty("No videos"));
        return Ui.Page(stack);
    }
}
