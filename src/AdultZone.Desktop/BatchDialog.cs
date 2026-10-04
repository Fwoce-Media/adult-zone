using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdultZone.Core.Data;
using AdultZone.Core.Library;
using AdultZone.Core.Providers;

namespace AdultZone.Desktop;

/// <summary>A batch scrape running, its counts, then the picks and merges it left for a person.</summary>
public static class BatchDialog
{
    static bool _busy;

    /// <summary>Runs a batch scrape, or with a saved review, opens that review where it was left.</summary>
    public static void Start(MainWindow win, BatchOptions o, BatchState? resumed = null)
    {
        if (_busy) { win.Toast("A batch scrape is already running"); return; }
        _busy = true;
        var w = Dialogs.Create(win, "Batch scrape", 780, 760);
        var state = resumed ?? new BatchState();
        var stop = new CancellationTokenSource();
        var finished = resumed != null;
        var index = 0;
        var typed = new Dictionary<BatchChoice, string[]>();
        ScrollViewer scroll = null!;
        void Save() => Batch.SaveSession(o, state);

        (Border Tile, TextBlock Number) Stat(string label, Brush colour)
        {
            var number = Ui.Text("0", 26, colour, FontWeights.Bold);
            var tile = new Border
            {
                Background = Theme.Panel2, BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(10),
                Padding = new Thickness(14, 10, 14, 12), Margin = new Thickness(0, 0, 10, 0),
                Child = Ui.Column(Ui.Caps(label, 10, Theme.Faint, 0.13), number.Margin(0, 4, 0, 0)),
            };
            return (tile, number);
        }
        var scraped = Stat("Scraped", Theme.Hd);
        var skipped = Stat("No data", Theme.Muted);
        var failed = Stat("Failed", Theme.Warn);
        var review = Stat("To review", Theme.Ember);
        var stats = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        var tiles = new[] { scraped.Tile, skipped.Tile, failed.Tile, review.Tile };
        for (var i = 0; i < tiles.Length; i++)
        {
            stats.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            if (i == tiles.Length - 1) tiles[i].Margin = new Thickness(0);
            Grid.SetColumn(tiles[i], i);
            stats.Children.Add(tiles[i]);
        }
        var current = Ui.Text("", 13, Theme.Muted, margin: new Thickness(0, 0, 0, 8));
        current.TextTrimming = TextTrimming.CharacterEllipsis;
        var bar = new ContentControl { Focusable = false, Content = Ui.ProgressBar(0) };
        var body = new StackPanel { Margin = new Thickness(0, 20, 0, 0) };

        Button stopButton = null!;
        stopButton = Ui.Button("Stop", () =>
        {
            stop.Cancel();
            stopButton.IsEnabled = false;
        }, small: true);
        var closeButton = Ui.Button("Close", () => w.Close(), Ui.Look.Ember, small: true);
        closeButton.Visibility = Visibility.Collapsed;
        var button = new Grid { VerticalAlignment = VerticalAlignment.Center };
        button.Children.Add(stopButton);
        button.Children.Add(closeButton);

        var player = new MiniPlayer { VerticalAlignment = VerticalAlignment.Top };
        var playerColumn = new ColumnDefinition { Width = new GridLength(0) };
        var blocks = new Dictionary<object, Border>();
        object? selected = null;
        var selectVersion = 0;

        List<(string Path, string Title)> VideosOf(long actorId, string prefix)
        {
            var a = Catalog.Actor(actorId, "added");
            if (a == null) return new();
            return Catalog.VideosOf(a).Select(v => (v.Str("path"), prefix + v.Str("title"))).ToList();
        }

        async void Select(object item)
        {
            selected = item;
            foreach (var (key, border) in blocks)
                border.BorderBrush = ReferenceEquals(key, item) ? Theme.Ember : Theme.LineSoft;
            var mine = ++selectVersion;
            var items = await Task.Run(() => item switch
            {
                BatchChoice c when o.Kind == "actor" => VideosOf(c.Id, ""),
                BatchChoice c => Db.QueryOne("SELECT path, title FROM videos WHERE id = ?", c.Id) is { } v
                    ? new List<(string, string)> { (v.Str("path"), v.Str("title")) } : new(),
                BatchMerge m => VideosOf(m.FromId, m.FromName + " · ").Concat(VideosOf(m.IntoId, m.IntoName + " · ")).ToList(),
                _ => new(),
            });
            if (mine != selectVersion) return;
            if (item is BatchChoice pick && o.Kind == "video" && pick.Options.Any(h => h.Image.Length == 0 && h.Source == "tpdb"))
                _ = FillArt(pick);
            var heading = item switch
            {
                BatchChoice c => c.Label,
                BatchMerge m => $"{m.FromName} / {m.IntoName}",
                _ => "",
            };
            player.Load(items, items.Count > 1 ? $"{heading} · {items.Count} videos" : heading);
        }

        // Long result lists come without pictures; a choice's are fetched when it is looked at.
        async Task FillArt(BatchChoice c)
        {
            var filled = await Task.Run(() => c.Options.Select(h => h.Image.Length > 0 ? h : Scrape.CompleteScene(h.Copy())).ToList());
            lock (state)
            {
                if (!state.Choices.Contains(c)) return;
                c.Options = filled;
            }
            if (blocks.TryGetValue(c, out var block)) FillChoice(block, c);
            Save();
        }

        void Paint()
        {
            int pending;
            lock (state) pending = state.Choices.Count + state.Merges.Count;
            scraped.Number.Text = state.Succeeded.ToString();
            skipped.Number.Text = state.Skipped.ToString();
            failed.Number.Text = state.Failed.ToString();
            review.Number.Text = pending.ToString();
            bar.Content = Ui.ProgressBar(state.Total == 0 ? (finished ? 1 : 0) : (double)state.Done / state.Total);
            current.Text = finished
                ? $"{state.Done} of {state.Total}"
                : state.Total == 0 ? "Starting…" : $"{state.Done} of {state.Total} · {state.Current}";
        }

        // One item at a time: the player shows one video, and a short page moves on at once.
        void ShowReview()
        {
            body.Children.Clear();
            blocks.Clear();
            List<object> queue;
            List<string> errors;
            lock (state)
            {
                queue = state.Choices.Cast<object>().Concat(state.Merges).ToList();
                errors = state.Errors.ToList();
            }
            if (selected != null && queue.IndexOf(selected) is var at and >= 0) index = at;
            index = queue.Count == 0 ? 0 : Math.Clamp(index, 0, queue.Count - 1);

            if (queue.Count > 0)
            {
                var item = queue[index];
                var nav = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 12) };
                Button Arrow(string glyph, int step)
                {
                    var b = Ui.Button(glyph, () => { index += step; selected = null; ShowReview(); }, small: true);
                    b.IsEnabled = index + step >= 0 && index + step < queue.Count;
                    return b;
                }
                var position = Ui.Text($"{index + 1} / {queue.Count}", 13, Theme.Muted, margin: new Thickness(12, 0, 12, 0));
                position.VerticalAlignment = VerticalAlignment.Center;
                position.FontFamily = Theme.Mono;
                var arrows = Ui.Row(Arrow("‹", -1), position, Arrow("›", 1));
                arrows.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(arrows, Dock.Right);
                nav.Children.Add(arrows);
                var heading = Heading(item is BatchChoice ? "Pick the match" : "Same performer?");
                heading.Margin = new Thickness(0);
                heading.VerticalAlignment = VerticalAlignment.Center;
                nav.Children.Add(heading);
                body.Children.Add(nav);
                body.Children.Add(item is BatchChoice c ? ChoiceBlock(c) : MergeRow((BatchMerge)item));
            }
            if (errors.Count > 0)
            {
                var failHead = new DockPanel { LastChildFill = true };
                Button retry = null!;
                retry = Ui.Button("Retry failed", async () =>
                {
                    List<long> ids;
                    lock (state)
                    {
                        ids = state.FailedIds.ToList();
                        state.FailedIds.Clear();
                        state.Errors.Clear();
                        state.Failed = 0;
                    }
                    retry.IsEnabled = false;
                    current.Text = "Retrying…";
                    await Task.Run(() => Batch.Run(o, state, CancellationToken.None, Tick, ids));
                    Images.Clear();
                    Save();
                    ShowReview();
                    win.Refresh(keepScroll: true);
                }, small: true);
                retry.VerticalAlignment = VerticalAlignment.Center;
                DockPanel.SetDock(retry, Dock.Right);
                failHead.Children.Add(retry);
                failHead.Children.Add(Heading($"Failed ({errors.Count})"));
                body.Children.Add(failHead);
                foreach (var e in errors)
                    body.Children.Add(Ui.Text(e, 12.5, Theme.Muted, wrap: true, margin: new Thickness(0, 0, 0, 6)));
            }
            var watchable = queue.Count > 0;
            player.Visibility = watchable ? Visibility.Visible : Visibility.Collapsed;
            playerColumn.Width = watchable ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
            if (!watchable) player.Close();
            else if (!ReferenceEquals(selected, queue[index])) Select(queue[index]);
            else if (blocks.TryGetValue(selected!, out var still)) still.BorderBrush = Theme.Ember;
            scroll.ScrollToTop();
            Paint();
        }

        static FrameworkElement Heading(string text) =>
            Ui.Text(text, 15, Theme.Text, FontWeights.Bold, margin: new Thickness(0, 10, 0, 12));

        FrameworkElement ChoiceBlock(BatchChoice c)
        {
            var block = new Border
            {
                Background = Theme.Panel2, BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14), Margin = new Thickness(0, 0, 0, 12),
            };
            FillChoice(block, c);
            blocks[c] = block;
            block.Cursor = Cursors.Hand;
            block.PreviewMouseLeftButtonDown += (_, _) => { if (!ReferenceEquals(selected, c)) Select(c); };
            return block;
        }

        void FillChoice(Border block, BatchChoice c)
        {
            var stack = new StackPanel();
            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
            var skip = Ui.Link("Skip", () =>
            {
                lock (state) state.Choices.Remove(c);
                state.Skipped++;
                Save();
                ShowReview();
            }, 12.5);
            DockPanel.SetDock(skip, Dock.Right);
            head.Children.Add(skip);
            var title = Ui.Text(c.Label, 14, Theme.Text, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            stack.Children.Add(head);
            var status = Ui.Text("", 12.5, Theme.Muted, wrap: true);
            stack.Children.Add(SearchRow(c, block, status));
            foreach (var hit in c.Options)
                stack.Children.Add(Option(hit, async () =>
                {
                    block.IsEnabled = false;
                    status.Foreground = Theme.Muted;
                    status.Text = "Saving…";
                    try
                    {
                        await Task.Run(() => Batch.Apply(o, c.Id, hit, state));
                        lock (state) state.Choices.Remove(c);
                        state.Succeeded++;
                        Images.Clear();
                        Save();
                        ShowReview();
                    }
                    catch (Exception ex)
                    {
                        block.IsEnabled = true;
                        status.Foreground = Theme.Warn;
                        status.Text = ex.Message;
                    }
                }));
            stack.Children.Add(status);
            block.Child = stack;
        }

        // Studio, performers and title to search again by hand (a name, for a performer).
        FrameworkElement SearchRow(BatchChoice c, Border block, TextBlock status)
        {
            var boxes = new List<TextBox>();
            string studio = "", cast = "", title = c.Label;
            if (typed.TryGetValue(c, out var kept))
            {
                (studio, cast, title) = (kept[0], kept[1], kept[2]);
            }
            else if (o.Kind != "actor" && Catalog.Video(c.Id) is { } v)
            {
                studio = v.Str("subsite").Length > 0 ? v.Str("subsite") : v.Str("studio_name");
                cast = string.Join(", ", Catalog.Cast(v).Select(a => a.Str("name")));
                title = v.Str("title");
            }
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 12) };
            var fields = o.Kind == "actor"
                ? new[] { ("Name", title, 1.0) }
                : new[] { ("Studio", studio, 1.0), ("Performers", cast, 1.4), ("Title", title, 1.8) };
            foreach (var (label, value, weight) in fields)
            {
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(weight, GridUnitType.Star) });
                var box = new TextBox { Text = value };
                boxes.Add(box);
                var field = Ui.Field(label, box);
                field.Margin = new Thickness(0, 0, 8, 0);
                Grid.SetColumn(field, grid.ColumnDefinitions.Count - 1);
                grid.Children.Add(field);
            }
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Button go = null!;
            async void Run()
            {
                go.IsEnabled = false;
                status.Foreground = Theme.Muted;
                status.Text = "Looking…";
                try
                {
                    var values = boxes.Select(b => b.Text.Trim()).ToArray();
                    typed[c] = o.Kind == "actor" ? new[] { "", "", values[0] } : values;
                    var found = await Task.Run(() => o.Kind == "actor"
                        ? Batch.SearchAgain(o, c.Id, "", "", values[0])
                        : Batch.SearchAgain(o, c.Id, values[0], values[1], values[2]));
                    if (found.Count == 0)
                    {
                        status.Text = "Nothing found";
                        go.IsEnabled = true;
                        return;
                    }
                    lock (state) c.Options = found;
                    FillChoice(block, c);
                    Save();
                    if (o.Kind == "video") _ = FillArt(c);
                }
                catch (Exception ex)
                {
                    status.Foreground = Theme.Warn;
                    status.Text = ex.Message;
                    go.IsEnabled = true;
                }
            }
            go = Ui.Button("Search", Run, Ui.Look.Ember, small: true);
            go.VerticalAlignment = VerticalAlignment.Bottom;
            Grid.SetColumn(go, grid.ColumnDefinitions.Count - 1);
            grid.Children.Add(go);
            foreach (var b in boxes)
                b.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Run(); e.Handled = true; } };
            return grid;
        }

        FrameworkElement Option(Found hit, Action use)
        {
            var portrait = o.Kind == "actor";
            var art = new Border
            {
                Width = portrait ? 66 : 176, Height = portrait ? 90 : 99, CornerRadius = new CornerRadius(6), Background = Theme.Ink, ClipToBounds = true,
            };
            Ui.Whole(art, hit.ImageChoices(), 360);
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            var name = Ui.Text(hit.Name.Length > 0 ? hit.Name : "Untitled", 13.5, Theme.Text, FontWeights.SemiBold);
            name.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(name);
            var line = string.Join(" · ", new[] { hit.Subtitle, hit.Date.Length > 0 ? hit.Date : hit.Birthdate }.Where(s => s.Length > 0));
            if (line.Length > 0) text.Children.Add(Ui.Text(line, 11.5, Theme.Muted, margin: new Thickness(0, 2, 0, 0)));
            var cast = portrait ? string.Join(", ", hit.Aliases.Take(4)) : string.Join(", ", hit.Performers.Take(5));
            if (cast.Length > 0)
            {
                var c = Ui.Text(cast, 11.5, Theme.Faint, margin: new Thickness(0, 2, 0, 0));
                c.TextTrimming = TextTrimming.CharacterEllipsis;
                text.Children.Add(c);
            }
            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(art);
            dock.Children.Add(text);
            var face = new Border
            {
                Padding = new Thickness(8), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 6),
                BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), Background = Theme.Clear, Child = dock,
            };
            var b = Ui.Bare(face, use);
            b.Cursor = Cursors.Hand;
            b.HorizontalContentAlignment = HorizontalAlignment.Stretch;
            b.MouseEnter += (_, _) => { face.BorderBrush = Theme.Ember; face.Background = Theme.Alpha(Theme.EmberC, 0x12); };
            b.MouseLeave += (_, _) => { face.BorderBrush = Theme.LineSoft; face.Background = Theme.Clear; };
            return b;
        }

        FrameworkElement MergeRow(BatchMerge m)
        {
            var names = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
            void Names()
            {
                names.Children.Clear();
                names.Children.Add(Ui.Text(m.FromName, 14, Theme.Text, FontWeights.SemiBold));
                // Which name stays: the swap turns the merge round.
                var swap = Ui.Link("  ⇄  ", () =>
                {
                    (m.FromId, m.IntoId) = (m.IntoId, m.FromId);
                    (m.FromName, m.IntoName) = (m.IntoName, m.FromName);
                    Names();
                }, 15, Theme.Faint);
                swap.ToolTip = "Swap";
                names.Children.Add(swap);
                names.Children.Add(Ui.Text(m.IntoName, 14, Theme.Ember, FontWeights.SemiBold));
            }
            Names();
            var merge = Ui.Button("Merge", async () =>
            {
                await Task.Run(() => Catalog.MergeActors(m.FromId, m.IntoId));
                lock (state)
                {
                    state.Merges.Remove(m);
                    // Anything else waiting on the profile that just went now points at the one kept.
                    foreach (var other in state.Merges)
                    {
                        if (other.FromId == m.FromId) { other.FromId = m.IntoId; other.FromName = m.IntoName; }
                        if (other.IntoId == m.FromId) { other.IntoId = m.IntoId; other.IntoName = m.IntoName; }
                    }
                    state.Merges.RemoveAll(x => x.FromId == x.IntoId);
                }
                Images.Clear();
                Save();
                ShowReview();
            }, Ui.Look.Ember, small: true);
            var apart = Ui.Button("Keep apart", () =>
            {
                lock (state) state.Merges.Remove(m);
                Save();
                ShowReview();
            }, small: true);
            var buttons = Ui.Row(apart, merge.Margin(8, 0, 0, 0));
            var dock = new DockPanel { LastChildFill = true };
            DockPanel.SetDock(buttons, Dock.Right);
            dock.Children.Add(buttons);
            dock.Children.Add(names);
            var row = new Border
            {
                Background = Theme.Panel2, BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(12),
                Padding = new Thickness(14, 10, 10, 10), Margin = new Thickness(0, 0, 0, 8), Child = dock, Cursor = Cursors.Hand,
            };
            blocks[m] = row;
            row.PreviewMouseLeftButtonDown += (_, _) => { if (!ReferenceEquals(selected, m)) Select(m); };
            return row;
        }

        var header = new StackPanel { Margin = new Thickness(24, 20, 24, 0) };
        var top = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 16) };
        DockPanel.SetDock(button, Dock.Right);
        top.Children.Add(button);
        top.Children.Add(Ui.Title(o.Kind == "actor" ? "Batch scrape · Pornstars" : "Batch scrape · Videos", 19, false));
        header.Children.Add(top);
        header.Children.Add(stats);
        header.Children.Add(current);
        header.Children.Add(bar);
        scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(0, 0, 8, 20) };
        Ui.ChainWheel(scroll);
        var split = new Grid { Margin = new Thickness(24, 0, 16, 0) };
        split.ColumnDefinitions.Add(playerColumn);
        split.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        player.Visibility = Visibility.Collapsed;
        player.Margin = new Thickness(0, 20, 20, 20);
        split.Children.Add(player);
        Grid.SetColumn(scroll, 1);
        split.Children.Add(scroll);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.Children.Add(header);
        Grid.SetRow(split, 1);
        layout.Children.Add(split);
        w.Content = layout;

        var lastPaint = DateTime.MinValue;
        void Tick()
        {
            if ((DateTime.UtcNow - lastPaint).TotalMilliseconds < 120 && state.Running) return;
            lastPaint = DateTime.UtcNow;
            w.Dispatcher.BeginInvoke(Paint);
        }

        w.PreviewKeyDown += (_, e) =>
        {
            if (!finished || e.Key is not (Key.Left or Key.Right) || e.OriginalSource is TextBox) return;
            int count;
            lock (state) count = state.Choices.Count + state.Merges.Count;
            var next = index + (e.Key == Key.Right ? 1 : -1);
            if (next < 0 || next >= count) return;
            index = next;
            selected = null;
            ShowReview();
            e.Handled = true;
        };
        w.Closing += (_, _) => { if (!finished) stop.Cancel(); player.Close(); if (finished) Save(); };
        w.Closed += (_, _) => win.Refresh(keepScroll: true);
        w.Loaded += async (_, _) =>
        {
            Paint();
            if (resumed == null)
            {
                try { await Task.Run(() => Batch.Run(o, state, stop.Token, Tick)); }
                finally { _busy = false; }
                finished = true;
                Save();
            }
            else _busy = false;
            stopButton.Visibility = Visibility.Collapsed;
            closeButton.Visibility = Visibility.Visible;
            Images.Clear();
            lock (state)
                if (state.Choices.Count + state.Merges.Count > 0 && w.WindowState == WindowState.Normal)
                {
                    var width = Math.Min(1400, SystemParameters.WorkArea.Width - 40);
                    var height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
                    w.Left = Math.Max(SystemParameters.WorkArea.Left, w.Left - (width - w.Width) / 2);
                    w.Top = Math.Max(SystemParameters.WorkArea.Top, w.Top - (height - w.Height) / 2);
                    w.Width = width;
                    w.Height = height;
                }
            ShowReview();
            win.Refresh(keepScroll: true);
        };
        w.Show();
    }
}
