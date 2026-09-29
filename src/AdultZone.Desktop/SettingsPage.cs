using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using AdultZone.Core;
using AdultZone.Core.Data;
using AdultZone.Core.Library;
using AdultZone.Core.Media;
using AdultZone.Core.Providers;

namespace AdultZone.Desktop;

/// <summary>Storage folders, scanning and artwork, metadata sources, the screen lock.</summary>
public static class SettingsPage
{
    public static async Task<FrameworkElement> Build(MainWindow win)
    {
        var (stats, locations) = await Task.Run(() => (Catalog.Stats(), Catalog.Locations()));
        var stack = new StackPanel { MaxWidth = 980, HorizontalAlignment = HorizontalAlignment.Left };
        stack.Children.Add(Ui.PageHead("Library", "Settings",
            $"{Ui.Plural(stats.Videos, "video", "videos")} · {Ui.Plural(stats.Actors, "star", "stars")} · " +
            $"{Ui.Plural(stats.Studios, "studio", "studios")} · {Ui.Plural(stats.Tags, "tag", "tags")}"));

        if (!Ffmpeg.Available)
        {
            var warn = Ui.Panel("ffmpeg was not found",
                Ui.Text("Put ffmpeg.exe and ffprobe.exe in a folder called ffmpeg beside Adult Zone, or install ffmpeg, then restart.",
                        13.5, Theme.Muted, wrap: true));
            warn.BorderBrush = Theme.Alpha(Theme.WarnC, 0x66);
            ((TextBlock)((StackPanel)warn.Child).Children[0]).Foreground = Theme.Warn;
            stack.Children.Add(warn);
        }

        stack.Children.Add(Folders(win, locations));
        stack.Children.Add(Scanning(win, stats));
        stack.Children.Add(Display(win));
        stack.Children.Add(HomeScreen(win));
        stack.Children.Add(Sources(win));
        stack.Children.Add(BatchScrape(win));
        stack.Children.Add(ScreenLock(win));
        stack.Children.Add(About(win));
        stack.Children.Add(FileNames());
        return Ui.Page(stack);
    }

    // --------------------------------------------------------------- folders
    static FrameworkElement Folders(MainWindow win, System.Collections.Generic.List<Row> locations)
    {
        var list = new StackPanel();
        foreach (var loc in locations)
        {
            var id = loc.Long("id") ?? 0;
            var on = new CheckBox { IsChecked = loc.Truthy("enabled"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            on.Checked += (_, _) => Catalog.EnableLocation(id, true);
            on.Unchecked += (_, _) => Catalog.EnableLocation(id, false);
            var path = Ui.Text(loc.Str("path"), 12.5, Theme.Text, wrap: true);
            path.FontFamily = Theme.Mono;
            var meta = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
            meta.Children.Add(Ui.Text(Ui.Plural(loc.Long("video_count") ?? 0, "video", "videos"), 11.5, Theme.Muted));
            if (!loc.Truthy("exists"))
            {
                meta.Children.Add(Ui.Dot());
                meta.Children.Add(Ui.Text("folder not found", 11.5, Theme.Warn));
            }
            var remove = Ui.Button("Remove", () =>
            {
                var purge = Dialogs.Choose(win, "Also remove this folder's videos from the library?", "Remove its videos", "Keep them");
                if (purge == null) return;
                Catalog.RemoveLocation(id, purge.Value);
                win.Refresh(keepScroll: true);
            }, small: true);
            remove.VerticalAlignment = VerticalAlignment.Center;
            var kind = Ui.Select(new[] { ("scene", "Scenes"), ("movie", "Movies") }, loc.Str("kind") is "movie" ? "movie" : "scene", k =>
            {
                Catalog.SetLocationKind(id, k);
                win.Toast(k == "movie" ? "This folder now holds movies" : "This folder now holds scenes");
            }, 130);
            kind.VerticalAlignment = VerticalAlignment.Center;
            kind.Margin = new Thickness(12, 0, 10, 0);
            var dock = new DockPanel { LastChildFill = true };
            dock.Children.Add(on);
            DockPanel.SetDock(remove, Dock.Right);
            dock.Children.Add(remove);
            DockPanel.SetDock(kind, Dock.Right);
            dock.Children.Add(kind);
            dock.Children.Add(Ui.Column(path, meta));
            list.Children.Add(new Border { BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(0, 0, 0, 1), Padding = new Thickness(0, 12, 0, 12), Child = dock });
        }
        if (locations.Count == 0) list.Children.Add(Ui.Text("No folders yet", 13, Theme.Muted, margin: new Thickness(0, 0, 0, 6)));
        Button Add(string label, string kind, Ui.Look look) => Ui.Button(label, () =>
        {
            if (Dialogs.PickFolder(win) is not { } folder) return;
            if (Catalog.AddLocation(folder, kind) is { } problem) { win.Toast(problem, true); return; }
            win.Toast(kind == "movie" ? "Movies folder added" : "Folder added");
            win.Refresh(keepScroll: true);
        }, look, Icons.Folder(16, look == Ui.Look.Ember ? Theme.OnEmber : Theme.Text), small: true);
        var buttons = Ui.Actions(Add("Add scenes folder", "scene", Ui.Look.Ember), Add("Add movies folder", "movie", Ui.Look.Ghost));
        buttons.Margin = new Thickness(0, 16, 0, 0);
        return Ui.Panel("Storage folders", list, buttons);
    }

    // -------------------------------------------------------------- scanning
    static FrameworkElement Scanning(MainWindow win, Stats stats)
    {
        CheckBox Switch(string label, string key)
        {
            var box = new CheckBox { Content = label, IsChecked = Db.SettingOn(key), Margin = new Thickness(0, 0, 0, 12), FontSize = 13.5 };
            box.Checked += (_, _) => Db.SetSetting(key, true);
            box.Unchecked += (_, _) => Db.SetSetting(key, false);
            return box;
        }
        var folderStudio = Switch("Folder name as studio", "folder_as_studio");
        var twoPart = Switch("“Studio - Name” as studio and performer", "two_part_actor");

        var quality = Ui.Select(PreviewProfiles.All.Select(p => (p.Name, $"{p.Label} — up to {p.Width}px")), Assets.Profile.Name, k =>
        {
            Db.SetSetting("preview_quality", k);
            win.Toast("Saved");
            win.Refresh(keepScroll: true);
        }, 240);
        var qualityField = Ui.Field("Preview quality", quality);
        qualityField.HorizontalAlignment = HorizontalAlignment.Left;
        qualityField.Margin = new Thickness(0, 6, 0, 16);

        var message = Ui.Text("", 13, Theme.Muted, wrap: true);
        var bar = new ContentControl { Focusable = false, Margin = new Thickness(0, 12, 0, 0), Content = Ui.ProgressBar(0) };
        void Show(ScanState s)
        {
            if (s.Error.Length > 0 && !s.Busy) { message.Text = s.Error; message.Foreground = Theme.Warn; return; }
            message.Foreground = Theme.Muted;
            if (s.Running)
            {
                message.Text = $"Scanning — {Ui.Plural(s.Found, "file", "files")} seen, {s.Added} added · {s.Current}";
                bar.Content = Ui.ProgressBar(0.35);
            }
            else if (s.Building)
            {
                message.Text = $"Building thumbnails and previews — {s.AssetsDone} of {s.AssetsTotal} · {s.Current}";
                bar.Content = Ui.ProgressBar((double)s.AssetsDone / Math.Max(1, s.AssetsTotal));
            }
            else if (s.Phase != "idle")
            {
                message.Text = $"Done — {s.Added} added, {s.Updated} restored, {s.Removed} missing";
                bar.Content = Ui.ProgressBar(1);
            }
        }
        Show(Scanner.State);
        win.ScanTick += Show;
        message.Unloaded += (_, _) => win.ScanTick -= Show;

        var scan = Ui.Button("Scan now", () =>
        {
            if (Catalog.Locations().Count == 0) { win.Toast("Add a folder first"); return; }
            Scanner.ScanAll();
            win.WatchScan();
        }, Ui.Look.Ember, small: true);
        var missing = Ui.Button(stats.NoArtwork > 0 ? $"Build missing artwork ({stats.NoArtwork})" : "Build missing artwork", () =>
        {
            if (!Ffmpeg.Available) { win.Toast("ffmpeg was not found", true); return; }
            var n = Scanner.RebuildAll(true);
            win.Toast(n > 0 ? $"Building artwork for {Ui.Plural(n, "video", "videos")}" : "Nothing to build");
            win.WatchScan();
        }, small: true);
        missing.IsEnabled = stats.NoArtwork > 0;
        var everything = Ui.Button(stats.BelowQuality > 0 ? $"Rebuild everything ({stats.BelowQuality} below this quality)" : "Rebuild everything", () =>
        {
            if (!Ffmpeg.Available) { win.Toast("ffmpeg was not found", true); return; }
            if (!Dialogs.Confirm(win, "Rebuild the thumbnail and preview of every video?", "Rebuild")) return;
            Scanner.RebuildAll(false);
            win.WatchScan();
        }, small: true);
        var buttons = Ui.Actions(scan, missing, everything);
        return Ui.Panel("Scan", folderStudio, twoPart, qualityField, buttons, message, bar);
    }

    // --------------------------------------------------------------- display
    static FrameworkElement Display(MainWindow win)
    {
        var order = Ui.Select(Catalog.CastOrders, Db.Setting("cast_order"), k =>
        {
            Db.SetSetting("cast_order", k);
            win.Toast("Saved");
        }, 240);
        var field = Ui.Field("Cast order", order);
        field.HorizontalAlignment = HorizontalAlignment.Left;
        field.Margin = new Thickness(0);
        return Ui.Panel("Display", field);
    }

    // ----------------------------------------------------------- home screen
    static FrameworkElement HomeScreen(MainWindow win)
    {
        var keys = Catalog.HomeRowKeys();
        void Save() { Catalog.SetHomeRowKeys(keys); }
        CheckBox Row(string key, string label)
        {
            var box = new CheckBox { Content = label, IsChecked = keys.Contains(key), Margin = new Thickness(0, 0, 22, 12), FontSize = 13.5 };
            box.Checked += (_, _) => { if (!keys.Contains(key)) keys.Add(key); Save(); };
            box.Unchecked += (_, _) => { keys.Remove(key); Save(); };
            return box;
        }
        var builtIn = new WrapPanel();
        foreach (var (key, label) in Catalog.HomeRowKinds) builtIn.Children.Add(Row(key, label));

        var tags = new WrapPanel();
        var names = Catalog.Tags().Where(t => (t.Long("video_count") ?? 0) > 0)
            .Select(t => (Name: t.Str("name"), Count: t.Long("video_count") ?? 0)).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        var filter = new TextBox { Width = 260, HorizontalAlignment = HorizontalAlignment.Left };
        void Fill()
        {
            tags.Children.Clear();
            var q = filter.Text.Trim();
            foreach (var (name, count) in names.Where(n => q.Length == 0 || n.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
                                                        || keys.Contains("tag:" + n.Name)))
            {
                var box = Row("tag:" + name, $"{name} ({count})");
                box.Width = 220;
                tags.Children.Add(box);
            }
        }
        filter.TextChanged += (_, _) => Fill();
        Fill();
        var tagScroll = new ScrollViewer { Content = tags, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Ui.ChainWheel(tagScroll);
        var filterField = Ui.Field("Tag rows", filter);
        filterField.Margin = new Thickness(0, 8, 0, 10);
        return Ui.Panel("Home screen", builtIn, filterField, tagScroll);
    }

    // --------------------------------------------------------------- sources
    static FrameworkElement Sources(MainWindow win)
    {
        var key = new PasswordBox
        {
            Password = Db.Setting("tpdb_key"), Background = Theme.InkSoft, Foreground = Theme.Text, BorderBrush = Theme.Line,
            Padding = new Thickness(12, 9, 12, 9), CaretBrush = Theme.EmberHi, SelectionBrush = Theme.Ember, FontSize = 13.5,
        };
        var address = new TextBox { Text = Db.Setting("tpdb_base") };
        var state = Ui.Text(Scrape.TpdbKey.Length > 0 ? "A key is saved" : "", 13, Theme.Muted, wrap: true);
        var save = Ui.Button("Save key", () =>
        {
            Db.SetSetting("tpdb_key", key.Password.Trim());
            Db.SetSetting("tpdb_base", address.Text.Trim().TrimEnd('/'));
            state.Foreground = Theme.Muted;
            state.Text = Scrape.TpdbKey.Length > 0 ? "A key is saved" : "";
            win.Toast("Saved");
        }, small: true);
        var test = Ui.Button("Test key", async () =>
        {
            Db.SetSetting("tpdb_key", key.Password.Trim());
            Db.SetSetting("tpdb_base", address.Text.Trim().TrimEnd('/'));
            state.Foreground = Theme.Muted;
            state.Text = "Checking…";
            var (ok, said) = await Task.Run(Scrape.TestTpdb);
            state.Text = said;
            state.Foreground = ok ? Theme.Hd : Theme.Warn;
        }, small: true);
        var keyField = Ui.Field("ThePornDB API key", key, 360);
        var addressField = Ui.Field("ThePornDB address", address, 360);
        var row = new WrapPanel();
        row.Children.Add(keyField);
        row.Children.Add(addressField);
        return Ui.Panel("Metadata sources", row, Ui.Actions(save, test), state);
    }

    // ----------------------------------------------------------- batch scrape
    static FrameworkElement BatchScrape(MainWindow win)
    {
        var kind = "video";
        var provider = "";
        var sourceHost = new ContentControl { Focusable = false };
        var fields = new WrapPanel { Margin = new Thickness(0, 4, 0, 6) };
        var checks = new System.Collections.Generic.List<(string Key, CheckBox Box)>();
        CheckBox Option(string label, bool on) => new() { Content = label, IsChecked = on, Margin = new Thickness(0, 0, 22, 12), FontSize = 13.5 };
        var autoMerge = Option("Auto-merge matching names", false);
        var skipDone = Option("Skip ones already scraped", true);
        var replace = Option("Replace cast and tags", true);
        Func<Task> begin = () => Task.CompletedTask;
        var start = Ui.Button("Start", () => _ = begin(), Ui.Look.Ember, small: true);

        void Build()
        {
            var providers = Scrape.Providers().Where(p => p.Id != "url" && p.Kinds.Contains(kind == "actor" ? "performer" : "scene")).ToList();
            provider = providers.FirstOrDefault(p => p.Ready && p.Id == "tpdb")?.Id ?? providers.FirstOrDefault(p => p.Ready)?.Id ?? "";
            var box = new ComboBox { Width = 220 };
            foreach (var p in providers)
            {
                var item = new ComboBoxItem { Content = p.Ready ? p.Name : p.Name + " — needs a key", Tag = p.Id, IsEnabled = p.Ready };
                box.Items.Add(item);
                if (p.Id == provider) box.SelectedItem = item;
            }
            box.SelectionChanged += (_, _) => { if (box.SelectedItem is ComboBoxItem { Tag: string id }) provider = id; };
            sourceHost.Content = Ui.Field("Source", box);

            fields.Children.Clear();
            checks.Clear();
            foreach (var (key, label) in kind == "actor" ? Batch.ActorFields : Batch.VideoFields)
            {
                var c = Option(label, true);
                c.Margin = new Thickness(0, 0, 20, 10);
                checks.Add((key, c));
                fields.Children.Add(c);
            }
            replace.Visibility = kind == "video" ? Visibility.Visible : Visibility.Collapsed;
            start.IsEnabled = provider.Length > 0;
        }
        var what = Ui.Select(new[] { ("video", "Videos"), ("actor", "Pornstars") }, kind, k => { kind = k; Build(); }, 220);
        Build();

        begin = async () =>
        {
            var picked = checks.Where(c => c.Box.IsChecked == true).Select(c => c.Key).ToHashSet();
            if (picked.Count == 0) { win.Toast("Tick at least one field", true); return; }
            if (provider.Length == 0) { win.Toast("No source is ready", true); return; }
            var options = new BatchOptions(kind, provider, picked, autoMerge.IsChecked == true, skipDone.IsChecked == true, replace.IsChecked == true);
            var count = await Task.Run(() => Batch.Count(options.Kind, options.SkipDone));
            if (count == 0) { win.Toast("Nothing to scrape"); return; }
            if (!Dialogs.Confirm(win, $"Scrape {Ui.Plural(count, kind == "actor" ? "profile" : "video", kind == "actor" ? "profiles" : "videos")}?", "Start")) return;
            BatchDialog.Start(win, options);
        };

        var top = new WrapPanel();
        top.Children.Add(Ui.Field("Scrape", what));
        top.Children.Add(sourceHost);
        var switches = new WrapPanel();
        switches.Children.Add(autoMerge);
        switches.Children.Add(skipDone);
        switches.Children.Add(replace);
        start.HorizontalAlignment = HorizontalAlignment.Left;
        return Ui.Panel("Batch scrape", top, Ui.Caps("Fields", 10.5, Theme.Faint, 0.13).Margin(0, 0, 0, 8), fields, switches, start);
    }

    // ------------------------------------------------------------ screen lock
    static FrameworkElement ScreenLock(MainWindow win)
    {
        PasswordBox Pin() => new()
        {
            MaxLength = Lock.MaxLength, Background = Theme.InkSoft, Foreground = Theme.Text, BorderBrush = Theme.Line, Width = 180,
            Padding = new Thickness(12, 9, 12, 9), CaretBrush = Theme.EmberHi, FontSize = 13.5,
        };
        var row = new WrapPanel();
        var buttons = new WrapPanel();
        var state = Ui.Text(Lock.Enabled ? $"On — {Lock.Length} digits" : "Off", 13, Theme.Muted, margin: new Thickness(0, 0, 0, 12));
        if (!Lock.Enabled)
        {
            var first = Pin();
            var again = Pin();
            row.Children.Add(Ui.Field("New PIN", first));
            row.Children.Add(Ui.Field("Confirm", again));
            buttons.Children.Add(Ui.Button("Turn on screen lock", () =>
            {
                if (first.Password != again.Password) { win.Toast("The two PINs do not match", true); return; }
                if (!Lock.Valid(first.Password)) { win.Toast($"Use {Lock.MinLength} to {Lock.MaxLength} digits", true); return; }
                Lock.SetPin(first.Password);
                win.UpdateLockButton();
                win.Toast("Screen lock is on");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Ember, small: true));
        }
        else
        {
            var current = Pin();
            var first = Pin();
            var again = Pin();
            row.Children.Add(Ui.Field("Current PIN", current));
            row.Children.Add(Ui.Field("New PIN", first));
            row.Children.Add(Ui.Field("Confirm new", again));
            buttons.Children.Add(Ui.Button("Change PIN", () =>
            {
                if (!Lock.Verify(current.Password)) { win.Toast("That is not the current PIN", true); return; }
                if (first.Password != again.Password) { win.Toast("The two PINs do not match", true); return; }
                if (!Lock.Valid(first.Password)) { win.Toast($"Use {Lock.MinLength} to {Lock.MaxLength} digits", true); return; }
                Lock.SetPin(first.Password);
                win.Toast("PIN changed");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Ember, small: true).Margin(0, 0, 10, 10));
            buttons.Children.Add(Ui.Button("Lock now", win.LockNow, small: true).Margin(0, 0, 10, 10));
            buttons.Children.Add(Ui.Button("Turn off", () =>
            {
                if (!Lock.Verify(current.Password)) { win.Toast("Enter your current PIN first", true); return; }
                Lock.Clear();
                win.UpdateLockButton();
                win.Toast("Screen lock is off");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Warn, small: true).Margin(0, 0, 10, 10));
        }
        return Ui.Panel("Screen lock", state, row, buttons);
    }

    // ----------------------------------------------------------------- about
    static FrameworkElement About(MainWindow win)
    {
        var folder = Ui.Text(Config.AppHome, 12.5, Theme.Text, wrap: true);
        folder.FontFamily = Theme.Mono;
        var open = Ui.Button("Open library folder", () =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Config.AppHome}\"") { UseShellExecute = true }); }
            catch (Exception ex) { win.Toast(ex.Message, true); }
        }, small: true);
        open.HorizontalAlignment = HorizontalAlignment.Left;
        open.Margin = new Thickness(0, 12, 0, 0);
        return Ui.Panel($"{Config.AppName} {Config.AppVersion}", folder, open);
    }

    // ------------------------------------------------------------- file names
    static FrameworkElement FileNames()
    {
        var list = new StackPanel();
        foreach (var line in new[]
        {
            "Studio - Actor Name.mp4",
            "Studio - Actor One, Actor Two.mp4",
            "Studio - Actor Name - 27.07.2021.mp4",
            "Studio - Actor One, Actor Two - 27.07.2021.mp4",
            "Studio - Actor Name - Title.mp4",
            "Studio - Actor One, Actor Two - Title (2024).mp4",
            "Actor Name - Title - [Studio].mp4",
            "Actor One, Actor Two - Title (2024) - [Studio].mp4",
            "Studio.24.03.15.Actor.Name.Title.mp4",
        })
        {
            var t = Ui.Text(line, 12.5, Theme.Muted, margin: new Thickness(0, 0, 0, 8));
            t.FontFamily = Theme.Mono;
            list.Children.Add(t);
        }
        return Ui.Panel("File name order", list);
    }
}
