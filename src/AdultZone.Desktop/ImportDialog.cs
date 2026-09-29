using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdultZone.Core.Providers;

namespace AdultZone.Desktop;

/// <summary>
/// Find info: search a source, pick the right match, tick the fields to keep.
/// Nothing is written until Apply, so a wrong match never overwrites anything.
/// </summary>
public static class ImportDialog
{
    public sealed record Hints(string Studio = "", string Subsite = "", string Performer = "");

    static readonly Dictionary<string, (string Key, string Label)[]> Fields = new()
    {
        ["actor"] = new[] { ("name", "Name"), ("description", "Biography"), ("birthdate", "Date of birth"), ("gender", "Gender"), ("country", "Nationality"), ("status", "Career status"), ("facts", "Details"), ("image", "Portrait"), ("banner", "Wide photo") },
        ["studio"] = new[] { ("name", "Name"), ("description", "Description"), ("image", "Logo") },
        ["video"] = new[] { ("name", "Title"), ("description", "Description"), ("date", "Release date"), ("performers", "Cast"), ("site", "Sub-site"),
                            ("tags", "Tags"), ("studio", "Studio"), ("image", "Thumbnail") },
    };

    static string SearchKind(string kind) => kind switch { "actor" => "performer", "studio" => "studio", _ => "scene" };

    public static void Show(MainWindow win, string kind, long id, string query, Hints? hints = null)
    {
        hints ??= new Hints();
        var searchKind = SearchKind(kind);
        var movie = kind == "video" && AdultZone.Core.Data.Db.QueryOne("SELECT kind FROM videos WHERE id = ?", id)?.Str("kind") == "movie";
        if (movie) searchKind = "movie";
        var providers = Scrape.Providers().Where(p => p.Kinds.Contains(searchKind))
                              .OrderBy(p => p.Id == "tpdb" && p.Ready ? 0 : p.Ready ? 1 : 2).ToList();
        var w = Dialogs.Create(win, "Find info", 720, 760);

        var provider = providers.FirstOrDefault(p => p.Ready)?.Id ?? "url";
        var source = new ComboBox { Width = 190 };
        foreach (var p in providers)
        {
            var item = new ComboBoxItem { Content = p.Ready ? p.Name : p.Name + " — needs a key", Tag = p.Id, IsEnabled = p.Ready };
            source.Items.Add(item);
            if (p.Id == provider) source.SelectedItem = item;
        }
        var box = new TextBox { Text = query };
        var queryLabel = Ui.Caps("Search for", 10.5, Theme.Faint, 0.13);
        var studio = new TextBox { Text = hints.Studio };
        var subsite = new TextBox { Text = hints.Subsite };
        var performer = new TextBox { Text = hints.Performer };
        var results = new StackPanel();
        var status = Ui.Text("", 13, Theme.Muted, wrap: true, margin: new Thickness(0, 12, 0, 0));
        var mode = "title";
        var version = 0;
        List<Found> last = new();

        var queryField = new StackPanel();
        queryField.Children.Add(queryLabel.Margin(0, 0, 0, 6));
        queryField.Children.Add(box);
        var pairFields = new Grid { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        foreach (var (label, control, col) in new[] { ("Studio", studio, 0), ("Sub-site", subsite, 1), ("Performer", performer, 2) })
        {
            pairFields.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var f = Ui.Field(label, control);
            f.Margin = new Thickness(0, 0, col < 2 ? 10 : 0, 0);
            Grid.SetColumn(f, col);
            pairFields.Children.Add(f);
        }

        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is not ComboBoxItem { Tag: string p }) return;
            provider = p;
            queryLabel.Set(p == "url" ? "PAGE ADDRESS" : "SEARCH FOR", Theme.Faint, FontWeights.SemiBold);
        };

        string CurrentQuery() => mode == "pair"
            ? string.Join(" ", new[] { studio.Text, subsite.Text, performer.Text }.Select(s => s.Trim()).Where(s => s.Length > 0))
            : box.Text.Trim();

        async Task Run()
        {
            var q = CurrentQuery();
            if (q.Length == 0) return;
            var mine = ++version;
            results.Children.Clear();
            status.Foreground = Theme.Muted;
            status.Text = "Looking…";
            var chosenProvider = provider;
            try
            {
                last = await Task.Run(() => Scrape.Search(chosenProvider, searchKind, q));
            }
            catch (Exception ex)
            {
                if (mine != version) return;
                status.Foreground = Theme.Warn;
                status.Text = ex.Message;
                return;
            }
            if (mine != version) return;
            status.Text = last.Count == 0 ? "Nothing found" : "";
            foreach (var hit in last) results.Children.Add(ResultRow(hit));
        }

        FrameworkElement ResultRow(Found hit)
        {
            var art = new Border { Width = 58, Height = 78, CornerRadius = new CornerRadius(6), Background = Theme.Panel2, ClipToBounds = true };
            if (hit.Image.Length > 0) Ui.Cover(art, hit.Image, decode: 160);
            var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0) };
            text.Children.Add(Ui.Text(hit.Name.Length > 0 ? hit.Name : "Untitled", 14, Theme.Text, FontWeights.SemiBold));
            var sub = string.Join(" · ", new[] { hit.Subtitle, hit.Date }.Where(s => s.Length > 0));
            if (sub.Length > 0) text.Children.Add(Ui.Text(sub, 11.5, Theme.Muted, margin: new Thickness(0, 2, 0, 0)));
            if (hit.Description.Length > 0)
            {
                var d = Ui.Clamp(hit.Description, 12.5, new SolidColorBrush(Color.FromRgb(0xA5, 0xA2, 0xB0)), 2, 18);
                d.Margin = new Thickness(0, 5, 0, 0);
                text.Children.Add(d);
            }
            var attribution = Ui.Text(hit.Attribution.Length > 0 ? hit.Attribution : hit.Source, 10.5, Theme.Faint, margin: new Thickness(0, 5, 0, 0));
            attribution.FontFamily = Theme.Mono;
            text.Children.Add(attribution);
            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(art);
            dock.Children.Add(text);
            var card = new Border
            {
                Padding = new Thickness(10), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 8),
                BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), Background = Theme.Clear, Child = dock,
            };
            var b = Ui.Bare(card, async () =>
            {
                // A performer is read in full before the fields are shown: the results carry only part of the record.
                if (kind == "actor" && hit.Source == "tpdb")
                {
                    status.Foreground = Theme.Muted;
                    status.Text = "Reading the profile…";
                    var mine = version;
                    await Task.Run(() => Scrape.Complete(hit));
                    if (mine != version) return;
                    status.Text = "";
                }
                Choose(hit);
            });
            b.Cursor = Cursors.Hand;
            b.MouseEnter += (_, _) => { card.BorderBrush = Theme.Ember; card.Background = Theme.Alpha(Theme.EmberC, 0x12); };
            b.MouseLeave += (_, _) => { card.BorderBrush = Theme.LineSoft; card.Background = Theme.Clear; };
            return b;
        }

        bool Has(Found f, string key) => key switch
        {
            "name" => f.Name.Length > 0,
            "description" => f.Description.Length > 0,
            "date" => f.Date.Length > 0,
            "birthdate" => f.Birthdate.Length > 0,
            "gender" => f.Gender.Length > 0,
            "country" => f.Country.Length > 0,
            "status" => f.Status.Length > 0,
            "facts" => f.Facts.Count > 0,
            "image" => f.Image.Length > 0,
            "banner" => f.Banner.Length > 0,
            "performers" => f.Performers.Count > 0,
            "tags" => f.Tags.Count > 0,
            "site" => f.Site.Length > 0,
            "studio" => f.Studio.Length > 0,
            _ => false,
        };

        UIElement Value(Found f, string key)
        {
            if (key is "image" or "banner")
            {
                var img = Images.Lazy(key == "image" ? f.Image : f.Banner, 380, Stretch.Uniform);
                img.MaxWidth = 190;
                img.MaxHeight = 130;
                img.HorizontalAlignment = HorizontalAlignment.Left;
                return img;
            }
            if (key is "performers" or "tags")
            {
                var wrap = new WrapPanel();
                foreach (var n in key == "performers" ? f.Performers : f.Tags)
                    wrap.Children.Add(new Pill
                    {
                        Background = Theme.Alpha(Colors.White, 0x12), BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(12), Padding = new Thickness(9, 2, 9, 2), Margin = new Thickness(0, 0, 5, 5),
                        Child = Ui.Text(n, 11.5, new SolidColorBrush(Color.FromRgb(0xC2, 0xC0, 0xCB))),
                    });
                return wrap;
            }
            var muted = new SolidColorBrush(Color.FromRgb(0xA5, 0xA2, 0xB0));
            if (key == "gender")
                return Gender.Labelled(f.Gender, AdultZone.Core.Library.Catalog.Genders.FirstOrDefault(g => g.Key == f.Gender).Label ?? "", muted);
            if (key == "country")
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal };
                if (Countries.Flag(f.Country, 20) is { } flag)
                {
                    flag.Margin = new Thickness(0, 0, 7, 0);
                    row.Children.Add(flag);
                }
                row.Children.Add(Ui.Text(Countries.Name(f.Country), 12.5, muted));
                return row;
            }
            if (key == "facts")
            {
                var grid = new WrapPanel();
                foreach (var (column, label, _) in AdultZone.Core.Library.Catalog.PerformerFacts)
                    if (f.Facts.TryGetValue(column, out var value))
                    {
                        var cell = Ui.Column(Ui.Caps(label, 9.5, Theme.Faint, 0.12), Ui.Text(value, 12.5, muted, margin: new Thickness(0, 2, 0, 0)));
                        cell.Width = 150;
                        cell.Margin = new Thickness(0, 0, 8, 8);
                        grid.Children.Add(cell);
                    }
                return grid;
            }
            var text = key switch
            {
                "name" => f.Name, "description" => f.Description, "date" => f.Date, "birthdate" => f.Birthdate,
                "status" => Dialogs.StatusLabel(f.Status),
                "site" => f.Site, "studio" => f.Studio, _ => "",
            };
            if (text.Length > 400) text = text[..400] + "…";
            return Ui.Text(text, 12.5, new SolidColorBrush(Color.FromRgb(0xA5, 0xA2, 0xB0)), wrap: true);
        }

        void Choose(Found hit)
        {
            var available = Fields[kind].Where(f => Has(hit, f.Key)).ToList();
            if (available.Count == 0)
            {
                status.Text = "That match has nothing to import";
                return;
            }
            ++version;
            status.Text = "";
            results.Children.Clear();
            var picks = new List<(string Key, CheckBox Box)>();
            foreach (var (key, raw) in available)
            {
                var label = movie && key == "image" ? "Cover" : raw;
                var check = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 12, 0) };
                var column = new StackPanel();
                column.Children.Add(Ui.Text(label, 12, Theme.Text, FontWeights.Bold, margin: new Thickness(0, 0, 0, 4)));
                column.Children.Add(Value(hit, key));
                var row = new DockPanel { LastChildFill = true };
                row.Children.Add(check);
                row.Children.Add(column);
                results.Children.Add(new Border
                {
                    BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 11, 0, 11), Child = row,
                });
                picks.Add((key, check));
            }
            var replace = new CheckBox { Content = "Replace the cast and tags already here", IsChecked = true, Margin = new Thickness(0, 14, 0, 0), FontSize = 13 };
            if (kind == "video") results.Children.Add(replace);
            var back = Ui.Button("Back to results", () =>
            {
                results.Children.Clear();
                foreach (var h in last) results.Children.Add(ResultRow(h));
            }, small: true);
            Button apply = null!;
            apply = Ui.Button("Apply selected", async () =>
            {
                var chosen = picks.Where(p => p.Box.IsChecked == true).Select(p => p.Key).ToHashSet();
                var replaceIt = replace.IsChecked == true;
                w.IsEnabled = false;
                status.Foreground = Theme.Muted;
                status.Text = "Saving…";
                try
                {
                    var applied = await Task.Run(() => kind switch
                    {
                        "actor" => Scrape.ApplyActor(id, hit, chosen),
                        "studio" => Scrape.ApplyStudio(id, hit, chosen),
                        _ => Scrape.ApplyVideo(id, hit, chosen, replaceIt),
                    });
                    w.Close();
                    Images.Clear();
                    win.Toast(applied.Count > 0 ? "Updated " + string.Join(", ", applied) : "Nothing selected");
                    win.Refresh(keepScroll: true);
                }
                catch (Exception ex)
                {
                    w.IsEnabled = true;
                    status.Foreground = Theme.Warn;
                    status.Text = ex.Message;
                }
            }, Ui.Look.Ember, small: true);
            var foot = Ui.Row(back, apply.Margin(10, 0, 0, 0));
            foot.HorizontalAlignment = HorizontalAlignment.Right;
            foot.Margin = new Thickness(0, 18, 0, 0);
            results.Children.Add(foot);
        }

        var go = Ui.Button("Search", () => _ = Run(), Ui.Look.Ember, small: true);
        go.VerticalAlignment = VerticalAlignment.Bottom;
        foreach (var t in new[] { box, studio, subsite, performer })
            t.KeyDown += (_, e) => { if (e.Key == Key.Enter) { _ = Run(); e.Handled = true; } };

        var top = new DockPanel { LastChildFill = true };
        var sourceField = Ui.Field("Source", source);
        sourceField.Margin = new Thickness(0, 0, 10, 0);
        DockPanel.SetDock(sourceField, Dock.Left);
        top.Children.Add(sourceField);
        go.Margin = new Thickness(10, 0, 0, 0);
        DockPanel.SetDock(go, Dock.Right);
        top.Children.Add(go);
        top.Children.Add(queryField);

        var stack = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        stack.Children.Add(Ui.Title("Find info", 19, false).Margin(0, 0, 0, 16));
        if (kind == "video")
        {
            var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 14) };
            var buttons = new List<(string Key, Border Face, TextBlock Text)>();
            void SetMode(string m)
            {
                mode = m;
                foreach (var (key, face, text) in buttons)
                {
                    var on = key == m;
                    face.Background = on ? Theme.Alpha(Theme.EmberC, 0x1F) : Theme.Alpha(Colors.White, 0x0D);
                    face.BorderBrush = on ? Theme.Alpha(Theme.EmberC, 0x73) : Theme.Line;
                    text.Foreground = on ? Theme.Ember : Theme.Muted;
                }
                pairFields.Visibility = m == "pair" ? Visibility.Visible : Visibility.Collapsed;
                queryField.Visibility = m == "pair" ? Visibility.Collapsed : Visibility.Visible;
            }
            foreach (var (key, label) in new[] { ("title", "By title"), ("pair", "By studio, sub-site and performer") })
            {
                var text = Ui.Text(label, 12.5, Theme.Muted);
                var face = new Pill { CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), Padding = new Thickness(14, 7, 14, 7), Child = text };
                var b = Ui.Bare(face, () => SetMode(key));
                b.Cursor = Cursors.Hand;
                b.Margin = new Thickness(0, 0, 8, 0);
                buttons.Add((key, face, text));
                modes.Children.Add(b);
            }
            SetMode("title");
            stack.Children.Add(modes);
            stack.Children.Add(pairFields);
        }
        stack.Children.Add(top);
        stack.Children.Add(status);
        stack.Children.Add(results.Margin(0, 16, 0, 0));
        w.Content = new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        w.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
            if (query.Trim().Length > 0 && provider != "url") _ = Run();
        };
        w.ShowDialog();
    }
}
