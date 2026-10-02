using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AdultZone.Desktop;

/// <summary>The date and time beside the search box and, on a laptop, the battery: how full, and whether it is charging.</summary>
public static class StatusStrip
{
    [StructLayout(LayoutKind.Sequential)]
    struct PowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public int BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    static extern bool GetSystemPowerStatus(out PowerStatus status);

    /// <summary>Percent and whether mains power is connected; null when the computer has no battery.</summary>
    public static (int Percent, bool Plugged)? Battery()
    {
        try
        {
            if (!GetSystemPowerStatus(out var s)) return null;
            // 128: no battery. 255: unknown.
            if ((s.BatteryFlag & 128) != 0 || s.BatteryFlag == 255 || s.BatteryLifePercent > 100) return null;
            return (s.BatteryLifePercent, s.ACLineStatus == 1);
        }
        catch { return null; }
    }

    public static string State(int percent, bool plugged) => plugged ? (percent >= 100 ? "Full" : "Charging") : "On battery";

    public static FrameworkElement Build()
    {
        var clock = Ui.Text("", 13, Theme.Muted, FontWeights.Medium);
        clock.VerticalAlignment = VerticalAlignment.Center;

        var level = new Border { CornerRadius = new CornerRadius(1.5), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(2) };
        var shell = new Border { Width = 26, Height = 13, CornerRadius = new CornerRadius(3), BorderThickness = new Thickness(1.4), BorderBrush = Theme.Muted, Child = level };
        var cap = new Border { Width = 2.5, Height = 6, CornerRadius = new CornerRadius(0, 1.5, 1.5, 0), Background = Theme.Muted, VerticalAlignment = VerticalAlignment.Center };
        var bolt = new Path
        {
            Data = Geometry.Parse("M7,0 L1,8 L5,8 L3.5,14 L10,5.5 L6,5.5 Z"), Fill = Theme.Ember, Width = 10, Height = 14, Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 5, 0),
        };
        var percent = Ui.Text("", 13, Theme.Muted, FontWeights.Medium);
        percent.VerticalAlignment = VerticalAlignment.Center;
        percent.Margin = new Thickness(7, 0, 0, 0);
        var battery = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(18, 0, 0, 0) };
        shell.VerticalAlignment = VerticalAlignment.Center;
        battery.Children.Add(bolt);
        battery.Children.Add(shell);
        battery.Children.Add(cap);
        battery.Children.Add(percent);

        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 18, 0) };
        row.Children.Add(clock);
        row.Children.Add(battery);

        void Update()
        {
            var now = DateTime.Now;
            clock.Text = now.ToString("ddd d MMM", CultureInfo.CurrentCulture) + "  ·  " + now.ToString("t", CultureInfo.CurrentCulture);
            if (Battery() is not { } b)
            {
                battery.Visibility = Visibility.Collapsed;
                return;
            }
            battery.Visibility = Visibility.Visible;
            var low = b.Percent <= 20 && !b.Plugged;
            level.Width = Math.Round(Math.Max(1.5, 19.2 * b.Percent / 100.0), 1);
            level.Background = low ? Theme.Ember : b.Plugged ? Theme.Ember : Theme.Muted;
            bolt.Visibility = b.Plugged ? Visibility.Visible : Visibility.Collapsed;
            percent.Text = b.Percent + "%  " + State(b.Percent, b.Plugged);
            percent.Foreground = low ? Theme.Ember : Theme.Muted;
        }

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) => Update();
        row.Loaded += (_, _) => { Update(); timer.Start(); };
        row.Unloaded += (_, _) => timer.Stop();
        Update();
        return row;
    }
}
