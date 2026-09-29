using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using AdultZone.Core;
using AdultZone.Core.Data;
using AdultZone.Core.Library;

namespace AdultZone.Desktop;

/// <summary>Dark child windows: questions, and the editors for videos, performers and studios.</summary>
public static class Dialogs
{
    static readonly List<Window> Open = new();

    public static Window Create(Window owner, string title, double width, double height)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            Width = width,
            Height = height,
            Background = Theme.Panel,
            Foreground = Theme.Text,
            FontFamily = Theme.Sans,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            ResizeMode = ResizeMode.CanResizeWithGrip,
        };
        App.DarkTitleBar(w);
        TextOptions.SetTextFormattingMode(w, TextFormattingMode.Display);
        w.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { w.Close(); e.Handled = true; }
        };
        w.Closed += (_, _) => Open.Remove(w);
        Open.Add(w);
        return w;
    }

    public static bool CloseTop()
    {
        var top = Open.LastOrDefault();
        if (top == null) return false;
        top.Close();
        return true;
    }

    public static void CloseAll()
    {
        foreach (var w in Open.ToList()) w.Close();
    }

    /// <summary>.modal-head, the body, then .modal-foot with its buttons on the right.</summary>
    static void Lay(Window w, string heading, UIElement body, params Button[] buttons)
    {
        var grid = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var h = Ui.Title(heading, 19);
        h.Margin = new Thickness(0, 0, 0, 18);
        grid.Children.Add(h);
        var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        var foot = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        foreach (var b in buttons)
        {
            b.Margin = new Thickness(10, 0, 0, 0);
            foot.Children.Add(b);
        }
        Grid.SetRow(foot, 2);
        grid.Children.Add(foot);
        w.Content = grid;
    }

    /// <summary>A yes-or-cancel question. True when the first button is pressed.</summary>
    public static bool Confirm(Window owner, string title, string yes)
    {
        var w = Create(owner, Config.AppName, 460, 200);
        w.ResizeMode = ResizeMode.NoResize;
        w.SizeToContent = SizeToContent.Height;
        var result = false;
        Lay(w, title, new Border(),
            Ui.Button("Cancel", () => w.Close(), small: true),
            Ui.Button(yes, () => { result = true; w.Close(); }, Ui.Look.Ember, small: true));
        w.ShowDialog();
        return result;
    }

    /// <summary>Two ways forward and a way out: returns the first (true), the second (false), or null.</summary>
    public static bool? Choose(Window owner, string title, string first, string second)
    {
        var w = Create(owner, Config.AppName, 520, 200);
        w.ResizeMode = ResizeMode.NoResize;
        w.SizeToContent = SizeToContent.Height;
        bool? result = null;
        Lay(w, title, new Border(),
            Ui.Button("Cancel", () => w.Close(), small: true),
            Ui.Button(second, () => { result = false; w.Close(); }, small: true),
            Ui.Button(first, () => { result = true; w.Close(); }, Ui.Look.Ember, small: true));
        w.ShowDialog();
        return result;
    }

    public static string? PickImage(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "Pictures|*.jpg;*.jpeg;*.png;*.webp;*.bmp;*.gif",
            CheckFileExists = true,
        };
        return dialog.ShowDialog(owner) == true ? dialog.FileName : null;
    }

    public static string? PickFolder(Window owner)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Multiselect = false };
        return dialog.ShowDialog(owner) == true ? dialog.FolderName : null;
    }

    static TextBox Box(string text, bool multi = false)
    {
        var t = new TextBox { Text = text };
        if (multi)
        {
            t.AcceptsReturn = true;
            t.TextWrapping = TextWrapping.Wrap;
            t.MinHeight = 96;
            t.MaxHeight = 220;
            t.VerticalContentAlignment = VerticalAlignment.Top;
            t.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        }
        return t;
    }

    /// <summary>Fields side by side, sharing the width.</summary>
    static Grid Pair(params UIElement[] fields)
    {
        var g = new Grid();
        for (var i = 0; i < fields.Length; i++)
        {
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(fields[i], i);
            if (i == fields.Length - 1 && fields[i] is FrameworkElement last) last.Margin = new Thickness(0, 0, 0, 14);
            g.Children.Add(fields[i]);
        }
        return g;
    }

    static StackPanel Full(string label, UIElement control)
    {
        var f = Ui.Field(label, control);
        f.Margin = new Thickness(0, 0, 0, 14);
        return f;
    }

    static List<string> Split(string text) =>
        text.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>A date typed as YYYY-MM-DD, or blank. Null when it is neither.</summary>
    static string? DateOrBlank(string text, out bool ok)
    {
        text = text.Trim();
        ok = true;
        if (text.Length == 0) return null;
        if (DateTime.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)) return text;
        ok = false;
        return null;
    }

    /// <summary>A button that picks a picture and shows its name once chosen.</summary>
    static (Button Button, Func<string?> Chosen) PictureButton(Window owner, string label)
    {
        string? chosen = null;
        Button b = null!;
        b = Ui.Button(label, () =>
        {
            if (PickImage(owner) is not { } file) return;
            chosen = file;
            ((StackPanel)b.Content).Children.OfType<TextBlock>().First().Text = Path.GetFileName(file);
        }, small: true);
        b.HorizontalAlignment = HorizontalAlignment.Left;
        return (b, () => chosen);
    }

    // ------------------------------------------------------------------ video
    public static void EditVideo(MainWindow win, Row v)
    {
        var id = v.Long("id") ?? 0;
        var w = Create(win, "Edit details", 660, 720);
        var title = Box(v.Str("title"));
        var desc = Box(v.Str("description"), true);
        var studio = Box(v.Str("studio_name"));
        var subsite = Box(v.Str("subsite"));
        var date = Box(v.Str("release_date") is { Length: >= 10 } d ? d[..10] : v.Str("release_date"));
        var cast = Box(string.Join(", ", Catalog.Cast(v).Select(a => a.Str("name"))));
        var tags = Box(string.Join(", ", Catalog.TagsOf(v)));
        var (thumbButton, thumbFile) = PictureButton(w, "Thumbnail");
        var (coverButton, coverFile) = PictureButton(w, "Cover");
        var kind = Catalog.IsMovie(v) ? "movie" : "scene";
        var kindBox = Ui.Select(Catalog.Kinds, kind, k => kind = k, double.NaN);
        var pictures = Ui.Row(thumbButton, coverButton.Margin(10, 0, 0, 0));
        var body = Ui.Column(
            Full("Title", title),
            Full("Description", desc),
            Pair(Ui.Field("Studio", studio), Ui.Field("Sub-site", subsite), Ui.Field("Release date", date), Ui.Field("Type", kindBox)),
            Full("Cast", cast),
            Full("Tags", tags),
            pictures);
        Lay(w, "Edit details", body,
            Ui.Button("Cancel", () => w.Close(), small: true),
            Ui.Button("Save changes", () =>
            {
                var release = DateOrBlank(date.Text, out var ok);
                if (!ok) { date.Focus(); date.SelectAll(); return; }
                Catalog.UpdateVideo(id, title.Text.Trim() is { Length: > 0 } t ? t : v.Str("title"), desc.Text.Trim(),
                    studio.Text.Trim(), subsite.Text.Trim(), release, Split(cast.Text), Split(tags.Text));
                if (thumbFile() is { } file)
                    Catalog.SetThumb(id, Core.Media.Assets.ImportThumb(v.Str("path"), file), true);
                if (coverFile() is { } coverPicked)
                    Catalog.SetCover(id, System.IO.File.ReadAllBytes(coverPicked), System.IO.Path.GetExtension(coverPicked));
                if (kind != (Catalog.IsMovie(v) ? "movie" : "scene")) Catalog.SetVideoKind(id, kind);
                w.Close();
                win.Toast("Saved");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Ember, small: true));
        w.Loaded += (_, _) => title.Focus();
        w.ShowDialog();
    }

    // ------------------------------------------------------------------ actor
    public static readonly (string Key, string Label)[] Statuses =
    {
        ("active", "Active"), ("inactive", "Inactive"), ("retired", "Retired"), ("died", "Died"),
    };

    public static string StatusLabel(string key) => Statuses.FirstOrDefault(s => s.Key == key).Label ?? "";

    public static void EditActor(MainWindow win, Row a)
    {
        var id = a.Long("id") ?? 0;
        var w = Create(win, "Edit profile", 640, 760);
        var name = Box(a.Str("name"));
        var age = Box(a.Long("age") is long n and > 0 ? n.ToString(CultureInfo.InvariantCulture) : "");
        var birth = Box(a.Str("birthdate") is { Length: >= 10 } b ? b[..10] : a.Str("birthdate"));
        var country = a.Str("country");
        var status = a.Str("status");
        var countryBox = Ui.Select(new[] { ("", "Not set") }.Concat(Countries.All.Select(c => (c.Code, c.Name))), country, k => country = k, double.NaN);
        countryBox.MaxDropDownHeight = 360;
        var statusBox = Ui.Select(new[] { ("", "Not set") }.Concat(Statuses), status, k => status = k, double.NaN);
        var gender = a.Str("gender");
        var genderBox = new ComboBox();
        genderBox.Items.Add(new ComboBoxItem { Content = "Not set", Tag = "" });
        foreach (var (key, label) in Catalog.Genders) genderBox.Items.Add(new ComboBoxItem { Content = Gender.Labelled(key, label), Tag = key });
        genderBox.SelectedIndex = Math.Max(0, Array.FindIndex(Catalog.Genders, g => g.Key == gender) + 1);
        genderBox.SelectionChanged += (_, _) => { if (genderBox.SelectedItem is ComboBoxItem { Tag: string k }) gender = k; };
        var desc = Box(a.Str("description"), true);
        var (photoButton, photoFile) = PictureButton(w, "Photo");
        var details = Catalog.PerformerFacts.Select(f => (f.Column, f.Label, Box: Box(a.Str(f.Column)))).ToList();
        var body = Ui.Column(
            Full("Name", name),
            Pair(Ui.Field("Age", age), Ui.Field("Date of birth", birth), Ui.Field("Gender", genderBox)),
            Pair(Ui.Field("Nationality", countryBox), Ui.Field("Career status", statusBox)));
        for (var i = 0; i < details.Count; i += 3)
            body.Children.Add(Pair(details.Skip(i).Take(3).Select(d => (UIElement)Ui.Field(d.Label, d.Box)).ToArray()));
        body.Children.Add(Full("Description", desc));
        body.Children.Add(photoButton);
        Lay(w, "Edit profile", body,
            Ui.Button("Cancel", () => w.Close(), small: true),
            Ui.Button("Save profile", () =>
            {
                var born = DateOrBlank(birth.Text, out var ok);
                if (!ok) { birth.Focus(); birth.SelectAll(); return; }
                var result = Catalog.UpdateActor(id, name.Text, age.Text, born, country, status, desc.Text.Trim(), gender);
                if (!result.Folded) Catalog.SetPerformerFacts(id, details.ToDictionary(d => d.Column, d => d.Box.Text.Trim()));
                if (!result.Folded && photoFile() is { } file) Catalog.SetActorPicture(id, "image", file);
                w.Close();
                if (result.Folded)
                {
                    win.Toast($"Merged {Ui.Plural(result.Merged, "credit", "credits")} into {name.Text.Trim()}");
                    win.Replace(new Location("actor", result.Id));
                    win.Refresh();
                    return;
                }
                win.Toast("Profile saved");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Ember, small: true));
        w.Loaded += (_, _) => name.Focus();
        w.ShowDialog();
    }

    // ----------------------------------------------------------------- studio
    public static void EditStudio(MainWindow win, Row s)
    {
        var id = s.Long("id") ?? 0;
        var w = Create(win, "Edit studio", 600, 460);
        var name = Box(s.Str("name"));
        var desc = Box(s.Str("description"), true);
        var (logoButton, logoFile) = PictureButton(w, "Logo");
        Lay(w, "Edit studio", Ui.Column(Full("Name", name), Full("Description", desc), logoButton),
            Ui.Button("Cancel", () => w.Close(), small: true),
            Ui.Button("Save studio", () =>
            {
                var result = Catalog.UpdateStudio(id, name.Text, desc.Text.Trim());
                if (!result.Folded && logoFile() is { } file) Catalog.SetStudioLogo(id, file);
                w.Close();
                if (result.Folded)
                {
                    win.Toast($"Merged {Ui.Plural(result.Merged, "video", "videos")} into {name.Text.Trim()}");
                    win.Replace(new Location("studio", result.Id));
                    win.Refresh();
                    return;
                }
                win.Toast("Studio saved");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Ember, small: true));
        w.Loaded += (_, _) => name.Focus();
        w.ShowDialog();
    }

    /// <summary>How the logo sits in its box, previewed exactly as the Studios page shows it.</summary>
    public static void AdjustLogo(MainWindow win, Row s)
    {
        var id = s.Long("id") ?? 0;
        var w = Create(win, "Adjust logo", 600, 640);
        var live = Cards.LogoLayout.Of(s);
        var preview = new ContentControl { Focusable = false };
        var count = s.Long("video_count") ?? 0;
        void Paint()
        {
            var logo = Cards.Logo(s, live, 262);
            var name = Ui.Text(s.Str("name"), 14, Theme.Text, FontWeights.SemiBold, margin: new Thickness(0, 9, 0, 0));
            var sub = Ui.Text(Ui.Plural(count, "video", "videos"), 11.5, Theme.Muted);
            sub.FontFamily = Theme.Mono;
            preview.Content = new Border
            {
                Background = Theme.Panel2, BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8),
                Padding = new Thickness(14), Width = 292, HorizontalAlignment = HorizontalAlignment.Left,
                Child = Ui.Column(logo, name, sub),
            };
        }

        var fit = Ui.Select(new[] { ("contain", "Contain"), ("cover", "Cover"), ("fill", "Stretch"), ("none", "Original size") }, live.Fit,
            k => { live = live with { Fit = k }; Paint(); }, double.NaN);
        var bg = Ui.Select(new[] { ("dark", "Dark"), ("black", "Black"), ("white", "White"), ("light", "Light grey"), ("none", "Transparent") }, live.Background,
            k => { live = live with { Background = k }; Paint(); }, double.NaN);
        (StackPanel Field, Slider Slider) SliderField(string label, double min, double max, double value, Action<int> set)
        {
            var valueText = Ui.Text("", 11, Theme.Ember, FontWeights.Bold);
            valueText.FontFamily = Theme.Mono;
            var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsSnapToTickEnabled = true, TickFrequency = 1 };
            void Show() => valueText.Text = $"{(int)slider.Value}%";
            slider.ValueChanged += (_, _) => { Show(); set((int)slider.Value); Paint(); };
            Show();
            var head = Ui.Row(Ui.Caps(label, 10.5, Theme.Faint, 0.13), valueText.Margin(8, 0, 0, 0));
            var f = new StackPanel { Margin = new Thickness(0, 0, 14, 14) };
            f.Children.Add(head.Margin(0, 0, 0, 6));
            f.Children.Add(slider);
            return (f, slider);
        }
        var zoom = SliderField("Zoom", 25, 1000, live.Zoom, v => live = live with { Zoom = v });
        var across = SliderField("Across", 0, 100, live.X, v => live = live with { X = v });
        var down = SliderField("Down", 0, 100, live.Y, v => live = live with { Y = v });
        Paint();
        var body = Ui.Column(preview.Margin(0, 0, 0, 18), Pair(Ui.Field("Fit", fit), Ui.Field("Behind it", bg)), zoom.Field, Pair(across.Field, down.Field));
        Lay(w, "Adjust logo", body,
            Ui.Button("Reset", () =>
            {
                live = Cards.LogoLayout.Default;
                fit.SelectedIndex = 0;
                bg.SelectedIndex = 0;
                zoom.Slider.Value = 100;
                across.Slider.Value = 50;
                down.Slider.Value = 50;
                Paint();
            }, small: true),
            Ui.Button("Cancel", () => w.Close(), small: true),
            Ui.Button("Save", () =>
            {
                Catalog.SetLogoLayout(id, live.Fit, live.Zoom, live.X, live.Y, live.Background);
                w.Close();
                win.Toast("Logo adjusted");
                win.Refresh(keepScroll: true);
            }, Ui.Look.Ember, small: true));
        w.ShowDialog();
    }
}
