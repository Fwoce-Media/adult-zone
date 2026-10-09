using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using AdultZone.Core.Data;
using AdultZone.Core.Library;
using AdultZone.Core.Media;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace AdultZone.Desktop;

/// <summary>
/// The player. VLC does the playing, so every file plays as it is, with
/// instant seeking. What plays next runs down the right; the video's details
/// sit below; frames follow the mouse along the seek bar.
/// </summary>
public sealed class PlayerView : Grid
{
    readonly MainWindow _win;
    LibVLC? _vlc;
    Task<bool>? _vlcStarting;
    VlcMediaPlayer? _mp;
    VideoView? _view;
    Media? _media;

    // layout
    readonly Grid _left = new();
    readonly Border _videoHost = new() { Background = Brushes.Black };
    readonly Border _side = new();
    readonly ScrollViewer _below = new();
    readonly StackPanel _queueList = new() { Margin = new Thickness(8) };
    readonly ScrollViewer _queueScroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Focusable = false };

    // overlay, drawn over the video
    readonly Grid _overlay = new();
    readonly Border _top = new();
    readonly TextBlock _topTitle = Ui.Text("", 15, Theme.Text, FontWeights.Bold);
    readonly TextBlock _topSub = Ui.Text("", 12, new SolidColorBrush(Color.FromRgb(0xB6, 0xB6, 0xBF)), margin: new Thickness(0, 2, 0, 0));
    readonly Border _controls = new();
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Focusable = false };
    readonly Slider _volume = new() { Minimum = 0, Maximum = 125, Width = 90, Focusable = false, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _time = Ui.Text("", 12.5, new SolidColorBrush(Color.FromRgb(0xCF, 0xCF, 0xD6)));
    readonly Button _playButton;
    readonly Button _prevButton;
    readonly Button _nextButton;
    readonly Button _backButton;
    readonly Button _fwdButton;
    readonly Button _muteButton;
    readonly Button _ccButton;
    readonly Button _gearButton;
    readonly Border _bigPlay = new();
    readonly Border _flash = new();
    readonly TextBlock _flashText = Ui.Text("", 14, Theme.Text, FontWeights.ExtraBold);
    readonly Border _opening = new();
    readonly Border _overlayToast = new();
    readonly TextBlock _overlayToastText = Ui.Text("", 13.5, Theme.Text, wrap: true);
    readonly Border _peek = new() { IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Visibility = Visibility.Collapsed };
    readonly Image _peekImage = new() { Width = 200, Height = 112, Stretch = Stretch.UniformToFill };
    readonly TextBlock _peekTime = Ui.Text("", 12, Theme.Text, FontWeights.SemiBold);

    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };
    readonly DispatcherTimer _saveTimer = new() { Interval = TimeSpan.FromSeconds(8) };
    readonly DispatcherTimer _hideTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    readonly DispatcherTimer _flashTimer = new() { Interval = TimeSpan.FromMilliseconds(700) };
    readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(3.5) };

    // what is playing
    long _videoId;
    Row? _video;
    string _path = "";
    List<Row> _queue = new();
    int _index = -1;
    double _duration;
    bool _seeking;
    bool _fullscreen;
    WindowState _restoreState;
    WindowStyle _restoreStyle;
    TaskCompletionSource<bool>? _viewReady;
    float _rate = 1f;
    CancellationTokenSource? _frames;

    List<(int Id, string Name)> _audioTracks = new();
    List<(int Id, string Name)> _subTracks = new();

    public bool IsOpen => Visibility == Visibility.Visible;
    public bool Active => IsOpen || _pop != null;
    int Skip => Math.Clamp(Db.SettingInt("skip_seconds", 10), 1, 120);

    public PlayerView(MainWindow win)
    {
        _win = win;
        Background = Theme.Ink;
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(360) });

        _playButton = Ctl(Glyphs.Play(), "Play", TogglePause);
        _prevButton = Ctl(Glyphs.Previous(), "Previous", () => _ = GoTo(-1));
        _backButton = Ctl(Glyphs.Skip(false, 10), "Back", () => SkipBy(-Skip));
        _fwdButton = Ctl(Glyphs.Skip(true, 10), "Forward", () => SkipBy(Skip));
        _nextButton = Ctl(Glyphs.Next(), "Next", () => _ = GoTo(1));
        _muteButton = Ctl(Glyphs.Speaker(false), "Mute", ToggleMute);
        _ccButton = Ctl(Glyphs.Captions(false), "Subtitles and audio", () => OpenTrackMenu(_ccButton!));
        _gearButton = Ctl(Glyphs.Gear(), "Speed", () => OpenGearMenu(_gearButton!));

        BuildLeft();
        BuildSide();
        BuildOverlay();

        _tick.Tick += (_, _) => UpdateClock();
        _saveTimer.Tick += (_, _) => SavePosition();
        _hideTimer.Tick += (_, _) => HideControls();
        _flashTimer.Tick += (_, _) => { _flashTimer.Stop(); _flash.Visibility = Visibility.Collapsed; };
        _toastTimer.Tick += (_, _) => { _toastTimer.Stop(); _overlayToast.Visibility = Visibility.Collapsed; };
    }

    /// <summary>.ctl — a 40px square control that lightens on hover.</summary>
    static Button Ctl(FrameworkElement glyph, string tip, Action action)
    {
        var face = new Border { Width = 40, Height = 40, CornerRadius = new CornerRadius(7), Background = Theme.Clear, Child = glyph };
        var b = Ui.Bare(face, action, tip);
        b.Focusable = false;
        b.Margin = new Thickness(0, 0, 6, 0);
        b.MouseEnter += (_, _) => face.Background = Theme.Alpha(Colors.White, 0x21);
        b.MouseLeave += (_, _) => face.Background = Theme.Clear;
        return b;
    }

    static void SetGlyph(Button b, FrameworkElement glyph) => ((Border)b.Content).Child = glyph;

    // ------------------------------------------------------------------ layout
    void BuildLeft()
    {
        _left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _left.RowDefinitions.Add(new RowDefinition { Height = new GridLength(230) });
        _left.Children.Add(_videoHost);
        _below.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        _below.Focusable = false;
        _below.Padding = new Thickness(34, 22, 34, 22);
        Grid.SetRow(_below, 1);
        _left.Children.Add(_below);
        Children.Add(_left);
    }

    void BuildSide()
    {
        _side.Background = new SolidColorBrush(Color.FromRgb(0x0C, 0x0C, 0x10));
        _side.BorderBrush = Theme.Line;
        _side.BorderThickness = new Thickness(1, 0, 0, 0);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.Children.Add(new Border
        {
            Padding = new Thickness(18, 16, 18, 14),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = Ui.Title("Up next", 16, false),
        });
        _queueScroll.Content = _queueList;
        Ui.TouchScroll(_queueScroll);
        Grid.SetRow(_queueScroll, 1);
        grid.Children.Add(_queueScroll);
        _side.Child = grid;
        Grid.SetColumn(_side, 1);
        Children.Add(_side);
    }

    void BuildOverlay()
    {
        // Nearly transparent: a fully clear overlay lets the mouse fall through.
        _overlay.Background = Theme.HitTarget;
        _overlay.MouseMove += (_, _) => ShowControls();
        _overlay.MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource != _overlay) return;
            if (e.ClickCount == 2) ToggleFullscreen();
            else TogglePause();
        };

        var back = Ctl(Glyphs.Back(), "Back", () => _ = CloseAsync());
        back.Margin = new Thickness(0, 0, 14, 0);
        var names = Ui.Column(_topTitle, _topSub);
        names.VerticalAlignment = VerticalAlignment.Center;
        var frame = Ui.Button("Use this frame as thumbnail", () => _ = UseFrame(), small: true);
        frame.Focusable = false;
        frame.VerticalAlignment = VerticalAlignment.Center;
        var topRow = new DockPanel { LastChildFill = true };
        topRow.Children.Add(back);
        DockPanel.SetDock(frame, Dock.Right);
        topRow.Children.Add(frame);
        topRow.Children.Add(names);
        _top.Child = topRow;
        _top.Padding = new Thickness(20, 16, 20, 30);
        _top.VerticalAlignment = VerticalAlignment.Top;
        _top.Background = new LinearGradientBrush(Color.FromArgb(0xCC, 0, 0, 0), Color.FromArgb(0, 0, 0, 0), 90);
        _overlay.Children.Add(_top);

        _bigPlay.Width = _bigPlay.Height = 84;
        _bigPlay.CornerRadius = new CornerRadius(42);
        _bigPlay.Background = Theme.Alpha(Theme.EmberC, 0xEB);
        _bigPlay.Child = Icons.Play(34, Theme.OnEmber).Margin(5, 0, 0, 0);
        _bigPlay.Cursor = Cursors.Hand;
        _bigPlay.Visibility = Visibility.Collapsed;
        _bigPlay.MouseLeftButtonDown += (_, e) => { e.Handled = true; TogglePause(); };
        _overlay.Children.Add(_bigPlay);

        _flash.Background = Theme.Alpha(Colors.Black, 0xAD);
        _flash.CornerRadius = new CornerRadius(40);
        _flash.Padding = new Thickness(20, 14, 20, 14);
        _flash.HorizontalAlignment = HorizontalAlignment.Center;
        _flash.VerticalAlignment = VerticalAlignment.Center;
        _flash.Visibility = Visibility.Collapsed;
        _flash.IsHitTestVisible = false;
        _flash.Child = _flashText;
        _overlay.Children.Add(_flash);

        _opening.Width = _opening.Height = 44;
        _opening.HorizontalAlignment = HorizontalAlignment.Center;
        _opening.VerticalAlignment = VerticalAlignment.Center;
        _opening.IsHitTestVisible = false;
        _opening.Visibility = Visibility.Collapsed;
        _opening.Child = Glyphs.Spinner();
        _overlay.Children.Add(_opening);

        _overlayToast.Background = Theme.Panel2;
        _overlayToast.BorderBrush = Theme.Ember;
        _overlayToast.BorderThickness = new Thickness(3, 1, 1, 1);
        _overlayToast.CornerRadius = new CornerRadius(8);
        _overlayToast.Padding = new Thickness(16, 10, 16, 10);
        _overlayToast.MaxWidth = 460;
        _overlayToast.HorizontalAlignment = HorizontalAlignment.Center;
        _overlayToast.VerticalAlignment = VerticalAlignment.Top;
        _overlayToast.Margin = new Thickness(0, 80, 0, 0);
        _overlayToast.Visibility = Visibility.Collapsed;
        _overlayToast.IsHitTestVisible = false;
        _overlayToast.Child = _overlayToastText;
        _overlay.Children.Add(_overlayToast);

        _controls.VerticalAlignment = VerticalAlignment.Bottom;
        _controls.Padding = new Thickness(18, 30, 18, 14);
        _controls.Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 1), EndPoint = new Point(0, 0),
            GradientStops = { new GradientStop(Color.FromArgb(0xEB, 0, 0, 0), 0.2), new GradientStop(Color.FromArgb(0, 0, 0, 0), 1) },
        };
        var stack = new StackPanel();
        // A click on the bar moves the slider before this sees the press and marks it handled, so it is listened
        // for even when handled; otherwise the clock could pull the slider back before the button comes up.
        _seek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _seeking = true), true);
        _seek.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (!_seeking) return;
            _seeking = false;
            SeekTo(_seek.Value);
        }), true);
        _seek.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler((_, _) =>
        {
            if (!_seeking) return;
            _seeking = false;
            SeekTo(_seek.Value);
        }));
        _seek.MouseMove += (_, e) => Peek(e.GetPosition(_seek).X);
        _seek.MouseLeave += (_, _) => _peek.Visibility = Visibility.Collapsed;
        BuildPeek();
        stack.Children.Add(_seek);

        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 8, 0, 0) };
        _volume.Value = Math.Clamp(Db.SettingInt("volume", 100), 0, 125);
        _volume.ValueChanged += (_, _) =>
        {
            if (_muted && _volume.Value > 0) _muted = false;
            ApplyVolume();
            Db.SetSetting("volume", ((int)_volume.Value).ToString(CultureInfo.InvariantCulture));
        };
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.Margin = new Thickness(12, 0, 10, 0);
        row.Children.Add(Ui.Row(_playButton, _prevButton, _backButton, _fwdButton, _nextButton, _muteButton, _volume, _time));
        var popOut = Ctl(Glyphs.PopOut(), "Pop out", () => _ = PopOut());
        var full = Ctl(Glyphs.FullScreen(), "Full screen", ToggleFullscreen);
        full.Margin = new Thickness(0);
        var rightGroup = Ui.Row(_ccButton, _gearButton, popOut, full);
        DockPanel.SetDock(rightGroup, Dock.Right);
        row.Children.Add(rightGroup);
        stack.Children.Add(row);
        _controls.Child = stack;
        _controls.MouseEnter += (_, _) => _hideTimer.Stop();
        _controls.MouseLeave += (_, _) => RestartHideTimer();
        _overlay.Children.Add(_controls);
        DrawMute();
    }

    // ------------------------------------------------------------- seek frames
    void BuildPeek()
    {
        RenderOptions.SetBitmapScalingMode(_peekImage, BitmapScalingMode.HighQuality);
        var picture = new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Background = Brushes.Black, Child = _peekImage };
        _peekTime.HorizontalAlignment = HorizontalAlignment.Center;
        _peekTime.Margin = new Thickness(0, 6, 0, 0);
        _peek.Child = new Border
        {
            Width = 210,
            Background = Theme.Alpha(Color.FromRgb(0x0A, 0x0A, 0x0D), 0xF2),
            BorderBrush = Theme.Alpha(Colors.White, 0x2E),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4, 4, 4, 6),
            Child = Ui.Column(picture, _peekTime),
        };
        Panel.SetZIndex(_peek, 10);
        _overlay.Children.Add(_peek);
    }

    /// <summary>The frame and time under the mouse, floating over the seek bar.</summary>
    void Peek(double x)
    {
        var total = _duration > 0 ? _duration : (_mp?.Length ?? 0) / 1000.0;
        if (total <= 0 || _seek.ActualWidth <= 0 || !IsOpen) { _peek.Visibility = Visibility.Collapsed; return; }
        var at = Math.Clamp(x / _seek.ActualWidth, 0, 1) * total;
        _peekTime.Text = Ui.Clock(at);
        var frame = Assets.SeekFrame(_path, total, at);
        if (frame != null) _ = ShowPeekFrame(frame);
        _peekImage.Visibility = frame != null ? Visibility.Visible : Visibility.Collapsed;
        // Laid over the video itself, just above the bar, centred on the mouse.
        var at_ = _seek.TranslatePoint(new Point(x, 0), _overlay);
        const double width = 210;
        var height = frame != null ? 150 : 40;
        var left = Math.Clamp(at_.X - width / 2, 8, Math.Max(8, _overlay.ActualWidth - width - 8));
        _peek.Margin = new Thickness(left, Math.Max(0, at_.Y - height - 10), 0, 0);
        _peek.Visibility = Visibility.Visible;
        _hideTimer.Stop();
    }

    string _peekShown = "";

    async Task ShowPeekFrame(string file)
    {
        if (file == _peekShown) return;
        _peekShown = file;
        var bmp = await Images.LoadAsync(file, 240);
        if (bmp != null && _peekShown == file) _peekImage.Source = bmp;
    }

    void MakeFrames(string path, double duration)
    {
        _frames?.Cancel();
        if (duration <= 0 || Assets.HasSeekFrames(path)) return;
        var cancel = new CancellationTokenSource();
        _frames = cancel;
        _ = Task.Run(() =>
        {
            try { Assets.MakeSeekFrames(path, duration, cancel.Token); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.Log("Seek frames: " + ex.Message); }
        });
    }

    // -------------------------------------------------------------- VLC set-up
    /// <summary>VLC loads a few hundred plugins when it starts; that happens once, off the UI thread.</summary>
    public Task<bool> EnsureVlcAsync() => _vlcStarting ??= StartVlcAsync();

    async Task<bool> StartVlcAsync()
    {
        var volume = EffectiveVolume;
        try
        {
            var (vlc, mp) = await Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                // Sound is decoded here rather than passed through, so AC3 or DTS
                // never plays silent on a device that cannot decode it.
                LibVLC v;
                try
                {
                    v = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=any",
                                   "--no-spdif", "--mmdevice-passthrough=0");
                }
                catch (Exception ex)
                {
                    App.Log("VLC refused the sound options, starting without them: " + ex.Message);
                    v = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=any");
                }
                var m = new VlcMediaPlayer(v)
                {
                    EnableHardwareDecoding = true,
                    EnableKeyInput = false,
                    EnableMouseInput = false,
                    Volume = volume,
                };
                return (v, m);
            });
            _vlc = vlc;
            _mp = mp;
            _mp.Playing += (_, _) => Dispatcher.BeginInvoke(OnPlaying);
            _mp.Paused += (_, _) => Dispatcher.BeginInvoke(() => SetPlayGlyph(false));
            _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(OnEnded);
            _mp.EncounteredError += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                ShowOpening(false);
                Notify("This file could not be played.", true);
            });
            _mp.LengthChanged += (_, e) => Dispatcher.BeginInvoke(() =>
            {
                if (e.Length > 0) _duration = e.Length / 1000.0;
            });
            _mp.ESAdded += (_, _) => Dispatcher.BeginInvoke(RefreshTracks);
            return true;
        }
        catch (Exception ex)
        {
            App.Log("VLC failed to start: " + ex);
            _win.Toast("The video player could not start: " + ex.Message, true);
            _vlc = null;
            _mp = null;
            _vlcStarting = null;
            return false;
        }
    }

    void ShowOpening(bool on) => _opening.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A fresh video surface each time the player opens; the controls' overlay window goes with it.</summary>
    Task EnsureView()
    {
        if (_view != null) return _viewReady?.Task ?? Task.CompletedTask;
        _viewReady = new TaskCompletionSource<bool>();
        var ready = _viewReady;
        DetachOverlay();
        var view = new VideoView { Background = Brushes.Black, Content = _overlay };
        view.Loaded += (_, _) =>
        {
            view.MediaPlayer = _mp;
            ready.TrySetResult(true);
        };
        _view = view;
        _videoHost.Child = view;
        return ready.Task;
    }

    void DropView()
    {
        var view = _view;
        _view = null;
        _videoHost.Child = null;
        if (view == null) return;
        try
        {
            view.MediaPlayer = null;
            view.Dispose();
        }
        catch (Exception ex) { App.Log("Video surface: " + ex.Message); }
        DetachOverlay();
    }

    void DetachOverlay()
    {
        switch (_overlay.Parent)
        {
            case Panel panel: panel.Children.Remove(_overlay); break;
            case ContentControl host: host.Content = null; break;
            case Decorator decorator: decorator.Child = null; break;
        }
    }

    // ----------------------------------------------------------------- opening
    public async Task OpenAsync(long videoId)
    {
        HoverPreview.Stop(null);
        HoverPreview.PauseBackdrops();
        if (_pop != null)
        {
            var playing = _mp;
            if (playing != null) await Task.Run(() => playing.Stop());
            ClosePop();
        }
        Visibility = Visibility.Visible;
        if (_vlc == null)
            _videoHost.Child = new Border { Width = 44, Height = 44, Child = Glyphs.Spinner() };
        if (!await EnsureVlcAsync())
        {
            _videoHost.Child = null;
            Visibility = Visibility.Collapsed;
            return;
        }
        if (!IsOpen) return;
        if (_view == null) _videoHost.Child = null;
        await EnsureView();
        _queue = new();
        await PlayVideo(videoId, null);
        Focus();
    }

    /// <summary>Opens a video, from where it was left unless a start is given.</summary>
    async Task PlayVideo(long videoId, double? position)
    {
        if (_mp == null || _vlc == null) return;
        var keepQueue = _queue.Any(q => q.Long("id") == videoId);
        var loaded = await Task.Run(() =>
        {
            var v = Catalog.Video(videoId);
            if (v == null) return null;
            Catalog.RegisterView(videoId);
            return new { Video = v, Queue = keepQueue ? null : Catalog.UpNext(videoId) };
        });
        if (loaded == null)
        {
            _win.Toast("That video is no longer in the library.", true);
            await CloseAsync();
            return;
        }
        var path = loaded.Video.Str("path");
        if (!File.Exists(path))
        {
            _win.Toast("File not found: " + path, true);
            if (_videoId == 0) await CloseAsync();
            return;
        }

        SavePosition();
        _videoId = videoId;
        _video = loaded.Video;
        _path = path;
        if (loaded.Queue != null)
        {
            // What plays next is worked out from the first video opened, and kept while moving along it.
            _queue = new List<Row> { loaded.Video };
            _queue.AddRange(loaded.Queue);
        }
        _index = _queue.FindIndex(e => e.Long("id") == videoId);
        _duration = loaded.Video.Double("duration") ?? 0;

        var saved = loaded.Video.Double("position") ?? 0;
        var start = position ?? (saved > 5 && (_duration <= 0 || saved < _duration - 10) ? saved : 0);

        DrawTitles();
        _pop?.SetTitle(PopTitle());

        _media?.Dispose();
        _media = new Media(_vlc, path, FromType.FromPath);
        if (start > 1) _media.AddOption($":start-time={start.ToString("F2", CultureInfo.InvariantCulture)}");

        ShowOpening(true);
        var mp = _mp;
        var clip = _media;
        var volume = EffectiveVolume;
        var rate = _rate;
        await Task.Run(() =>
        {
            mp.Mute = false;
            mp.Volume = volume;
            mp.Play(clip);
            if (Math.Abs(rate - 1f) > 0.01f) mp.SetRate(rate);
        });

        _audioTracks.Clear();
        _subTracks.Clear();
        DrawCc();
        DrawTransport();
        DrawQueue();
        DrawBelow();
        MakeFrames(path, _duration);
        _tick.Start();
        _saveTimer.Start();
        ShowControls();
    }

    void DrawTitles()
    {
        if (_video == null) return;
        _topTitle.Text = _video.Str("title");
        var sub = string.Join(" · ", new[] { _video.Str("studio_name"), string.Join(", ", Catalog.Cast(_video).Select(a => a.Str("name"))) }.Where(s => s.Length > 0));
        _topSub.Text = sub;
        _topSub.Visibility = sub.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnPlaying()
    {
        ShowOpening(false);
        SetPlayGlyph(true);
        DrawMute();
        SyncAudio();
        if (_pauseWhenPlaying)
        {
            _pauseWhenPlaying = false;
            _mp?.SetPause(true);
        }
        if (_mp != null && _mp.Length > 0)
        {
            var had = _duration;
            _duration = _mp.Length / 1000.0;
            if (had <= 0) MakeFrames(_path, _duration);
        }
        RefreshTracks();
        RestartHideTimer();
    }

    // ------------------------------------------------------ audio and subtitles
    static readonly Regex Bracketed = new(@"\[([^\]]+)\]", RegexOptions.CultureInvariant);

    /// <summary>VLC names tracks "Track 1 - [English]"; this keeps what matters.</summary>
    static string TidyTrackName(string name)
    {
        var m = Bracketed.Match(name);
        var lang = m.Success ? m.Groups[1].Value.Trim() : "";
        var head = Bracketed.Replace(name, "").Trim(' ', '-');
        if (Regex.IsMatch(head, @"^Track \d+$") && lang.Length > 0) return lang;
        if (lang.Length > 0 && !head.Contains(lang, StringComparison.OrdinalIgnoreCase)) return $"{lang} · {head}";
        return head.Length > 0 ? head : name;
    }

    void RefreshTracks()
    {
        if (_mp == null) return;
        _audioTracks = _mp.AudioTrackDescription.Where(t => t.Id >= 0).Select(t => (t.Id, TidyTrackName(t.Name ?? $"Track {t.Id}"))).ToList();
        _subTracks = _mp.SpuDescription.Where(t => t.Id >= 0).Select(t => (t.Id, TidyTrackName(t.Name ?? $"Track {t.Id}"))).ToList();
        DrawCc();
    }

    /// <summary>Changes the audio track; setting the position again makes VLC restart its sound for it.</summary>
    void SwitchAudio(int id)
    {
        var mp = _mp;
        if (mp == null || mp.AudioTrack == id) return;
        mp.SetAudioTrack(id);
        var videoId = _videoId;
        _ = Task.Delay(250).ContinueWith(_ =>
        {
            if (videoId != _videoId || _mp != mp) return;
            try { if (mp.IsSeekable && mp.Time > 0) mp.Time = mp.Time; }
            catch (Exception ex) { App.Log("Audio switch: " + ex.Message); }
            Dispatcher.BeginInvoke(SyncAudio);
        });
    }

    void SetSubtitle(int id)
    {
        _mp?.SetSpu(id);
        DrawCc();
    }

    void ToggleSubtitles()
    {
        if (_mp == null || _subTracks.Count == 0) return;
        if (_mp.Spu >= 0)
        {
            SetSubtitle(-1);
            Flash("Subtitles off");
            return;
        }
        SetSubtitle(_subTracks[0].Id);
        Flash(_subTracks[0].Name);
    }

    void CycleAudio()
    {
        if (_mp == null || _audioTracks.Count < 2) return;
        var i = _audioTracks.FindIndex(t => t.Id == _mp.AudioTrack);
        var next = _audioTracks[(i + 1) % _audioTracks.Count];
        SwitchAudio(next.Id);
        Flash(next.Name);
        DrawCc();
    }

    // -------------------------------------------------------------- transport
    public void TogglePause()
    {
        if (_mp == null) return;
        if (_mp.IsPlaying) _mp.SetPause(true);
        else if (_mp.State == VLCState.Ended && _videoId != 0) _ = PlayVideo(_videoId, 0);
        else _mp.SetPause(false);
    }

    void SetPlayGlyph(bool playing)
    {
        SetGlyph(_playButton, playing ? Glyphs.Pause() : Glyphs.Play());
        _playButton.ToolTip = playing ? "Pause" : "Play";
        _bigPlay.Visibility = playing ? Visibility.Collapsed : Visibility.Visible;
        _pop?.SetPlaying(playing);
        if (!playing) ShowControls(stay: true);
    }

    void SkipBy(int seconds)
    {
        if (_mp == null) return;
        SeekTo(Now() + seconds);
        Flash(seconds > 0 ? $"+{seconds}s" : $"−{-seconds}s", seconds < 0 ? HorizontalAlignment.Left : HorizontalAlignment.Right);
    }

    double Now() => _mp != null ? Math.Max(0, _mp.Time / 1000.0) : 0;

    void SeekTo(double seconds)
    {
        if (_mp == null) return;
        var total = _duration > 0 ? _duration : _mp.Length / 1000.0;
        var target = Math.Clamp(seconds, 0, Math.Max(0, total - 1));
        _mp.Time = (long)(target * 1000);
        // VLC takes a moment to get there; until it does, the bar and clock show where it is going, not where it was.
        _heldAt = target;
        _heldUntil = DateTime.UtcNow.AddSeconds(2.5);
        UpdateClock();
    }

    double _heldAt;
    DateTime _heldUntil;

    /// <summary>Where the video is, or where it was just sent while it gets there.</summary>
    double Shown()
    {
        var now = Now();
        if (DateTime.UtcNow < _heldUntil && Math.Abs(now - _heldAt) > 1.5) return _heldAt;
        _heldUntil = DateTime.MinValue;
        return now;
    }

    void SetRate(float rate)
    {
        _rate = rate;
        _mp?.SetRate(rate);
        Flash(rate.ToString("0.##", CultureInfo.InvariantCulture) + "×");
    }

    // Muting is volume 0 rather than VLC's mute, which VLC resets on every new file.
    bool _muted;

    void ToggleMute()
    {
        _muted = !_muted;
        ApplyVolume();
        Flash(_muted ? "Muted" : "Sound on");
    }

    int EffectiveVolume => _muted ? 0 : (int)_volume.Value;

    void ApplyVolume()
    {
        if (_mp != null)
        {
            _mp.Mute = false;
            _mp.Volume = EffectiveVolume;
        }
        DrawMute();
    }

    /// <summary>Windows remembers a per-app volume and mute; once sound starts, the slider's level is put back.</summary>
    async void SyncAudio()
    {
        foreach (var wait in new[] { 0, 500, 1500 })
        {
            if (wait > 0) await Task.Delay(wait);
            var mp = _mp;
            if (mp == null || !Active) return;
            var volume = EffectiveVolume;
            try
            {
                await Task.Run(() =>
                {
                    mp.Mute = false;
                    mp.Volume = volume;
                });
            }
            catch (Exception ex) { App.Log("Sound: " + ex.Message); }
        }
    }

    void DrawMute()
    {
        var silent = _muted || _volume.Value <= 0;
        _volume.Opacity = _muted ? 0.35 : 1;
        SetGlyph(_muteButton, Glyphs.Speaker(silent));
        _muteButton.ToolTip = _muted ? "Unmute" : "Mute";
        _pop?.SetMuted(silent);
    }

    void UpdateClock()
    {
        if (_mp == null) return;
        var total = _duration > 0 ? _duration : _mp.Length / 1000.0;
        var now = Shown();
        if (!_seeking)
        {
            _seek.Maximum = Math.Max(1, total);
            _seek.Value = Math.Min(now, _seek.Maximum);
        }
        _time.Text = total > 0 ? $"{Ui.Clock(_seeking ? _seek.Value : now)} / {Ui.Clock(total)}" : "";
        _pop?.SetTime(now, total);
    }

    void Flash(string text, HorizontalAlignment where = HorizontalAlignment.Center)
    {
        _flashText.Text = text;
        _flash.HorizontalAlignment = where;
        _flash.Margin = where == HorizontalAlignment.Center ? new Thickness(0) : new Thickness(where == HorizontalAlignment.Left ? 120 : 0, 0, where == HorizontalAlignment.Right ? 120 : 0, 0);
        _flash.Visibility = Visibility.Visible;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    /// <summary>Messages show inside the video, where the main window's would be hidden.</summary>
    public void MirrorToast(string message)
    {
        if (!IsOpen) return;
        _overlayToastText.Text = message;
        _overlayToast.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    void Notify(string message, bool bad = false) => _win.Toast(message, bad);

    void ShowControls(bool stay = false)
    {
        _controls.Visibility = Visibility.Visible;
        _top.Visibility = Visibility.Visible;
        _overlay.Cursor = null;
        if (stay) _hideTimer.Stop();
        else RestartHideTimer();
    }

    void RestartHideTimer()
    {
        _hideTimer.Stop();
        if (_mp?.IsPlaying == true) _hideTimer.Start();
    }

    void HideControls()
    {
        _hideTimer.Stop();
        if (_mp?.IsPlaying != true || _controls.IsMouseOver || _top.IsMouseOver || _seeking || MenuOpen) return;
        _controls.Visibility = Visibility.Collapsed;
        _top.Visibility = Visibility.Collapsed;
        _peek.Visibility = Visibility.Collapsed;
        _overlay.Cursor = Cursors.None;
    }

    public void ToggleFullscreen()
    {
        _fullscreen = !_fullscreen;
        if (_fullscreen)
        {
            _restoreState = _win.WindowState;
            _restoreStyle = _win.WindowStyle;
            _win.WindowStyle = WindowStyle.None;
            if (_win.WindowState == WindowState.Maximized) _win.WindowState = WindowState.Normal;
            _win.WindowState = WindowState.Maximized;
            _below.Visibility = Visibility.Collapsed;
            _side.Visibility = Visibility.Collapsed;
            _left.RowDefinitions[1].Height = new GridLength(0);
            Grid.SetColumnSpan(_left, 2);
        }
        else
        {
            _win.WindowStyle = _restoreStyle;
            _win.WindowState = WindowState.Normal;
            _win.WindowState = _restoreState;
            _below.Visibility = Visibility.Visible;
            _side.Visibility = Visibility.Visible;
            _left.RowDefinitions[1].Height = new GridLength(230);
            Grid.SetColumnSpan(_left, 1);
        }
        ShowControls();
    }

    // ------------------------------------------------------ thumbnail from frame
    async Task UseFrame()
    {
        if (_mp == null || _videoId == 0) return;
        var id = _videoId;
        var path = _path;
        var at = Now();
        var name = await Task.Run(() => Assets.ThumbFromTime(path, at));
        if (name == null)
        {
            Notify("ffmpeg could not read that frame", true);
            return;
        }
        Catalog.SetThumb(id, name, false);
        Images.Clear();
        Notify("Thumbnail updated from this frame");
    }

    // --------------------------------------------------------------- the queue
    Row? Neighbour(int step)
    {
        var i = _index + step;
        return _index >= 0 && i >= 0 && i < _queue.Count ? _queue[i] : null;
    }

    async Task GoTo(int step)
    {
        var target = Neighbour(step);
        if (target == null) return;
        await PlayVideo(target.Long("id") ?? 0, null);
    }

    void DrawTransport()
    {
        _prevButton.IsEnabled = Neighbour(-1) != null;
        _nextButton.IsEnabled = Neighbour(1) != null;
        var skip = Skip;
        SetGlyph(_backButton, Glyphs.Skip(false, skip));
        SetGlyph(_fwdButton, Glyphs.Skip(true, skip));
        _pop?.SetSkip(skip);
    }

    void DrawQueue()
    {
        _queueList.Children.Clear();
        FrameworkElement? current = null;
        foreach (var v in _queue)
        {
            var id = v.Long("id") ?? 0;
            var on = id == _videoId;
            var element = Mini(v, on, () => { if (id != _videoId) _ = PlayVideo(id, null); });
            if (on) current = element;
            _queueList.Children.Add(element);
        }
        if (current != null) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () => current.BringIntoView());
    }

    /// <summary>A compact row in the side list: the still, the title, the studio.</summary>
    static Button Mini(Row v, bool current, Action open)
    {
        var picture = new Grid { Width = 120, Height = 68, ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top };
        var face = new Border { CornerRadius = new CornerRadius(5), Background = Theme.Panel3 };
        picture.Children.Add(face);
        var thumb = Catalog.ThumbPath(v);
        if (thumb.Length > 0 && File.Exists(thumb)) Ui.Cover(face, thumb, decode: 240, fade: true);
        if (Ui.Duration(v.Double("duration")) is { Length: > 0 } length)
            picture.Children.Add(new Border
            {
                Background = Theme.Alpha(Color.FromRgb(4, 4, 6), 0xDB), CornerRadius = new CornerRadius(3), Padding = new Thickness(4, 1, 4, 1),
                Margin = new Thickness(4), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Bottom,
                Child = new TextBlock { Text = length, FontFamily = Theme.Mono, FontSize = 10, Foreground = Theme.Text },
            });
        var body = new StackPanel { Margin = new Thickness(11, 0, 0, 0) };
        body.Children.Add(Ui.Clamp(v.Str("title"), 13, Theme.Text, 2, 17.5, FontWeights.SemiBold));
        var sub = string.Join(" · ", new[] { v.Str("studio_name"), Catalog.Cast(v).FirstOrDefault()?.Str("name") ?? "" }.Where(s => s.Length > 0));
        if (sub.Length > 0) body.Children.Add(Ui.Text(sub, 11.5, Theme.Muted, margin: new Thickness(0, 4, 0, 0)));
        if (current) body.Children.Add(Ui.Text("Now playing", 10.5, Theme.EmberHi, margin: new Thickness(0, 4, 0, 0)));
        var dock = new DockPanel { LastChildFill = true };
        dock.Children.Add(picture);
        dock.Children.Add(body);
        var card = new Border
        {
            Padding = new Thickness(9), CornerRadius = new CornerRadius(8), Margin = new Thickness(0, 0, 0, 4),
            BorderThickness = new Thickness(1),
            Background = current ? Theme.EmberWash : Theme.Clear,
            BorderBrush = current ? Theme.Ember : Theme.Clear,
            Child = dock,
        };
        var b = Ui.Bare(card, open);
        b.Focusable = false;
        b.Cursor = Cursors.Hand;
        if (!current)
        {
            b.MouseEnter += (_, _) => card.Background = Theme.Panel;
            b.MouseLeave += (_, _) => card.Background = Theme.Clear;
        }
        return b;
    }

    /// <summary>Below the picture: the title, its facts, cast and tags, the description.</summary>
    void DrawBelow()
    {
        if (_video == null) return;
        var v = _video;
        var stack = new StackPanel();
        var head = new WrapPanel();
        if (v.Str("studio_name") is { Length: > 0 } studio)
        {
            var eyebrow = Ui.Eyebrow(studio);
            eyebrow.VerticalAlignment = VerticalAlignment.Center;
            eyebrow.Margin = new Thickness(0, 0, 10, 0);
            head.Children.Add(eyebrow);
        }
        if (v.Str("subsite") is { Length: > 0 } site)
            head.Children.Add(new Pill
            {
                Background = Theme.Alpha(Theme.SubsiteC, 0x1F), BorderBrush = Theme.Alpha(Theme.SubsiteC, 0x57), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 3, 10, 3), Child = Ui.Caps(site, 11, Theme.Subsite, 0.06),
            });
        if (head.Children.Count > 0) stack.Children.Add(head);
        stack.Children.Add(Ui.Title(v.Str("title"), 24).Margin(0, 6, 0, 10));
        stack.Children.Add(Ui.Facts(new UIElement?[]
        {
            v.Str("release_date") is { Length: > 0 } rd ? Ui.Text(Ui.Date(rd)) : null,
            Catalog.Quality(v) is { Length: > 0 } q ? Ui.Quality(q) : null,
            Ui.Duration(v.Double("duration")) is { Length: > 0 } d ? Ui.Text(d) : null,
            Ui.Text(Ui.Views((v.Long("views") ?? 0) + 1)),
        }));
        var chips = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var t in Catalog.TagsOf(v).Where(t => !string.Equals(t, v.Str("subsite"), StringComparison.OrdinalIgnoreCase)))
            chips.Children.Add(TagButton(t));
        if (chips.Children.Count > 0) stack.Children.Add(chips);
        // The cast's profiles, above the description.
        var cast = Catalog.Cast(v);
        if (cast.Count > 0)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 12, 0, 4) };
            foreach (var a in cast) row.Children.Add(Profile(a));
            stack.Children.Add(row);
        }
        if (v.Str("description") is { Length: > 0 } desc)
        {
            var text = Ui.Text(desc, 14.5, Theme.Body, wrap: true, margin: new Thickness(0, 8, 0, 0));
            text.LineHeight = 23;
            text.MaxWidth = 900;
            text.HorizontalAlignment = HorizontalAlignment.Left;
            stack.Children.Add(text);
        }
        _below.Content = stack;
        _below.ScrollToTop();
    }

    /// <summary>Whole years between a date of birth and a release date; null when either is missing or they make no sense.</summary>
    public static int? AgeAt(string birthdate, string released)
    {
        if (birthdate.Length < 10 || released.Length < 10) return null;
        if (!DateTime.TryParse(birthdate[..10], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var born)) return null;
        if (!DateTime.TryParse(released[..10], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var then)) return null;
        var age = then.Year - born.Year;
        if (then < born.AddYears(age)) age--;
        return age is >= 18 and < 100 ? age : null;
    }

    /// <summary>A performer's portrait and name; opens their page.</summary>
    FrameworkElement Profile(Row a)
    {
        var id = a.Long("id") ?? 0;
        var art = Cards.Portrait(a, 22);
        art.SizeChanged += (_, e) => art.Clip = new RectangleGeometry(new Rect(e.NewSize), 6, 6);
        var frame = new Border
        {
            Width = 112, Height = Math.Round(112 * 600 / 435.0), CornerRadius = new CornerRadius(7),
            BorderBrush = Theme.LineSoft, BorderThickness = new Thickness(1), Background = Theme.Panel, Child = art,
        };
        var nameText = Ui.Text(a.Str("name"), 12.5, Theme.Text, FontWeights.SemiBold);
        var name = new DockPanel { LastChildFill = true, Width = 112, Margin = new Thickness(0, 7, 0, 0) };
        if (Gender.Mark(a.Str("gender"), 15) is { } mark)
        {
            mark.Margin = new Thickness(5, 0, 0, 0);
            DockPanel.SetDock(mark, Dock.Right);
            name.Children.Add(mark);
        }
        name.Children.Add(nameText);
        var column = Ui.Column(frame, name);
        // How old they were when this was released, from their date of birth.
        if (AgeAt(a.Str("birthdate"), _video?.Str("release_date") ?? "") is { } age)
            column.Children.Add(Ui.Text($"{age} in this scene", 11.5, Theme.Muted, margin: new Thickness(0, 2, 0, 0)));
        var b = Ui.Bare(column, async () =>
        {
            await CloseAsync();
            _win.Navigate(new Location("actor", id));
        });
        b.Focusable = false;
        b.Cursor = Cursors.Hand;
        b.Margin = new Thickness(0, 0, 14, 10);
        b.VerticalAlignment = VerticalAlignment.Top;
        b.MouseEnter += (_, _) => frame.BorderBrush = Theme.Ember;
        b.MouseLeave += (_, _) => frame.BorderBrush = Theme.LineSoft;
        return b;
    }

    /// <summary>A tag that closes the player and lists every video with it.</summary>
    Button TagButton(string tag)
    {
        var face = Chip(tag, true);
        var b = Ui.Bare(face, async () =>
        {
            await CloseAsync();
            _win.Navigate(new Location("videos", Tag: tag));
        });
        b.Focusable = false;
        b.Cursor = Cursors.Hand;
        b.MouseEnter += (_, _) => { face.BorderBrush = Theme.Ember; face.Background = Theme.EmberWash; };
        b.MouseLeave += (_, _) => { face.BorderBrush = Theme.Line; face.Background = Theme.Alpha(Colors.White, 0x12); };
        return b;
    }

    static Pill Chip(string text, bool tag = false) => new()
    {
        Background = Theme.Alpha(Colors.White, 0x12), BorderBrush = Theme.Line, BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(0, 0, 7, 7),
        Child = Ui.Text(text, 12, tag ? Theme.Muted : Theme.Text),
    };

    // --------------------------------------------------------- position, ending
    void SavePosition()
    {
        if (_mp == null || _videoId == 0) return;
        var total = _duration > 0 ? _duration : _mp.Length / 1000.0;
        var at = _mp.State == VLCState.Ended ? 0 : Now();
        // Near the end counts as finished: next time it starts from the top.
        if (total > 0 && at > total - 10) at = 0;
        var id = _videoId;
        _ = Task.Run(() =>
        {
            try { Catalog.SavePosition(id, at); }
            catch (Exception ex) { App.Log("Saving position: " + ex.Message); }
        });
    }

    void OnEnded()
    {
        SetPlayGlyph(false);
        SavePosition();
    }

    public async Task CloseAsync()
    {
        if (!Active) return;
        if (_fullscreen) ToggleFullscreen();
        SavePosition();
        _frames?.Cancel();
        _peek.Visibility = Visibility.Collapsed;
        _tick.Stop();
        _saveTimer.Stop();
        _hideTimer.Stop();
        ShowOpening(false);
        var mp = _mp;
        if (mp != null) await Task.Run(() => mp.Stop());
        DropView();
        ClosePop();
        _media?.Dispose();
        _media = null;
        var watched = _videoId;
        _videoId = 0;
        _queue = new();
        _video = null;
        Visibility = Visibility.Collapsed;
        // Leaving the player lands on the video's own page.
        var page = new Location("video", watched);
        if (watched != 0 && _win.Here != page) _win.Navigate(page);
        else _win.Refresh(keepScroll: true);
    }

    public void Shutdown()
    {
        try
        {
            SavePosition();
            _frames?.Cancel();
            _mp?.Stop();
            DropView();
            ClosePop();
            _media?.Dispose();
            _mp?.Dispose();
            _vlc?.Dispose();
        }
        catch { }
    }

    // ------------------------------------------------------------------ menus
    public bool MenuOpen { get; private set; }

    void DrawCc()
    {
        var on = _mp != null && _mp.Spu >= 0 && _subTracks.Count > 0;
        SetGlyph(_ccButton, Glyphs.Captions(on));
        _ccButton.Visibility = _subTracks.Count > 0 || _audioTracks.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _pop?.SetCaptions(on);
    }

    Popup Menu(FrameworkElement anchor, Action<StackPanel, Popup> fill)
    {
        var list = new StackPanel();
        var popup = new Popup
        {
            PlacementTarget = anchor,
            Placement = PlacementMode.Top,
            StaysOpen = false,
            AllowsTransparency = true,
            VerticalOffset = -10,
            PopupAnimation = PopupAnimation.Fade,
        };
        fill(list, popup);
        popup.Child = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x10, 0x16)),
            BorderBrush = Theme.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(6),
            MinWidth = 210,
            Child = new ScrollViewer { Content = list, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };
        popup.Closed += (_, _) =>
        {
            MenuOpen = false;
            RestartHideTimer();
            _pop?.ShowChrome();
        };
        MenuOpen = true;
        _hideTimer.Stop();
        popup.IsOpen = true;
        return popup;
    }

    static void MenuLabel(StackPanel list, string text) =>
        list.Children.Add(Ui.Caps(text, 10.5, Theme.Faint, 0.13, FontWeights.Bold).Margin(12, 8, 12, 4));

    static void MenuItem(StackPanel list, Popup popup, string text, bool on, Action pick)
    {
        var label = Ui.Text(text, 13, on ? Theme.EmberHi : Theme.Text, on ? FontWeights.Bold : FontWeights.Normal);
        var row = new DockPanel { LastChildFill = true };
        if (on)
        {
            var tick = Icons.Check(13, Theme.EmberHi);
            tick.Margin = new Thickness(12, 0, 0, 0);
            DockPanel.SetDock(tick, Dock.Right);
            row.Children.Add(tick);
        }
        row.Children.Add(label);
        var face = new Border { Padding = new Thickness(12, 9, 12, 9), CornerRadius = new CornerRadius(6), Background = Theme.Clear, Child = row };
        var b = Ui.Bare(face, () =>
        {
            popup.IsOpen = false;
            pick();
        });
        b.Focusable = false;
        b.MouseEnter += (_, _) => face.Background = Theme.Alpha(Colors.White, 0x17);
        b.MouseLeave += (_, _) => face.Background = Theme.Clear;
        list.Children.Add(b);
    }

    public void OpenTrackMenu(FrameworkElement anchor)
    {
        if (_mp == null) return;
        RefreshTracks();
        Menu(anchor, (list, popup) =>
        {
            if (_subTracks.Count > 0)
            {
                MenuLabel(list, "Subtitles");
                MenuItem(list, popup, "Off", _mp.Spu < 0, () => SetSubtitle(-1));
                foreach (var (id, name) in _subTracks)
                    MenuItem(list, popup, name, _mp.Spu == id, () => SetSubtitle(id));
            }
            if (_audioTracks.Count > 1)
            {
                MenuLabel(list, "Audio");
                foreach (var (id, name) in _audioTracks)
                    MenuItem(list, popup, name, _mp.AudioTrack == id, () => { SwitchAudio(id); DrawCc(); });
            }
        });
    }

    void OpenGearMenu(FrameworkElement anchor)
    {
        Menu(anchor, (list, popup) =>
        {
            MenuLabel(list, "Playback speed");
            foreach (var r in new[] { 0.5f, 0.75f, 1f, 1.25f, 1.5f, 2f })
                MenuItem(list, popup, r.ToString("0.##", CultureInfo.InvariantCulture) + "×", Math.Abs(_rate - r) < 0.01f, () => SetRate(r));
        });
    }

    // ----------------------------------------------------------------- pop-out
    PopOutWindow? _pop;
    bool _pauseWhenPlaying;

    string PopTitle() => _video?.Str("title") ?? "";

    /// <summary>Moves playback into the pop-out window, picking the file up there from the same second.</summary>
    async Task PopOut()
    {
        if (_pop != null || _mp == null || _videoId == 0) return;
        if (_fullscreen) ToggleFullscreen();
        var mp = _mp;
        var id = _videoId;
        var at = Now();
        _pauseWhenPlaying = !mp.IsPlaying;
        SavePosition();
        _peek.Visibility = Visibility.Collapsed;

        var pop = new PopOutWindow(this, PopTitle(), _win.Icon, Skip);
        _pop = pop;
        pop.Show();
        await pop.Ready;
        await Task.Run(() => mp.Stop());
        DropView();
        pop.View.MediaPlayer = mp;
        foreach (Window w in Application.Current.Windows)
            if (ReferenceEquals(w.Owner, pop)) w.Topmost = true;
        Visibility = Visibility.Collapsed;
        DrawMute();
        DrawCc();
        await PlayVideo(id, at);
        foreach (Window w in Application.Current.Windows)
            if (ReferenceEquals(w.Owner, pop)) w.Topmost = true;
    }

    public async Task ReturnFromPopOut()
    {
        var pop = _pop;
        var mp = _mp;
        if (pop == null || mp == null) return;
        var id = _videoId;
        var at = Now();
        _pauseWhenPlaying = !mp.IsPlaying;
        SavePosition();
        await Task.Run(() => mp.Stop());
        ClosePop();
        Visibility = Visibility.Visible;
        if (_win.WindowState == WindowState.Minimized) _win.WindowState = WindowState.Maximized;
        _win.Activate();
        await EnsureView();
        await PlayVideo(id, at);
        Focus();
    }

    void ClosePop()
    {
        var pop = _pop;
        _pop = null;
        if (pop == null) return;
        try
        {
            pop.View.MediaPlayer = null;
            pop.View.Dispose();
        }
        catch (Exception ex) { App.Log("Pop-out surface: " + ex.Message); }
        pop.CloseFromPlayer();
    }

    public void PopToggleMute() => ToggleMute();

    public void PopSkip(int direction) => SeekTo(Now() + direction * Skip);

    public void PopSeek(double seconds) => SeekTo(seconds);

    public void PopKey(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.F or Key.F11) return;
        HandleKey(e);
        _pop?.ShowChrome();
    }

    // ---------------------------------------------------------------- keyboard
    public void HandleKey(KeyEventArgs e)
    {
        if (e.Handled) return;
        if (e.OriginalSource is TextBox || (e.OriginalSource is DependencyObject d && FindAncestor<ComboBox>(d) is { IsDropDownOpen: true }))
            return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        switch (key)
        {
            case Key.Escape:
                if (_fullscreen) ToggleFullscreen();
                else _ = CloseAsync();
                break;
            case Key.Space:
            case Key.K:
            case Key.MediaPlayPause:
                TogglePause();
                break;
            case Key.Left:
            case Key.J:
                SkipBy(-Skip);
                break;
            case Key.Right:
            case Key.L:
                SkipBy(Skip);
                break;
            case Key.Up:
                _volume.Value = Math.Min(_volume.Maximum, _volume.Value + 5);
                Flash($"Volume {(int)_volume.Value}");
                break;
            case Key.Down:
                _volume.Value = Math.Max(0, _volume.Value - 5);
                Flash($"Volume {(int)_volume.Value}");
                break;
            case Key.N:
            case Key.MediaNextTrack:
                _ = GoTo(1);
                break;
            case Key.P:
            case Key.MediaPreviousTrack:
                _ = GoTo(-1);
                break;
            case Key.C:
                ToggleSubtitles();
                break;
            case Key.A:
                CycleAudio();
                break;
            case Key.M:
                ToggleMute();
                break;
            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                break;
            default:
                return;
        }
        ShowControls();
        e.Handled = true;
    }

    static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node != null)
        {
            if (node is T hit) return hit;
            node = node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);
        }
        return null;
    }
}
