using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdultZone.Core;
using AdultZone.Core.Library;

namespace AdultZone.Desktop;

/// <summary>Where you are: a tab, a video, a performer, a studio, a search.</summary>
public sealed record Location(string Kind, long Id = 0, string Query = "", string Tag = "", string Sort = "",
                              string Quality = "", bool Hidden = false, bool Play = false);

public sealed class MainWindow : Window
{
    public const double BarHeight = 62;

    readonly Grid _root = new();
    readonly Grid _app = new();
    readonly ContentControl _content = new() { Focusable = false };
    readonly Border _bar = new();
    readonly Button _back;
    readonly List<(string Kind, TextBlock Label, Border Underline)> _tabs = new();
    readonly TextBox _search = new();
    readonly Button _scan;
    readonly Border _scanFace = new();
    readonly Button _lockButton;
    readonly Border _toast = new();
    readonly TextBlock _toastText = Ui.Text("", 13.5, Theme.Text, wrap: true);
    readonly Border _busy = new();
    readonly TextBlock _busyText = Ui.Text("", 12, Theme.Muted);
    readonly Grid _busyBar = new();
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(2.8) };
    readonly DispatcherTimer _searchTimer = new() { Interval = TimeSpan.FromMilliseconds(180) };
    readonly DispatcherTimer _scanPoll = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    readonly List<(Location Where, double Offset)> _trail = new();
    Location? _searchOrigin;
    bool _settingSearch;
    bool _quickScan;

    public PlayerView Player { get; }
    public LockScreen LockScreen { get; }
    public Location Here => _trail.Count > 0 ? _trail[^1].Where : new Location("home");

    public MainWindow()
    {
        Title = Config.AppName;
        Background = Theme.Ink;
        Foreground = Theme.Text;
        FontFamily = Theme.Sans;
        Width = 1440;
        Height = 900;
        MinWidth = 960;
        MinHeight = 620;
        WindowState = WindowState.Maximized;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        App.DarkTitleBar(this);
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        try { Icon = BitmapFrame.Create(new Uri("pack://application:,,,/adultzone.ico")); } catch { }

        Player = new PlayerView(this);

        _back = Ui.Bare(new Border { Width = 36, Height = 36, CornerRadius = new CornerRadius(18), Background = Theme.Clear, Child = Icons.Back(22, Theme.Muted) }, GoBack, "Back");
        _scan = IconButton(_scanFace, Icons.Scan(20, Theme.Muted), QuickScan, "Scan");
        _lockButton = IconButton(new Border(), Icons.Lock(20, Theme.Muted), LockNow, "Lock");

        _app.Children.Add(_content);
        BuildBar();
        _app.Children.Add(_bar);
        BuildBusy();
        _app.Children.Add(_busy);
        _root.Children.Add(_app);

        Panel.SetZIndex(Player, 100);
        Player.Visibility = Visibility.Collapsed;
        _root.Children.Add(Player);

        BuildToast();
        _root.Children.Add(_toast);

        LockScreen = new LockScreen(this);
        Panel.SetZIndex(LockScreen, 400);
        _root.Children.Add(LockScreen);
        Content = _root;

        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _toast.Visibility = Visibility.Collapsed; };
        _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = Suggest(); };
        _scanPoll.Tick += (_, _) => PollScan();
        Scanner.Changed += () => Dispatcher.BeginInvoke(() => { if (!_scanPoll.IsEnabled) _scanPoll.Start(); });

        EventManager.RegisterClassHandler(typeof(Window), Keyboard.PreviewKeyDownEvent, new KeyEventHandler(OnAnyWindowKey));
        PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton == MouseButton.XButton1) { e.Handled = true; GoBack(); }
        };
        Loaded += async (_, _) =>
        {
            // The library is drawn behind the lock, blurred past reading, so the lock is glass over it.
            StartUp();
            if (Lock.Enabled) LockScreen.Show();
            await Task.Delay(1500);
            _ = Player.EnsureVlcAsync();
        };
        SourceInitialized += (_, _) =>
        {
            if (PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource source) source.AddHook(PaintVideoHostBlack);
        };
        Closing += (_, _) => Player.Shutdown();
    }

    bool _started;

    /// <summary>The library appears only once it is unlocked.</summary>
    public void StartUp()
    {
        UpdateLockButton();
        if (_started) return;
        _started = true;
        Navigate(new Location("home"));
    }

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    static extern IntPtr GetStockObject(int index);

    /// <summary>
    /// VLC draws into a plain Windows "static" window, which Windows paints
    /// light grey wherever the picture does not cover it. Answering its colour
    /// request with black hides those moments. Sideways wheels move rows.
    /// </summary>
    internal static IntPtr PaintVideoHostBlack(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_MOUSEHWHEEL = 0x020E;
        if (msg == WM_MOUSEHWHEEL)
        {
            var delta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
            if (Ui.SideWheel(delta)) handled = true;
            return IntPtr.Zero;
        }
        const int WM_CTLCOLORSTATIC = 0x0138;
        const int BLACK_BRUSH = 4;
        if (msg != WM_CTLCOLORSTATIC) return IntPtr.Zero;
        handled = true;
        return GetStockObject(BLACK_BRUSH);
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Maximized;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    // ---------------------------------------------------------------- top bar
    static Button IconButton(Border face, FrameworkElement icon, Action click, string tip)
    {
        face.Width = 36;
        face.Height = 36;
        face.CornerRadius = new CornerRadius(18);
        face.Background = Theme.Clear;
        face.Child = icon;
        var b = Ui.Bare(face, click, tip);
        b.Focusable = false;
        b.MouseEnter += (_, _) => face.Background = Theme.Alpha(Colors.White, 0x12);
        b.MouseLeave += (_, _) => face.Background = Theme.Clear;
        return b;
    }

    static readonly Brush BarFade = new LinearGradientBrush
    {
        StartPoint = new Point(0, 0), EndPoint = new Point(0, 1),
        GradientStops =
        {
            new GradientStop(Color.FromArgb(0xF5, 0x08, 0x08, 0x0B), 0),
            new GradientStop(Color.FromArgb(0xB8, 0x08, 0x08, 0x0B), 0.62),
            new GradientStop(Color.FromArgb(0x00, 0x08, 0x08, 0x0B), 1),
        },
    };

    static readonly Brush BarSolid = Theme.Alpha(Theme.InkC, 0xF7);

    void BuildBar()
    {
        _bar.Height = BarHeight;
        _bar.VerticalAlignment = VerticalAlignment.Top;
        _bar.Background = BarFade;
        _bar.Padding = new Thickness(Ui.Gutter, 0, Ui.Gutter, 0);
        _bar.BorderBrush = Theme.Clear;
        _bar.BorderThickness = new Thickness(0, 0, 0, 1);
        Panel.SetZIndex(_bar, 60);

        var dock = new DockPanel { LastChildFill = false };
        _back.Margin = new Thickness(-8, 0, 14, 0);
        _back.Visibility = Visibility.Collapsed;
        _back.Focusable = false;
        _back.VerticalAlignment = VerticalAlignment.Center;
        _back.MouseEnter += (_, _) => ((Border)_back.Content).Child = Icons.Back(22, Theme.Text);
        _back.MouseLeave += (_, _) => ((Border)_back.Content).Child = Icons.Back(22, Theme.Muted);
        dock.Children.Add(_back);

        var mark = Ui.Wordmark(20);
        mark.VerticalAlignment = VerticalAlignment.Center;
        var home = Ui.Bare(mark, () => Navigate(new Location("home")));
        home.Focusable = false;
        home.VerticalAlignment = VerticalAlignment.Center;
        home.Cursor = Cursors.Hand;
        home.Margin = new Thickness(0, 0, 34, 0);
        dock.Children.Add(home);

        foreach (var (kind, label) in new[] { ("home", "Home"), ("videos", "Scenes"), ("movies", "Movies"), ("studios", "Studios"), ("stars", "Pornstars") })
        {
            var text = Ui.Text(label, 13.5, Theme.Muted, FontWeights.Medium);
            var underline = new Border { Height = 2, CornerRadius = new CornerRadius(2), Background = Theme.Clear, Margin = new Thickness(0, 5, 0, 0) };
            var stack = Ui.Column(text, underline);
            stack.Margin = new Thickness(0, 7, 0, 0);
            var b = Ui.Bare(stack, () => Navigate(new Location(kind)));
            b.Focusable = false;
            b.Cursor = Cursors.Hand;
            b.VerticalAlignment = VerticalAlignment.Center;
            b.Margin = new Thickness(0, 0, 26, 0);
            b.MouseEnter += (_, _) => text.Foreground = Theme.Text;
            b.MouseLeave += (_, _) => text.Foreground = underline.Background == Theme.Clear ? Theme.Muted : Theme.Text;
            _tabs.Add((kind, text, underline));
            dock.Children.Add(b);
        }

        var right = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        right.Children.Add(BuildSearch());
        _scan.Margin = new Thickness(10, 0, 0, 0);
        right.Children.Add(_scan);
        _lockButton.Margin = new Thickness(4, 0, 0, 0);
        right.Children.Add(_lockButton);
        var settings = IconButton(new Border(), Icons.Settings(20, Theme.Muted), () => Navigate(new Location("settings")), "Settings");
        settings.Margin = new Thickness(4, 0, 0, 0);
        right.Children.Add(settings);
        DockPanel.SetDock(right, Dock.Right);
        dock.Children.Insert(0, right);
        _bar.Child = dock;
    }

    FrameworkElement BuildSearch()
    {
        // Just the text: the pill around it is the field.
        var bare = new ControlTemplate(typeof(TextBox));
        var host = new FrameworkElementFactory(typeof(ScrollViewer)) { Name = "PART_ContentHost" };
        host.SetValue(FocusableProperty, false);
        host.SetValue(ScrollViewer.HorizontalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        host.SetValue(ScrollViewer.VerticalScrollBarVisibilityProperty, ScrollBarVisibility.Hidden);
        bare.VisualTree = host;
        _search.Template = bare;
        _search.Background = Theme.Clear;
        _search.Padding = new Thickness(0);
        _search.FontSize = 13.5;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        var hint = Ui.Text("Search", 13.5, Theme.Faint);
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(_search);
        var icon = Icons.Search(16, Theme.Muted);
        icon.Margin = new Thickness(0, 0, 8, 0);
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(icon);
        dock.Children.Add(field);
        var pill = new Pill
        {
            Background = Theme.Alpha(Colors.White, 0x0D),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(14, 7, 14, 7),
            Width = 300,
            Child = dock,
            Cursor = Cursors.IBeam,
        };
        pill.MouseLeftButtonDown += (_, _) => _search.Focus();
        _search.GotKeyboardFocus += (_, _) => { pill.BorderBrush = Theme.Ember; pill.Background = Theme.Alpha(Colors.White, 0x14); };
        _search.LostKeyboardFocus += (_, _) => { pill.BorderBrush = Theme.Line; pill.Background = Theme.Alpha(Colors.White, 0x0D); };
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length > 0 ? Visibility.Collapsed : Visibility.Visible;
            // Text put there by the app itself is never a new search.
            if (_settingSearch) return;
            _searchTimer.Stop();
            if (_search.Text.Trim().Length == 0)
            {
                ClearSuggestions();
                LeaveSearch();
            }
            else _searchTimer.Start();
        };
        BuildSuggestions(pill);
        return pill;
    }

    // ----------------------------------------------------- search as you type
    // A list drops from the search box while typing: performers, studios and
    // videos that match. Arrow keys move through it, Enter opens the lit one
    // (or every result when none is lit), Esc closes it.
    readonly Popup _suggest = new()
    {
        Placement = PlacementMode.Bottom,
        StaysOpen = true,
        AllowsTransparency = true,
        VerticalOffset = 8,
        PopupAnimation = PopupAnimation.Fade,
    };
    readonly StackPanel _suggestList = new();
    readonly List<(Border Face, Action Open)> _suggestRows = new();
    int _suggestIndex = -1;
    int _suggestVersion;
    const double SuggestWidth = 420;

    void BuildSuggestions(FrameworkElement anchor)
    {
        _suggest.PlacementTarget = anchor;
        _suggest.HorizontalOffset = -(SuggestWidth - 300);      // right edges line up with the box
        _suggest.Child = new Border
        {
            Width = SuggestWidth,
            Background = Theme.Panel,
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 24, 24),
            Child = _suggestList,
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 10, Direction = 270, Opacity = 0.55 },
        };
        _search.PreviewKeyDown += (_, e) =>
        {
            switch (e.Key)
            {
                case Key.Down when _suggest.IsOpen:
                    MoveSuggestion(1);
                    e.Handled = true;
                    break;
                case Key.Up when _suggest.IsOpen:
                    MoveSuggestion(-1);
                    e.Handled = true;
                    break;
                case Key.Enter:
                    _searchTimer.Stop();
                    if (_suggest.IsOpen && _suggestIndex >= 0 && _suggestIndex < _suggestRows.Count)
                        _suggestRows[_suggestIndex].Open();
                    else RunSearch();
                    e.Handled = true;
                    break;
            }
        };
        _search.LostKeyboardFocus += (_, _) =>
        {
            // A click on a row lands after focus leaves; let it through first.
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                if (!_search.IsKeyboardFocusWithin && !_suggestList.IsMouseOver) CloseSuggestions();
            });
        };
        _search.GotKeyboardFocus += (_, _) =>
        {
            if (_search.Text.Trim().Length > 0 && _suggestRows.Count > 0) _suggest.IsOpen = true;
        };
        Deactivated += (_, _) => CloseSuggestions();
        PreviewMouseDown += (_, _) =>
        {
            if (_suggest.IsOpen && !anchor.IsMouseOver && !_suggestList.IsMouseOver) CloseSuggestions();
        };
        LocationChanged += (_, _) => CloseSuggestions();
    }

    public bool SuggestionsOpen => _suggest.IsOpen;

    public void CloseSuggestions()
    {
        _suggest.IsOpen = false;
        _suggestIndex = -1;
        PaintSuggestions();
    }

    void ClearSuggestions()
    {
        _suggestVersion++;
        _suggestRows.Clear();
        _suggestList.Children.Clear();
        CloseSuggestions();
    }

    void MoveSuggestion(int step)
    {
        if (_suggestRows.Count == 0) return;
        _suggestIndex = Math.Clamp(_suggestIndex + step, -1, _suggestRows.Count - 1);
        PaintSuggestions();
    }

    void PaintSuggestions()
    {
        for (var i = 0; i < _suggestRows.Count; i++)
            _suggestRows[i].Face.Background = i == _suggestIndex ? Theme.Alpha(Theme.EmberC, 0x1F) : Theme.Clear;
    }

    /// <summary>Best first: an exact name, then names starting with it, then a word starting with it.</summary>
    static int SuggestRank(string name, string q)
    {
        var t = name.ToLowerInvariant();
        if (t == q) return 0;
        if (t.StartsWith(q, StringComparison.Ordinal)) return 1;
        if (System.Text.RegularExpressions.Regex.IsMatch(t, @"(^|[^a-z0-9])" + System.Text.RegularExpressions.Regex.Escape(q))) return 2;
        if (t.Contains(q, StringComparison.Ordinal)) return 3;
        return 4;
    }

    async Task Suggest()
    {
        var q = _search.Text.Trim();
        var version = ++_suggestVersion;
        if (q.Length == 0)
        {
            ClearSuggestions();
            return;
        }
        var lower = q.ToLowerInvariant();
        var found = await Task.Run(() =>
        {
            var videos = Catalog.Videos(new VideoQuery(Search: q, Sort: "title", Limit: 80));
            var stars = Catalog.Actors(q, "count", false).Items;
            var studios = Catalog.Studios("count").Where(s => s.Str("name").Contains(q, StringComparison.OrdinalIgnoreCase)).ToList();
            return (videos.Total,
                    Videos: videos.Items.OrderBy(v => SuggestRank(v.Str("title"), lower)).ThenBy(v => v.Str("title"), StringComparer.OrdinalIgnoreCase).Take(5).ToList(),
                    Stars: stars.OrderBy(a => SuggestRank(a.Str("name"), lower)).ThenByDescending(a => a.Long("video_count") ?? 0).Take(3).ToList(),
                    Studios: studios.OrderBy(s => SuggestRank(s.Str("name"), lower)).ThenByDescending(s => s.Long("video_count") ?? 0).Take(3).ToList());
        });
        if (version != _suggestVersion || _search.Text.Trim() != q) return;       // typed on since

        _suggestRows.Clear();
        _suggestList.Children.Clear();
        _suggestIndex = -1;
        if (found.Videos.Count + found.Stars.Count + found.Studios.Count == 0)
            _suggestList.Children.Add(Ui.Text("No matches", 13, Theme.Muted, margin: new Thickness(12, 12, 12, 12)));

        void Add(FrameworkElement art, string title, string sub, Location to)
        {
            art.VerticalAlignment = VerticalAlignment.Center;
            var name = Ui.Text(title, 13.5, Theme.Text, FontWeights.SemiBold);
            var text = Ui.Column(name);
            if (sub.Length > 0) text.Children.Add(Ui.Text(sub, 11.5, Theme.Muted, margin: new Thickness(0, 3, 0, 0)));
            text.Margin = new Thickness(12, 0, 0, 0);
            text.VerticalAlignment = VerticalAlignment.Center;
            var row = new DockPanel { LastChildFill = true };
            row.Children.Add(art);
            row.Children.Add(text);
            var face = new Border { Padding = new Thickness(8, 7, 8, 7), CornerRadius = new CornerRadius(8), Background = Theme.Clear, Child = row };
            void Open()
            {
                CloseSuggestions();
                SetSearchText("");
                Keyboard.ClearFocus();
                Navigate(to);
            }
            var index = _suggestRows.Count;
            var button = Ui.Bare(face, Open);
            button.Focusable = false;
            button.Cursor = Cursors.Hand;
            button.MouseEnter += (_, _) => { _suggestIndex = index; PaintSuggestions(); };
            _suggestRows.Add((face, Open));
            _suggestList.Children.Add(button);
        }
        void Heading(string text) => _suggestList.Children.Add(Ui.Caps(text, 10.5, Theme.Faint, 0.13).Margin(10, 8, 10, 4));

        if (found.Stars.Count > 0)
        {
            Heading("Pornstars");
            foreach (var a in found.Stars)
            {
                var disc = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(20), Background = Theme.Panel3, ClipToBounds = true };
                var photo = Catalog.ActorPhoto(a);
                if (photo.Length > 0 && System.IO.File.Exists(photo)) Ui.Cover(disc, photo, 0.5, 0.25, decode: 96);
                else
                {
                    var initials = Ui.Text(Core.Library.Names.Initials(a.Str("name")), 13, Theme.Muted, FontWeights.Bold);
                    initials.HorizontalAlignment = HorizontalAlignment.Center;
                    initials.VerticalAlignment = VerticalAlignment.Center;
                    disc.Child = initials;
                }
                Add(disc, a.Str("name"), Ui.Plural(a.Long("video_count") ?? 0, "video", "videos"), new Location("actor", a.Long("id") ?? 0));
            }
        }
        if (found.Studios.Count > 0)
        {
            Heading("Studios");
            foreach (var st in found.Studios)
            {
                var logo = Cards.Logo(st, Cards.LogoLayout.Of(st), 80, "");
                Add(logo, st.Str("name"), Ui.Plural(st.Long("video_count") ?? 0, "video", "videos"), new Location("studio", st.Long("id") ?? 0));
            }
        }
        if (found.Videos.Count > 0)
        {
            Heading("Videos");
            foreach (var v in found.Videos)
            {
                var still = new Border { Width = 80, Height = 45, CornerRadius = new CornerRadius(5), Background = Theme.Panel3 };
                var thumb = Catalog.ThumbPath(v);
                if (thumb.Length > 0 && System.IO.File.Exists(thumb)) Ui.Cover(still, thumb, decode: 160);
                var sub = string.Join(" · ", new[] { v.Str("studio_name"), Catalog.Cast(v).FirstOrDefault()?.Str("name") ?? "" }.Where(x => x.Length > 0));
                Add(still, v.Str("title"), sub, new Location("video", v.Long("id") ?? 0));
            }
        }
        if (found.Total > found.Videos.Count)
        {
            var all = new Border
            {
                Padding = new Thickness(10, 10, 10, 10), CornerRadius = new CornerRadius(8), Background = Theme.Clear,
                Child = Ui.Text($"All {Ui.Plural(found.Total, "video", "videos")}", 13, Theme.Ember, FontWeights.SemiBold),
            };
            var allButton = Ui.Bare(all, RunSearch);
            allButton.Focusable = false;
            allButton.Cursor = Cursors.Hand;
            var index = _suggestRows.Count;
            allButton.MouseEnter += (_, _) => { _suggestIndex = index; PaintSuggestions(); };
            _suggestRows.Add((all, RunSearch));
            _suggestList.Children.Add(allButton);
        }
        if (_search.IsKeyboardFocusWithin) _suggest.IsOpen = true;
    }

    void RunSearch()
    {
        CloseSuggestions();
        var q = _search.Text.Trim();
        if (q.Length == 0) return;
        var next = new Location("videos", Query: q);
        if (Here.Kind == "videos" && Here.Query.Length > 0)
        {
            Replace(next);
            Refresh();
        }
        else
        {
            _searchOrigin = Here;
            Navigate(next);
        }
    }

    void LeaveSearch()
    {
        if (Here.Kind != "videos" || Here.Query.Length == 0) return;
        var back = _searchOrigin ?? new Location("home");
        _searchOrigin = null;
        Navigate(back);
    }

    public void FocusSearch()
    {
        _search.Focus();
        _search.SelectAll();
    }

    void SetActive(Location where)
    {
        var tab = where.Kind switch
        {
            "home" => "home",
            "videos" => "videos",
            "movies" => "movies",
            "studios" or "studio" => "studios",
            "stars" or "actor" => "stars",
            _ => "",
        };
        foreach (var (kind, label, underline) in _tabs)
        {
            var on = kind == tab;
            label.Foreground = on ? Theme.Text : Theme.Muted;
            label.FontWeight = on ? FontWeights.SemiBold : FontWeights.Medium;
            underline.Background = on ? Theme.Ember : Theme.Clear;
        }
        _back.Visibility = _trail.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (where.Kind != "videos" || where.Query.Length == 0)
        {
            _searchTimer.Stop();
            SetSearchText("");
            _searchOrigin = null;
        }
        else if (!_search.IsKeyboardFocused && _search.Text != where.Query) SetSearchText(where.Query);
    }

    void SetSearchText(string text)
    {
        if (_search.Text == text) return;
        _settingSearch = true;
        _search.Text = text;
        _settingSearch = false;
    }

    public void UpdateLockButton() => _lockButton.Visibility = Lock.Enabled ? Visibility.Visible : Visibility.Collapsed;

    public async void LockNow()
    {
        if (!Lock.Enabled) return;
        // The video goes first, so its picture is never left above the lock.
        if (Player.Active) await Player.CloseAsync();
        Lock.LockNow();
        LockScreen.Show();
    }

    /// <summary>Content behind the lock is blurred past reading, not just covered.</summary>
    public void Blur(bool on)
    {
        CloseSuggestions();
        _app.Effect = on ? new BlurEffect { Radius = 40, KernelType = KernelType.Gaussian } : null;
        _app.IsHitTestVisible = !on;
        _app.IsEnabled = !on;
        if (on) HoverPreview.PauseBackdrops();
        else if (!Player.Active) HoverPreview.PlayBackdrops();
    }

    // ------------------------------------------------------------------ scans
    /// <summary>Looks through the storage folders for anything new.</summary>
    public void QuickScan()
    {
        Keyboard.ClearFocus();
        if (Catalog.Locations().Count == 0)
        {
            Toast("Add a folder first");
            Navigate(new Location("settings"));
            return;
        }
        if (!Scanner.ScanAll()) return;
        _quickScan = true;
        Toast("Scanning your folders…");
        _scanPoll.Start();
        PollScan();
    }

    public void WatchScan()
    {
        _scanPoll.Start();
        PollScan();
    }

    void BuildBusy()
    {
        _busy.Background = Theme.Alpha(Theme.Panel2C, 0xF2);
        _busy.BorderBrush = Theme.Line;
        _busy.BorderThickness = new Thickness(1);
        _busy.CornerRadius = new CornerRadius(8);
        _busy.Padding = new Thickness(14, 10, 14, 10);
        _busy.Width = 340;
        _busy.HorizontalAlignment = HorizontalAlignment.Left;
        _busy.VerticalAlignment = VerticalAlignment.Bottom;
        _busy.Margin = new Thickness(24);
        _busy.Visibility = Visibility.Collapsed;
        _busy.IsHitTestVisible = false;
        _busy.Effect = new DropShadowEffect { BlurRadius = 30, Opacity = 0.5, ShadowDepth = 10, Direction = 270 };
        _busyText.Margin = new Thickness(0, 0, 0, 8);
        _busy.Child = Ui.Column(_busyText, _busyBar);
        Panel.SetZIndex(_busy, 70);
    }

    public event Action<ScanState>? ScanTick;

    void PollScan()
    {
        var s = Scanner.State;
        ScanTick?.Invoke(s);
        _scanFace.RenderTransformOrigin = new Point(0.5, 0.5);
        if (s.Busy)
        {
            if (_scanFace.RenderTransform is not RotateTransform)
            {
                var spin = new RotateTransform();
                _scanFace.RenderTransform = spin;
                spin.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });
                _scanFace.Child = Icons.Scan(20, Theme.Ember);
            }
            _busy.Visibility = Here.Kind == "settings" ? Visibility.Collapsed : Visibility.Visible;
            _busyText.Text = s.Running
                ? $"Scanning — {Ui.Plural(s.Found, "file", "files")} seen, {s.Added} added"
                : $"Building thumbnails and previews — {s.AssetsDone} of {s.AssetsTotal}";
            _busyBar.Children.Clear();
            _busyBar.Children.Add(Ui.ProgressBar(s.Running ? 0.35 : (double)s.AssetsDone / Math.Max(1, s.AssetsTotal), 4));
            return;
        }
        _scanPoll.Stop();
        _scanFace.RenderTransform = null;
        _scanFace.Child = Icons.Scan(20, Theme.Muted);
        _busy.Visibility = Visibility.Collapsed;
        if (_quickScan)
        {
            _quickScan = false;
            Toast(s.Error.Length > 0 ? s.Error : s.Added > 0 ? $"Added {Ui.Plural(s.Added, "video", "videos")}" : "No new videos found");
        }
        Images.Clear();
        if (!Player.IsOpen && Here.Kind is "home" or "videos" or "studios" or "stars" or "studio" or "actor" or "video") Refresh(keepScroll: true);
    }

    // ------------------------------------------------------------------ toast
    void BuildToast()
    {
        _toast.Background = Theme.Panel2;
        _toast.BorderBrush = Theme.Ember;
        _toast.BorderThickness = new Thickness(3, 1, 1, 1);
        _toast.CornerRadius = new CornerRadius(8);
        _toast.Padding = new Thickness(18, 11, 18, 11);
        _toast.MaxWidth = 520;
        _toast.HorizontalAlignment = HorizontalAlignment.Center;
        _toast.VerticalAlignment = VerticalAlignment.Bottom;
        _toast.Margin = new Thickness(0, 0, 0, 28);
        _toast.Visibility = Visibility.Collapsed;
        _toast.IsHitTestVisible = false;
        _toast.Effect = new DropShadowEffect { BlurRadius = 40, Opacity = 0.55, ShadowDepth = 12, Direction = 270 };
        _toast.Child = _toastText;
        Panel.SetZIndex(_toast, 300);
    }

    public void Toast(string message, bool bad = false)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => Toast(message, bad));
            return;
        }
        if (Player.IsOpen)
        {
            Player.MirrorToast(message);
            return;
        }
        _toastText.Text = message;
        _toast.BorderBrush = bad ? Theme.Warn : Theme.Ember;
        _toast.Visibility = Visibility.Visible;
        _toast.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // -------------------------------------------------------------- navigation
    public void Navigate(Location where)
    {
        CloseSuggestions();
        if (_trail.Count > 0 && _trail[^1].Where == where)
        {
            Refresh();
            return;
        }
        if (_trail.Count > 0 && _content.Content is ScrollViewer old) _trail[^1] = (_trail[^1].Where, old.VerticalOffset);
        _trail.Add((where, 0));
        if (_trail.Count > 60) _trail.RemoveAt(0);
        _ = Render(where, 0);
    }

    /// <summary>Swaps the current place for another without adding a step to Back.</summary>
    public void Replace(Location where)
    {
        if (_trail.Count > 0) _trail[^1] = (where, 0);
        else _trail.Add((where, 0));
    }

    public void Refresh(bool keepScroll = false)
    {
        var offset = keepScroll && _content.Content is ScrollViewer sv ? sv.VerticalOffset : 0;
        _ = Render(Here, offset);
    }

    public void GoBack()
    {
        if (Player.IsOpen)
        {
            _ = Player.CloseAsync();
            return;
        }
        if (Dialogs.CloseTop()) return;
        if (Lock.Locked || _trail.Count < 2) return;
        _trail.RemoveAt(_trail.Count - 1);
        var (where, offset) = _trail[^1];
        _ = Render(where, offset);
    }

    int _renderToken;

    async Task Render(Location where, double offset)
    {
        var token = ++_renderToken;
        HoverPreview.Stop(null);
        SetActive(where);
        FrameworkElement page;
        try
        {
            page = await Pages.Build(this, where);
        }
        catch (Exception ex)
        {
            App.Log($"Page {where} failed: {ex}");
            page = Ui.Page(Ui.Empty("That did not load", Ui.Button("Back to home", () => Navigate(new Location("home")))));
        }
        if (token != _renderToken) return;
        _content.Content = page;
        _bar.Background = BarFade;
        _bar.BorderBrush = Theme.Clear;
        if (page is ScrollViewer sv)
        {
            Ui.TouchScroll(sv);
            sv.ScrollChanged += (_, _) =>
            {
                var solid = sv.VerticalOffset > 30;
                _bar.Background = solid ? BarSolid : BarFade;
                _bar.BorderBrush = solid ? Theme.LineSoft : Theme.Clear;
            };
            if (offset > 0) _ = sv.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => sv.ScrollToVerticalOffset(offset));
            else sv.ScrollToTop();
        }
    }

    // ----------------------------------------------------------------- keyboard
    static bool IsVideoOverlay(Window w) => w.GetType().Name == "ForegroundWindow";

    void OnAnyWindowKey(object sender, KeyEventArgs e)
    {
        if (sender is not Window w || e.Handled) return;
        if (w is PopOutWindow || (IsVideoOverlay(w) && w.Owner is PopOutWindow))
            Player.PopKey(e);
        else if (ReferenceEquals(w, this) || (IsVideoOverlay(w) && ReferenceEquals(w.Owner, this)))
            OnKey(e);
    }

    void OnKey(KeyEventArgs e)
    {
        if (Lock.Locked)
        {
            LockScreen.HandleKey(e);
            return;
        }
        if (Player.IsOpen)
        {
            Player.HandleKey(e);
            return;
        }
        var typing = Keyboard.FocusedElement is TextBox or PasswordBox;
        if (e.Key == Key.Escape)
        {
            if (Dialogs.CloseTop()) { e.Handled = true; return; }
            if (_suggest.IsOpen) { CloseSuggestions(); e.Handled = true; return; }
            if (Keyboard.FocusedElement == _search && _search.Text.Length > 0) _search.Text = "";
            if (typing) { Keyboard.ClearFocus(); FocusManager.SetFocusedElement(this, this); e.Handled = true; }
            return;
        }
        if (!typing && (e.Key == Key.OemQuestion || e.Key == Key.Divide))
        {
            FocusSearch();
            e.Handled = true;
            return;
        }
        if ((e.Key == Key.System && e.SystemKey == Key.Left && Keyboard.Modifiers == ModifierKeys.Alt) ||
            e.Key == Key.BrowserBack || (e.Key == Key.Back && !typing))
        {
            GoBack();
            e.Handled = true;
        }
    }

    // ----------------------------------------------------------------- playback
    public void Play(long videoId) => _ = Player.OpenAsync(videoId);
}
