using System;
using System.Globalization;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using LibVLCSharp.WPF;
using AdultZone.Core.Data;

namespace AdultZone.Desktop;

/// <summary>
/// The pop-out player: a small borderless window that stays above everything
/// else, laid out like YouTube's picture-in-picture window. Drag it anywhere,
/// pull any edge to resize it. The controls show while the mouse moves over it
/// and fade five seconds after it stops, or as soon as a drag begins.
/// </summary>
public sealed class PopOutWindow : Window
{
    const double Grip = 7;               // how close to an edge counts as the edge
    const double IdleSeconds = 5;

    readonly PlayerView _player;
    readonly Grid _overlay = new() { Background = Theme.HitTarget };
    readonly Grid _chrome = new() { Opacity = 0, IsHitTestVisible = false };
    readonly TextBlock _title = Ui.Text("", 13, Theme.Text, FontWeights.SemiBold);
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Focusable = false };
    readonly TextBlock _time = Ui.Text("", 12.5, Theme.Text);
    readonly Border _playFace = new();
    readonly Button _mute;
    readonly Button _cc;
    Button? _back;
    Button? _fwd;
    int _skip;
    readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(IdleSeconds) };
    readonly TaskCompletionSource<bool> _ready = new();
    Point _lastMouse = new(double.NaN, double.NaN);
    bool _shown;
    bool _seeking;
    bool _closingFromPlayer;

    public VideoView View { get; }
    public Task Ready => _ready.Task;
    public bool Seeking => _seeking;

    public PopOutWindow(PlayerView player, string title, ImageSource? icon, int skip)
    {
        _player = player;
        _skip = skip;
        Title = "Adult Zone · " + title;
        Icon = icon;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        Topmost = true;
        ShowInTaskbar = true;
        Background = Brushes.Black;
        MinWidth = 280;
        MinHeight = 158;
        WindowStartupLocation = WindowStartupLocation.Manual;
        // No frame, but a real resizable window underneath, so Windows does the
        // moving and resizing when asked.
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(Grip),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        PlaceFromLastTime();

        _title.Text = title;
        _title.TextTrimming = TextTrimming.CharacterEllipsis;

        _mute = IconButton(Glyphs.Speaker(false), "Mute", () => player.PopToggleMute());
        _cc = IconButton(Glyphs.Captions(false), "Subtitles", () => player.OpenTrackMenu(_cc!));
        BuildChrome();

        View = new VideoView { Background = Brushes.Black, Content = _overlay };
        View.Loaded += (_, _) => _ready.TrySetResult(true);
        Content = View;

        _idle.Tick += (_, _) => HideChrome();
        _overlay.MouseMove += OnMouseMove;
        _overlay.MouseLeave += (_, _) =>
        {
            _lastMouse = new Point(double.NaN, double.NaN);
            if (!_player.MenuOpen && !_seeking) HideChrome();
        };
        _overlay.MouseLeftButtonDown += OnMouseDown;

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            try
            {
                var round = 2;           // DWMWCP_ROUND: Windows 11 rounds the corners
                DwmSetWindowAttribute(hwnd, 33, ref round, sizeof(int));
                var dark = 1;
                DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
            }
            catch { }
            HwndSource.FromHwnd(hwnd)?.AddHook(MainWindow.PaintVideoHostBlack);
        };
        Closing += (_, e) =>
        {
            SaveBounds();
            if (!_closingFromPlayer)
            {
                // Closed from the taskbar or with Alt+F4: that means stop watching.
                // The player stops the video first, then closes this window itself.
                e.Cancel = true;
                _ = _player.CloseAsync();
                return;
            }
            _idle.Stop();
        };
    }

    /// <summary>Closed by the player itself, which has already dealt with playback.</summary>
    public void CloseFromPlayer()
    {
        _closingFromPlayer = true;
        Close();
    }

    // -------------------------------------------------------------- layout
    void BuildChrome()
    {
        // YouTube darkens the whole picture a little while its controls show.
        _chrome.Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0));
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _chrome.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _chrome.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // top: the name on the left, window buttons on the right
        var top = new DockPanel { Margin = new Thickness(12, 8, 8, 0), LastChildFill = true };
        var buttons = Ui.Row(
            IconButton(Glyphs.Minimise(), "Minimise", () => WindowState = WindowState.Minimized),
            IconButton(Glyphs.BackToApp(), "Back to Adult Zone", () => _ = _player.ReturnFromPopOut()),
            IconButton(Glyphs.Close(), "Close", () => _ = _player.CloseAsync()));
        DockPanel.SetDock(buttons, Dock.Right);
        top.Children.Add(buttons);
        var mark = new Image { Source = Icon, Width = 16, Height = 16 };
        mark.Margin = new Thickness(0, 0, 8, 0);
        mark.VerticalAlignment = VerticalAlignment.Center;
        _title.VerticalAlignment = VerticalAlignment.Center;
        var name = new DockPanel { LastChildFill = true, IsHitTestVisible = false };
        name.Children.Add(mark);
        name.Children.Add(_title);
        top.Children.Add(name);
        _chrome.Children.Add(top);

        // middle: back ten seconds, play or pause, forward ten seconds
        var play = new Button
        {
            Width = 56, Height = 56, Padding = new Thickness(0), Focusable = false,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            ToolTip = "Play",
            Template = RoundTemplate(),
            Background = Theme.Ember,
            Content = _playFace,
        };
        play.Click += (_, _) => _player.TogglePause();
        SetPlaying(true);
        _back = IconButton(Glyphs.Skip(false, _skip, 28), "Back", () => _player.PopSkip(-1), 44);
        _fwd = IconButton(Glyphs.Skip(true, _skip, 28), "Forward", () => _player.PopSkip(1), 44);
        var back = _back;
        var fwd = _fwd;
        back.Margin = new Thickness(0, 0, 22, 0);
        fwd.Margin = new Thickness(22, 0, 0, 0);
        var middle = Ui.Row(back, play, fwd);
        middle.HorizontalAlignment = HorizontalAlignment.Center;
        middle.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(middle, 1);
        _chrome.Children.Add(middle);

        // bottom: the seek bar, then the time, sound and subtitles
        _seek.Margin = new Thickness(0, 0, 0, 2);
        _seek.PreviewMouseLeftButtonDown += (_, _) => _seeking = true;
        _seek.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _seeking = false;
            _player.PopSeek(_seek.Value);
        };
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            _seeking = false;
            _player.PopSeek(_seek.Value);
        }));
        var row = new DockPanel { LastChildFill = false };
        _time.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(_time);
        var right = Ui.Row(_mute);
        DockPanel.SetDock(right, Dock.Right);
        row.Children.Add(right);
        var bottom = Ui.Column(_seek, row);
        bottom.Margin = new Thickness(16, 0, 12, 8);
        Grid.SetRow(bottom, 2);
        _chrome.Children.Add(bottom);

        _overlay.Children.Add(_chrome);
    }

    static ControlTemplate RoundTemplate()
    {
        var template = new ControlTemplate(typeof(Button));
        var ellipse = new FrameworkElementFactory(typeof(Border));
        ellipse.SetValue(Border.CornerRadiusProperty, new CornerRadius(28));
        ellipse.SetValue(Border.BackgroundProperty, new TemplateBindingExtension(BackgroundProperty));
        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        ellipse.AppendChild(presenter);
        template.VisualTree = ellipse;
        return template;
    }

    static Button IconButton(UIElement glyph, string tip, Action click, double size = 32)
    {
        var face = new Border
        {
            Width = size, Height = size, CornerRadius = new CornerRadius(size / 2),
            Background = Theme.Clear, Child = glyph,
        };
        var b = Ui.Bare(face, click, tip);
        b.Focusable = false;
        b.VerticalAlignment = VerticalAlignment.Center;
        b.MouseEnter += (_, _) => face.Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        b.MouseLeave += (_, _) => face.Background = Theme.Clear;
        return b;
    }

    // ------------------------------------------------------- state from player
    public void SetPlaying(bool playing) => _playFace.Child = playing ? Glyphs.Pause(Theme.OnEmber) : Glyphs.Play(Theme.OnEmber);

    public void SetTime(double now, double total)
    {
        if (!_seeking)
        {
            _seek.Maximum = Math.Max(1, total);
            _seek.Value = Math.Min(now, _seek.Maximum);
        }
        _time.Text = total > 0 ? $"{Ui.Clock(_seeking ? _seek.Value : now)} / {Ui.Clock(total)}" : "";
    }

    public void SetSkip(int seconds)
    {
        if (seconds == _skip || _back == null || _fwd == null) return;
        _skip = seconds;
        ((Border)_back.Content).Child = Glyphs.Skip(false, seconds, 28);
        ((Border)_fwd.Content).Child = Glyphs.Skip(true, seconds, 28);
    }

    public void SetMuted(bool silent) => ((Border)_mute.Content).Child = Glyphs.Speaker(silent);

    public void SetCaptions(bool on) => ((Border)_cc.Content).Child = Glyphs.Captions(on);

    public void SetTitle(string title)
    {
        _title.Text = title;
        Title = "Adult Zone · " + title;
    }

    // ------------------------------------------------------ showing the controls
    void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(_overlay);
        _overlay.Cursor = CursorFor(Edge(p));
        // WPF reports a "move" when things change under a still pointer too;
        // only a real move should bring the controls back.
        if (!double.IsNaN(_lastMouse.X) && Math.Abs(p.X - _lastMouse.X) < 1 && Math.Abs(p.Y - _lastMouse.Y) < 1) return;
        _lastMouse = p;
        ShowChrome();
    }

    public void ShowChrome()
    {
        _idle.Stop();
        _idle.Start();
        if (_shown) return;
        _shown = true;
        _chrome.IsHitTestVisible = true;
        _chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(150)));
    }

    public void HideChrome(bool now = false)
    {
        _idle.Stop();
        if (!now && (_player.MenuOpen || _seeking)) { _idle.Start(); return; }
        if (!_shown) return;
        _shown = false;
        _chrome.IsHitTestVisible = false;
        _chrome.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(now ? 80 : 350)));
    }

    // --------------------------------------------------- dragging and resizing
    const int WM_NCLBUTTONDOWN = 0xA1;
    const int HTCAPTION = 2;

    int Edge(Point p)
    {
        var w = _overlay.ActualWidth;
        var h = _overlay.ActualHeight;
        var left = p.X < Grip;
        var right = p.X > w - Grip;
        var top = p.Y < Grip;
        var bottom = p.Y > h - Grip;
        if (top && left) return 13;
        if (top && right) return 14;
        if (bottom && left) return 16;
        if (bottom && right) return 17;
        if (left) return 10;
        if (right) return 11;
        if (top) return 12;
        if (bottom) return 15;
        return HTCAPTION;
    }

    static Cursor? CursorFor(int hit) => hit switch
    {
        10 or 11 => Cursors.SizeWE,
        12 or 15 => Cursors.SizeNS,
        13 or 17 => Cursors.SizeNWSE,
        14 or 16 => Cursors.SizeNESW,
        _ => null,
    };

    void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        // Buttons and the seek bar take their own clicks before this is reached.
        var hit = Edge(e.GetPosition(_overlay));
        if (hit == HTCAPTION) HideChrome(now: true);     // the controls go while it moves
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        e.Handled = true;
        ReleaseCapture();
        // Hands the drag to Windows, exactly as if the frame had been grabbed.
        SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)hit, IntPtr.Zero);
        _lastMouse = new Point(double.NaN, double.NaN);
    }

    // ------------------------------------------------------ where it sits
    void PlaceFromLastTime()
    {
        var area = SystemParameters.WorkArea;
        double w = 480, h = 270, l = area.Right - 480 - 24, t = area.Bottom - 270 - 24;
        var saved = Db.Setting("popout_bounds").Split(',');
        if (saved.Length == 4 &&
            double.TryParse(saved[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var sl) &&
            double.TryParse(saved[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var st) &&
            double.TryParse(saved[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var sw) &&
            double.TryParse(saved[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var sh) &&
            sw >= MinWidth && sh >= MinHeight &&
            sl + sw > SystemParameters.VirtualScreenLeft + 40 && st + 40 > SystemParameters.VirtualScreenTop &&
            sl < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40 &&
            st < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40)
        {
            (l, t, w, h) = (sl, st, sw, sh);
        }
        Left = l;
        Top = t;
        Width = w;
        Height = h;
    }

    void SaveBounds()
    {
        if (WindowState != WindowState.Normal) return;
        Db.SetSetting("popout_bounds", string.Join(",", new[] { Left, Top, Width, Height }
            .Select(v => Math.Round(v).ToString(CultureInfo.InvariantCulture))));
    }

    [DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

