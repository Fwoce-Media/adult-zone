using System.Net;
using Microsoft.Extensions.FileProviders;
using System.Net.Sockets;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace AdultZone;

/// <summary>
/// The window. WebView2 renders the same interface the Python build served to
/// a browser, so nothing about the frontend changes — it simply has its own
/// window now, with no tabs, address bar or browser chrome.
/// </summary>
public class MainForm : Form
{
    private readonly string _url;
    private WebView2 _view;

    public MainForm(string url)
    {
        _url = url;

        Text = AppPaths.AppName;
        WindowState = FormWindowState.Maximized;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 600);
        BackColor = Color.FromArgb(8, 8, 11);   // matches the app's background

        Icon = LoadIcon();

        _view = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_view);

        Load += OnLoad;
        HandleCreated += (_, _) => Native.UseDarkTitleBar(Handle);
        Shown += (_, _) => WindowState = FormWindowState.Maximized;
        FormClosed += (_, _) => Shell.RequestExit();
    }

    /// <summary>
    /// The window and taskbar icon. Read from inside the program first, so it
    /// does not depend on any file being present beside the exe.
    /// </summary>
    private static Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(MainForm).Assembly.GetManifestResourceStream("AdultZone.window.ico");
            if (stream != null) return new Icon(stream);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[icon] embedded: {ex.Message}");
        }

        try
        {
            var file = Path.Combine(AppPaths.ProgramDir, "assets", "adult-zone.ico");
            if (File.Exists(file)) return new Icon(file);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[icon] file: {ex.Message}");
        }

        try
        {
            // The icon Windows shows for the exe in Explorer.
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe)) return Icon.ExtractAssociatedIcon(exe);
        }
        catch { }

        return null;
    }

    private async void OnLoad(object sender, EventArgs e)
    {
        try
        {
            // Keep the browser profile inside the library folder rather than
            // beside the program, which may sit in Program Files.
            var environment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(AppPaths.Home, "webview"));
            await _view.EnsureCoreWebView2Async(environment);

            var settings = _view.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = true;   // keeps F11 and Ctrl+R
            settings.IsSwipeNavigationEnabled = false;

            // Nothing in the app opens a second window; keep any attempt inside.
            _view.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                _view.CoreWebView2.Navigate(args.Uri);
            };

            _view.CoreWebView2.Navigate(_url);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[webview] {ex}");
            MessageBox.Show(
                "The WebView2 runtime could not start.\n\n" +
                "Install the Microsoft Edge WebView2 Runtime, then run Adult Zone again.\n\n" +
                $"Details are in:\n{AppPaths.LogPath}",
                AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            Shell.RequestExit();
        }
    }
}

/// <summary>Startup, shutdown and the bit that keeps them in step.</summary>
public static class Shell
{
    private static Form _window;

    public static void RequestExit()
    {
        try
        {
            if (_window is { IsDisposed: false })
            {
                _window.BeginInvoke(() => Application.Exit());
                return;
            }
        }
        catch { }
        Application.Exit();
    }

    private static readonly HashSet<string> OpenPaths = new(StringComparer.OrdinalIgnoreCase)
    {
        "/", "/favicon.ico", "/api/version", "/api/live", "/api/quit", "/api/selfcheck"
    };

    private static bool IsOpenWhileLocked(string path) =>
        OpenPaths.Contains(path) ||
        path.StartsWith("/static/", StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith("/api/lock/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Newest change across the interface files. Used in the script and
    /// stylesheet addresses, so an update to any of them changes the address
    /// and the browser has to fetch it fresh.
    /// </summary>
    private static long AssetStamp()
    {
        var newest = DateTime.MinValue;
        foreach (var pattern in new[] { "*.html", "css/*.css", "js/*.js" })
        {
            var folder = Path.Combine(AppPaths.WebRoot, Path.GetDirectoryName(pattern) ?? "");
            if (!Directory.Exists(folder)) continue;
            foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(pattern)))
            {
                var written = File.GetLastWriteTimeUtc(file);
                if (written > newest) newest = written;
            }
        }
        return newest == DateTime.MinValue ? 0 : new DateTimeOffset(newest).ToUnixTimeSeconds();
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // Order matters: decide where the library lives, move an existing one
        // there if this is the first run, and only then open the database.
        Native.SetAppId();
        AppPaths.Resolve();
        DataMove.Run();
        AppPaths.EnsureFolders();

        try
        {
            Db.Init();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[startup] database: {ex}");
            MessageBox.Show($"The library database could not be opened.\n\n{ex.Message}",
                            AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var port = FreePort();
        var url = $"http://127.0.0.1:{port}";

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            ContentRootPath = AppPaths.ProgramDir,
            WebRootPath = AppPaths.WebRoot
        });
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls(url);

        var app = builder.Build();

        // While locked, refuse everything except what the lock screen itself
        // needs. Hiding the overlay therefore reveals nothing. Matches the
        // Python build's list of always-open paths.
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            if (!IsOpenWhileLocked(path) && Lock.IsLocked)
            {
                context.Response.StatusCode = 423;
                await context.Response.WriteAsJsonAsync(new { detail = "Locked", locked = true });
                return;
            }
            await next();
        });

        // The interface asks for its files under /static/ -- /static/css/style.css,
        // /static/js/app.js, /static/flags/... -- because that is where the Python
        // build served them. UseStaticFiles() alone would put them at the root
        // instead, and every one of those requests would 404.
        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = new PhysicalFileProvider(AppPaths.WebRoot),
            RequestPath = "/static",
            // Always check with the server before reusing a cached copy. On a
            // local server that check is nearly free, and it means an update
            // can never be hidden behind an old app.js.
            OnPrepareResponse = context =>
                context.Context.Response.Headers.CacheControl = "no-cache"
        });
        Api.Map(app);

        // The shell is served with its cache-busting placeholders filled in.
        app.MapGet("/", (HttpContext context) =>
        {
            var indexPath = Path.Combine(AppPaths.WebRoot, "index.html");
            if (!File.Exists(indexPath)) return Results.NotFound("index.html is missing");

            // The page itself is never cached, so it always hands out current links.
            context.Response.Headers.CacheControl = "no-store, must-revalidate";
            var stamp = AssetStamp();
            var html = File.ReadAllText(indexPath)
                .Replace("__V__", stamp.ToString())
                // Rendered locked from the first paint, so content never flashes up.
                .Replace("__LOCK__", Lock.IsLocked ? "locked" : "");

            return Results.Content(html, "text/html; charset=utf-8");
        });

        app.MapGet("/favicon.ico", () =>
        {
            var path = Path.Combine(AppPaths.WebRoot, "favicon.ico");
            return File.Exists(path) ? Results.File(path, "image/x-icon") : Results.NotFound();
        });

        try
        {
            app.Start();
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[startup] server: {ex}");
            MessageBox.Show($"The server could not start.\n\n{ex.Message}",
                            AppPaths.AppName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        AppPaths.Log($"Adult Zone {AppPaths.Version} started on {url}");

        ApplicationConfiguration.Initialize();
        _window = new MainForm(url);
        Application.Run(_window);

        try
        {
            app.StopAsync(TimeSpan.FromSeconds(2)).GetAwaiter().GetResult();
        }
        catch { }
    }
}
