using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using AdultZone.Core;
using AdultZone.Core.Data;
using AdultZone.Core.Library;
using AdultZone.Core.Providers;

namespace AdultZone.Desktop;

public static class Program
{
    const string MutexName = "Local\\AdultZone.Desktop.SingleInstance";
    const string WakeName = "Local\\AdultZone.Desktop.Wake";

    [STAThread]
    public static int Main(string[] args)
    {
        // A second launch hands over to the copy already open.
        using var mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            try
            {
                using var wake = EventWaitHandle.OpenExisting(WakeName);
                wake.Set();
            }
            catch { }
            return 0;
        }

        using var wakeEvent = new EventWaitHandle(false, EventResetMode.AutoReset, WakeName);
        var app = new App();
        new Thread(() =>
        {
            while (true)
            {
                wakeEvent.WaitOne();
                app.Dispatcher.BeginInvoke(() => (app.MainWindow as MainWindow)?.BringToFront());
            }
        }) { IsBackground = true, Name = "Adult Zone wake" }.Start();
        return app.Run();
    }
}

public sealed class App : Application
{
    static readonly object LogGate = new();

    public App()
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        DispatcherUnhandledException += OnUiException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log("Unhandled: " + e.ExceptionObject);
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log("Background task: " + e.Exception);
            e.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Theme.Apply(this);
        try
        {
            Db.Init();
        }
        catch (Exception ex)
        {
            Log("Could not open the library: " + ex);
            MessageBox.Show("Adult Zone could not open its library:\n\n" + ex.Message + $"\n\n{Config.AppHome}",
                            Config.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Scanner.Log = Log;
        Scrape.Trim = ImageTrim.Trim;
        Log($"=== {Config.AppName} {Config.AppVersion} started — library in {Config.AppHome} ===");
        var window = new MainWindow();
        MainWindow = window;
        window.Show();
    }

    void OnUiException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log("UI error: " + e.Exception);
        e.Handled = true;
        (MainWindow as MainWindow)?.Toast("Something went wrong: " + e.Exception.Message, true);
    }

    public static void Log(string message)
    {
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(Config.AppHome);
                var path = Config.LogPath;
                if (File.Exists(path) && new FileInfo(path).Length > 2_000_000)
                    File.Move(path, path + ".old", true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>A dark title bar, in the page's own ink.</summary>
    public static void DarkTitleBar(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            try
            {
                var hwnd = new WindowInteropHelper(window).Handle;
                var on = 1;
                if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int));
                var caption = 0x000B0808;
                DwmSetWindowAttribute(hwnd, 35, ref caption, sizeof(int));
            }
            catch { }
        };
    }
}

/// <summary>
/// Crops flat bars from around a picture: thumbnail services fit a landscape
/// still into a portrait frame by padding it, which arrives as a small
/// picture in a large plain box.
/// </summary>
public static class ImageTrim
{
    public static byte[] Trim(byte[] data)
    {
        try
        {
            var decoder = BitmapDecoder.Create(new MemoryStream(data), BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad);
            var frame = decoder.Frames[0];
            var bgra = new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
            int w = bgra.PixelWidth, h = bgra.PixelHeight;
            if (w < 40 || h < 40) return data;
            var stride = w * 4;
            var px = new byte[stride * h];
            bgra.CopyPixels(px, stride, 0);
            (byte B, byte G, byte R) At(int x, int y) => (px[y * stride + x * 4], px[y * stride + x * 4 + 1], px[y * stride + x * 4 + 2]);

            // Corners agreeing on one colour is what padding looks like.
            var corners = new[] { At(0, 0), At(w - 1, 0), At(0, h - 1), At(w - 1, h - 1) };
            if (corners.Distinct().Count() > 2) return data;
            var bg = corners[0];
            bool Differs(int x, int y)
            {
                var c = At(x, y);
                return Math.Max(Math.Max(Math.Abs(c.R - bg.R), Math.Abs(c.G - bg.G)), Math.Abs(c.B - bg.B)) > 18;
            }
            int left = w, right = -1, top = h, bottom = -1;
            for (var y = 0; y < h; y++)
                for (var x = 0; x < w; x++)
                    if (Differs(x, y))
                    {
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
            if (right < 0) return data;
            int cw = right - left + 1, ch = bottom - top + 1;
            if (cw < 40 || ch < 40 || cw * (double)ch > 0.94 * w * h) return data;

            var cropped = new CroppedBitmap(bgra, new Int32Rect(left, top, cw, ch));
            // A PNG may be see-through, so it stays a PNG.
            BitmapEncoder encoder = data[0] == 0x89 ? new PngBitmapEncoder() : new JpegBitmapEncoder { QualityLevel = 92 };
            encoder.Frames.Add(BitmapFrame.Create(cropped));
            using var output = new MemoryStream();
            encoder.Save(output);
            return output.ToArray();
        }
        catch
        {
            return data;
        }
    }
}
