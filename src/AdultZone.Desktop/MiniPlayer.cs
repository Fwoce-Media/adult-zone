using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using LibVLCSharp.Shared;
using LibVLCSharp.WPF;
using VlcMediaPlayer = LibVLCSharp.Shared.MediaPlayer;

namespace AdultZone.Desktop;

/// <summary>
/// A small player for checking which match is right: one or more library
/// videos, a seek bar to skip through, previous/next, play and sound.
/// </summary>
public sealed class MiniPlayer : Border
{
    LibVLC? _vlc;
    VlcMediaPlayer? _mp;
    Task<bool>? _starting;
    readonly Border _screen = new() { Background = Brushes.Black };
    double _heldAt;
    DateTime _heldUntil;
    readonly Slider _seek = new() { Minimum = 0, Maximum = 1, Focusable = false, IsMoveToPointEnabled = true, Margin = new Thickness(0, 10, 0, 6) };
    readonly TextBlock _caption = Ui.Text("", 13, Theme.Text, FontWeights.SemiBold);
    readonly TextBlock _heading = Ui.Text("", 15, Theme.Ember, FontWeights.Bold, margin: new Thickness(0, 0, 0, 4));
    readonly object _gate = new();
    int _playVersion;
    readonly TextBlock _count = Ui.Text("", 12, Theme.Muted);
    readonly TextBlock _time = Ui.Text("", 12, Theme.Muted);
    readonly ContentControl _playFace = new() { Focusable = false };
    readonly ContentControl _soundFace = new() { Focusable = false };
    readonly Button _prev, _next;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    List<(string Path, string Title)> _items = new();
    int _index;
    bool _dragging, _muted = true;
    double _length;

    public MiniPlayer()
    {
        Background = Theme.Panel2;
        BorderBrush = Theme.LineSoft;
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(12);
        Padding = new Thickness(12);

        _screen.SetBinding(HeightProperty, new System.Windows.Data.Binding(nameof(ActualWidth)) { Source = _screen, Converter = new Ratio() });
        _seek.AddHandler(PreviewMouseLeftButtonDownEvent, new MouseButtonEventHandler((_, _) => _dragging = true), true);
        _seek.AddHandler(PreviewMouseLeftButtonUpEvent, new MouseButtonEventHandler((_, _) =>
        {
            if (!_dragging) return;
            _dragging = false;
            if (_mp != null && _mp.IsSeekable)
            {
                _mp.Position = (float)_seek.Value;
                _heldAt = _seek.Value;
                _heldUntil = DateTime.UtcNow.AddSeconds(2.5);
            }
        }), true);
        _seek.ValueChanged += (_, _) =>
        {
            if (_dragging) _time.Text = Clock(_seek.Value * _length) + " / " + Clock(_length);
        };

        Button Icon(ContentControl face, Action click)
        {
            var b = Ui.Bare(new Border { Width = 34, Height = 34, Background = Theme.Clear, Child = face }, click);
            b.Cursor = Cursors.Hand;
            b.Focusable = false;
            return b;
        }
        _prev = Icon(new ContentControl { Content = Glyphs.Previous(), Focusable = false }, () => Show(_index - 1));
        _next = Icon(new ContentControl { Content = Glyphs.Next(), Focusable = false }, () => Show(_index + 1));
        var play = Icon(_playFace, TogglePlay);
        var sound = Icon(_soundFace, ToggleSound);
        _playFace.Content = Glyphs.Pause();
        _soundFace.Content = Glyphs.Speaker(true);

        var controls = new DockPanel { LastChildFill = true };
        var left = Ui.Row(play, _prev, _next, sound);
        DockPanel.SetDock(left, Dock.Left);
        controls.Children.Add(left);
        _time.VerticalAlignment = VerticalAlignment.Center;
        _time.HorizontalAlignment = HorizontalAlignment.Right;
        controls.Children.Add(_time);

        _caption.TextTrimming = TextTrimming.CharacterEllipsis;
        var head = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(_count, Dock.Right);
        _count.VerticalAlignment = VerticalAlignment.Center;
        head.Children.Add(_count);
        head.Children.Add(_caption);

        _heading.TextTrimming = TextTrimming.CharacterEllipsis;
        Child = Ui.Column(_heading, head, _screen, _seek, controls);

        _timer.Tick += (_, _) =>
        {
            if (_mp == null || _dragging) return;
            _length = Math.Max(0, _mp.Length / 1000.0);
            var at = (double)_mp.Position;
            if (DateTime.UtcNow < _heldUntil && Math.Abs(at - _heldAt) * _length > 1.5) at = _heldAt;
            _seek.Value = Math.Clamp(at, 0, 1);
            _time.Text = _length > 0 ? Clock(_mp.Time / 1000.0) + " / " + Clock(_length) : "";
        };
        Unloaded += (_, _) => Close();
    }

    sealed class Ratio : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type t, object p, CultureInfo c) => value is double w && w > 0 ? w * 9 / 16 : 200.0;
        public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
    }

    static string Clock(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    Task<bool> Start() => _starting ??= StartAsync();

    async Task<bool> StartAsync()
    {
        try
        {
            (_vlc, _mp) = await Task.Run(() =>
            {
                LibVLCSharp.Shared.Core.Initialize();
                var v = new LibVLC("--no-video-title-show", "--no-snapshot-preview", "--avcodec-hw=any", "--no-spdif");
                return (v, new VlcMediaPlayer(v) { EnableHardwareDecoding = true, EnableKeyInput = false, EnableMouseInput = false, Mute = true });
            });
            _mp.Playing += (_, _) => Dispatcher.BeginInvoke(() => _playFace.Content = Glyphs.Pause());
            _mp.Paused += (_, _) => Dispatcher.BeginInvoke(() => _playFace.Content = Glyphs.Play());
            _mp.EndReached += (_, _) => Dispatcher.BeginInvoke(() => _playFace.Content = Glyphs.Play());
            var view = new VideoView { Background = Brushes.Black };
            var ready = new TaskCompletionSource<bool>();
            view.Loaded += (_, _) =>
            {
                view.MediaPlayer = _mp;
                ready.TrySetResult(true);
            };
            _screen.Child = view;
            await ready.Task;
            _timer.Start();
            return true;
        }
        catch (Exception ex)
        {
            App.Log("Mini player could not start: " + ex);
            _screen.Child = Ui.Text("The player could not start", 13, Theme.Muted, margin: new Thickness(14));
            return false;
        }
    }

    /// <summary>The videos to look through, under a heading; plays the first, a fifth of the way in.</summary>
    public async void Load(List<(string Path, string Title)> items, string heading)
    {
        _items = items;
        _index = 0;
        _heading.Text = heading;
        if (items.Count == 0)
        {
            var version = ++_playVersion;
            _caption.Text = "No videos in your library";
            _count.Text = "";
            _time.Text = "";
            _seek.Value = 0;
            _prev.Visibility = _next.Visibility = Visibility.Collapsed;
            var mp = _mp;
            if (mp != null) _ = Task.Run(() => { lock (_gate) if (version == _playVersion) mp.Stop(); });
            return;
        }
        if (!await Start()) return;
        if (!ReferenceEquals(items, _items)) return;
        Show(0);
    }

    void Show(int index)
    {
        if (_mp == null || _vlc == null || _items.Count == 0) return;
        _index = (index % _items.Count + _items.Count) % _items.Count;
        var (path, title) = _items[_index];
        _caption.Text = title;
        _count.Text = _items.Count > 1 ? $"{_index + 1} / {_items.Count}" : "";
        _prev.Visibility = _next.Visibility = _items.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _seek.Value = 0;
        _time.Text = "";
        var mp = _mp;
        var vlc = _vlc;
        var version = ++_playVersion;
        var muted = _muted;
        Task.Run(() =>
        {
            // One start at a time, and only the latest: a slow open never
            // lands after the pick has moved on.
            lock (_gate)
            {
                if (version != _playVersion) return;
                using var media = new Media(vlc, new Uri(path));
                mp.Play(media);
                mp.Mute = muted;
                for (var i = 0; i < 40 && !mp.IsSeekable && version == _playVersion; i++) System.Threading.Thread.Sleep(50);
                if (version == _playVersion && mp.IsSeekable) mp.Position = 0.2f;
            }
        });
    }

    void TogglePlay()
    {
        if (_mp == null) return;
        if (_mp.IsPlaying) _mp.Pause();
        else if (_mp.State == VLCState.Ended || _mp.State == VLCState.Stopped) Show(_index);
        else _mp.Play();
    }

    void ToggleSound()
    {
        _muted = !_muted;
        if (_mp != null) _mp.Mute = _muted;
        _soundFace.Content = Glyphs.Speaker(_muted);
    }

    public void Close()
    {
        _timer.Stop();
        var mp = _mp;
        var vlc = _vlc;
        _mp = null;
        _vlc = null;
        _starting = null;
        if (_screen.Child is VideoView v) v.MediaPlayer = null;
        _screen.Child = null;
        if (mp == null) return;
        Task.Run(() =>
        {
            try { mp.Stop(); mp.Dispose(); vlc?.Dispose(); } catch { }
        });
    }
}
