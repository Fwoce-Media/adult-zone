using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using AdultZone.Core;

namespace AdultZone.Desktop;

/// <summary>
/// The PIN screen: frosted glass over a library blurred past reading, a row
/// of dots and a keypad. Digits, Backspace and Enter work from the keyboard too.
/// </summary>
public sealed class LockScreen : Grid
{
    readonly MainWindow _win;
    readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Height = 14 };
    readonly TextBlock _hint = Ui.Text("", 12.5, Theme.Muted);
    readonly Border _card = new();
    readonly TranslateTransform _shake = new();
    readonly DispatcherTimer _wait = new() { Interval = TimeSpan.FromSeconds(1) };
    string _entry = "";

    public LockScreen(MainWindow win)
    {
        _win = win;
        Visibility = Visibility.Collapsed;
        // Frosted glass over the library: it shows through, blurred past reading,
        // under a dark wash with an ember glow from the top.
        Background = Theme.Alpha(Theme.InkC, 0x99);
        Children.Add(new Border
        {
            Background = new RadialGradientBrush
            {
                Center = new Point(0.5, -0.1), GradientOrigin = new Point(0.5, -0.1), RadiusX = 0.55, RadiusY = 0.62,
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x1A, 0xFF, 0x8A, 0x3D), 0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0x8A, 0x3D), 1),
                },
            },
        });

        var mark = Ui.Wordmark(19);
        mark.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.Margin = new Thickness(0, 8, 0, 20);
        _hint.MinHeight = 17;
        _dots.Margin = new Thickness(0, 0, 0, 26);

        var keys = new System.Windows.Controls.Primitives.UniformGrid { Columns = 3 };
        foreach (var key in new[] { "1", "2", "3", "4", "5", "6", "7", "8", "9", "back", "0", "enter" })
            keys.Children.Add(Key(key));

        _card.Width = 340;
        _card.Padding = new Thickness(26, 30, 26, 24);
        _card.CornerRadius = new CornerRadius(24);
        _card.HorizontalAlignment = HorizontalAlignment.Center;
        _card.VerticalAlignment = VerticalAlignment.Center;
        _card.Background = new LinearGradientBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x09, 0xFF, 0xFF, 0xFF), 68);
        _card.BorderBrush = new LinearGradientBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 90);
        _card.BorderThickness = new Thickness(1);
        _card.RenderTransform = _shake;
        _card.Child = Ui.Column(mark, _hint, _dots, keys);

        // The shadow is a layer of its own behind the glass: an effect on the
        // card itself would draw its text and keys soft.
        var shadow = new Border
        {
            Width = 340, CornerRadius = new CornerRadius(24), Background = Theme.Ink,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 90, ShadowDepth = 34, Direction = 270, Opacity = 0.62 },
            Opacity = 0.9,
            RenderTransform = _shake,
        };
        _card.SizeChanged += (_, _) => shadow.Height = _card.ActualHeight;
        Children.Add(shadow);
        Children.Add(_card);

        _wait.Tick += (_, _) => DrawWait();
    }

    FrameworkElement Key(string key)
    {
        UIElement face = key switch
        {
            "back" => Icons.Delete(22, Theme.Muted),
            "enter" => Icons.Check(22, Theme.Ember),
            _ => new TextBlock { Text = key, FontSize = 21, FontWeight = FontWeights.Medium, Foreground = Theme.Text, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
        };
        var rest = new LinearGradientBrush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x0B, 0xFF, 0xFF, 0xFF), 70);
        var hover = new LinearGradientBrush(Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF), Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF), 70);
        var border = key == "enter" ? Theme.Alpha(Theme.EmberC, 0x66) : Theme.Alpha(Colors.White, 0x21);
        var pad = new Border
        {
            Height = 58, CornerRadius = new CornerRadius(16), Margin = new Thickness(6),
            Background = rest, BorderBrush = border, BorderThickness = new Thickness(1), Child = face,
        };
        var scale = new ScaleTransform(1, 1);
        pad.RenderTransformOrigin = new Point(0.5, 0.5);
        pad.RenderTransform = scale;
        var b = Ui.Bare(pad, () => Press(key));
        b.Focusable = false;
        b.Cursor = Cursors.Hand;
        b.MouseEnter += (_, _) => { pad.Background = key == "enter" ? Theme.Alpha(Theme.EmberC, 0x33) : hover; if (key == "enter") pad.BorderBrush = Theme.Ember; };
        b.MouseLeave += (_, _) => { pad.Background = rest; pad.BorderBrush = border; };
        b.PreviewMouseLeftButtonDown += (_, _) => { scale.ScaleX = scale.ScaleY = 0.95; };
        b.PreviewMouseLeftButtonUp += (_, _) => { scale.ScaleX = scale.ScaleY = 1; };
        return b;
    }

    public void Show()
    {
        HoverPreview.Stop(null);
        Dialogs.CloseAll();
        Keyboard.ClearFocus();
        _entry = "";
        _win.Blur(true);
        Visibility = Visibility.Visible;
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(300)));
        Draw();
        if (Lock.WaitSeconds > 0) { DrawWait(); _wait.Start(); }
    }

    void Hide()
    {
        _entry = "";
        Visibility = Visibility.Collapsed;
        _win.Blur(false);
        _win.StartUp();
    }

    void Draw()
    {
        _dots.Children.Clear();
        var total = Math.Max(Math.Max(Lock.Length, _entry.Length), 4);
        for (var i = 0; i < total; i++)
        {
            var on = i < _entry.Length;
            _dots.Children.Add(new Border
            {
                Width = 12, Height = 12, CornerRadius = new CornerRadius(6), Margin = new Thickness(6.5, 0, 6.5, 0),
                BorderThickness = new Thickness(1.6),
                BorderBrush = on ? Theme.Ember : Theme.Alpha(Colors.White, 0x61),
                Background = on ? Theme.Ember : Theme.Clear,
                Effect = on ? new DropShadowEffect { Color = Theme.EmberC, BlurRadius = 12, ShadowDepth = 0, Opacity = 0.5 } : null,
            });
        }
        if (Lock.WaitSeconds <= 0 && _hint.Foreground == Theme.Muted) _hint.Text = "";
    }

    void DrawWait()
    {
        var left = Lock.WaitSeconds;
        if (left <= 0)
        {
            _wait.Stop();
            _hint.Text = "";
            _hint.Foreground = Theme.Muted;
            return;
        }
        _hint.Text = $"Too many attempts — wait {Math.Ceiling(left).ToString(CultureInfo.InvariantCulture)}s";
        _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
    }

    void Press(string key)
    {
        if (Lock.WaitSeconds > 0) return;
        if (key == "back")
        {
            if (_entry.Length > 0) _entry = _entry[..^1];
        }
        else if (key == "enter")
        {
            Submit();
            return;
        }
        else if (_entry.Length < Lock.MaxLength) _entry += key;
        _hint.Foreground = Theme.Muted;
        Draw();
        // A full-length PIN opens it without Enter.
        if (Lock.LengthKnown && _entry.Length == Lock.Length) Submit();
    }

    void Submit()
    {
        if (_entry.Length == 0) return;
        if (Lock.Unlock(_entry))
        {
            Hide();
            return;
        }
        _entry = "";
        Draw();
        var shake = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(420) };
        foreach (var (x, t) in new[] { (-2.0, 0.04), (4.0, 0.08), (-7.0, 0.13), (7.0, 0.17), (-7.0, 0.21), (7.0, 0.25), (-7.0, 0.29), (4.0, 0.34), (-2.0, 0.38), (0.0, 0.42) })
            shake.KeyFrames.Add(new LinearDoubleKeyFrame(x, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
        _shake.BeginAnimation(TranslateTransform.XProperty, shake);
        if (Lock.WaitSeconds > 0)
        {
            DrawWait();
            _wait.Start();
        }
        else
        {
            _hint.Text = "Wrong PIN";
            _hint.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x80, 0x80));
        }
    }

    public void HandleKey(KeyEventArgs e)
    {
        var key = e.Key;
        if (key is >= System.Windows.Input.Key.D0 and <= System.Windows.Input.Key.D9 && Keyboard.Modifiers == ModifierKeys.None)
            Press(((int)(key - System.Windows.Input.Key.D0)).ToString(CultureInfo.InvariantCulture));
        else if (key is >= System.Windows.Input.Key.NumPad0 and <= System.Windows.Input.Key.NumPad9)
            Press(((int)(key - System.Windows.Input.Key.NumPad0)).ToString(CultureInfo.InvariantCulture));
        else if (key == System.Windows.Input.Key.Back) Press("back");
        else if (key == System.Windows.Input.Key.Enter) Press("enter");
        else return;
        e.Handled = true;
    }
}
