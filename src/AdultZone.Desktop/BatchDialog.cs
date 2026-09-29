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

    public static void Start(MainWindow win, BatchOptions o)
    {
        if (_busy) { win.Toast("A batch scrape is already running"); return; }
        _busy = true;
        var w = Dialogs.Create(win, "Batch scrape", 780, 760);
        var state = new BatchState();
        var stop = new CancellationTokenSource();
        var finished = false;

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
            var heading = item switch
            {
                BatchChoice c => c.Label,
                BatchMerge m => $"{m.FromName} / {m.IntoName}",
                _ => "",
            };
            player.Load(items, items.Count > 1 ? $"{heading} · {items.Count} videos" : heading);
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

        void ShowReview()
        {
            body.Children.Clear();
            blocks.Clear();
            List<BatchChoice> choices;
            List<BatchMerge> merges;
            List<string> errors;
            lock (state)
            {
                choices = state.Choices.ToList();
                merges = state.Merges.ToList();
                errors = state.Errors.ToList();
            }

            if (choices.Count > 0)
            {
                body.Children.Add(Heading($"Pick the match ({choices.Count})"));
                foreach (var c in choices) body.Children.Add(ChoiceBlock(c));
            }
            if (merges.Count > 0)
            {
                body.Children.Add(Heading($"Same performer? ({merges.Count})"));
                foreach (var m in merges) body.Children.Add(MergeRow(m));
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
            var watchable = choices.Count + merges.Count > 0;
            player.Visibility = watchable ? Visibility.Visible : Visibility.Collapsed;
            playerColumn.Width = watchable ? new GridLength(0.9, GridUnitType.Star) : new GridLength(0);
            if (!watchable) player.Close();
            else if (selected == null || !blocks.ContainsKey(selected))
                Select(choices.Count > 0 ? choices[0] : merges[0]);
            else if (blocks.TryGetValue(selected, out var still)) still.BorderBrush = Theme.Ember;
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
            var stack = new StackPanel();
            var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
            var skip = Ui.Link("Skip", () =>
            {
                lock (state) state.Choices.Remove(c);
                state.Skipped++;
                ShowReview();
            }, 12.5);
            DockPanel.SetDock(skip, Dock.Right);
            head.Children.Add(skip);
            var title = Ui.Text(c.Label, 14, Theme.Text, FontWeights.SemiBold);
            title.TextTrimming = TextTrimming.CharacterEllipsis;
            head.Children.Add(title);
            stack.Children.Add(head);
            var status = Ui.Text("", 12.5, Theme.Muted, wrap: true);
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
            blocks[c] = block;
            block.Cursor = Cursors.Hand;
            block.PreviewMouseLeftButtonDown += (_, _) => { if (!ReferenceEquals(selected, c)) Select(c); };
            return block;
        }

        FrameworkElement Option(Found hit, Action use)
        {
            var portrait = o.Kind == "actor";
            var art = new Border
            {
                Width = portrait ? 46 : 104, Height = portrait ? 62 : 58, CornerRadius = new CornerRadius(6), Background = Theme.Panel3, ClipToBounds = true,
            };
            if (hit.Image.Length > 0) Ui.Cover(art, hit.Image, decode: 220);
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
                ShowReview();
            }, Ui.Look.Ember, small: true);
            var apart = Ui.Button("Keep apart", () =>
            {
                lock (state) state.Merges.Remove(m);
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
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false, Padding = new Thickness(0, 0, 8, 20) };
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

        w.Closing += (_, _) => { if (!finished) stop.Cancel(); player.Close(); };
        w.Closed += (_, _) => win.Refresh(keepScroll: true);
        w.Loaded += async (_, _) =>
        {
            Paint();
            try { await Task.Run(() => Batch.Run(o, state, stop.Token, Tick)); }
            finally { _busy = false; }
            finished = true;
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
