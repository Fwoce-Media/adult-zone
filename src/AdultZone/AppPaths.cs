using System.Diagnostics;

namespace AdultZone;

/// <summary>
/// Where everything lives.
///
/// The library — database, artwork, previews, logs — sits in a Data folder
/// beside the program, so the whole app is one folder you can move or back up.
/// The PIN and API key stay in %USERPROFILE%\.adultzone (see Secrets.cs), so
/// copying that folder never carries them along.
/// </summary>
public static class AppPaths
{
    public const string AppName = "Adult Zone";
    public const string Version = "2.3.0";

    /// <summary>Where the Python build kept everything, and where secrets stay.</summary>
    public static string LegacyHome { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".adultzone");

    private static string _secretsDir;

    /// <summary>
    /// Where the PIN and API key live. Normally the user's own profile folder;
    /// in portable mode, beside the program so a USB stick carries everything.
    /// </summary>
    public static string SecretsDir => _secretsDir ?? LegacyHome;

    /// <summary>
    /// Portable mode: a file named portable.txt sitting beside the program.
    /// Everything then stays in the program's own folder and nothing is
    /// written to the machine it runs on.
    /// </summary>
    public static bool IsPortable { get; private set; }

    /// <summary>The library folder. Settled by Resolve() before anything opens it.</summary>
    public static string Home { get; private set; } = LegacyHome;

    /// <summary>
    /// True when running out of a project's bin folder, i.e. from Visual Studio.
    /// Those builds must never keep the library beside themselves: deleting the
    /// project folder to update it would take the library with it.
    /// </summary>
    public static bool IsDevBuild
    {
        get
        {
            var dir = ProgramDir.Replace('/', '\\');
            return dir.Contains("\\bin\\Debug\\", StringComparison.OrdinalIgnoreCase) ||
                   dir.Contains("\\bin\\Release\\", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void Resolve()
    {
        var overrideHome = Environment.GetEnvironmentVariable("ADULTZONE_HOME");
        if (!string.IsNullOrWhiteSpace(overrideHome))
        {
            Home = overrideHome.Trim();
            return;
        }

        if (File.Exists(Path.Combine(ProgramDir, "portable.txt")))
        {
            var data = Path.Combine(ProgramDir, "Data");
            if (CanWriteTo(data))
            {
                IsPortable = true;
                Home = data;
                _secretsDir = data;   // secrets travel with the program too
                return;
            }
        }

        if (IsDevBuild)
        {
            // Open whatever library the installed program uses, if there is one.
            var pointer = Secrets.Get("library_dir");
            Home = !string.IsNullOrEmpty(pointer) && Directory.Exists(pointer) ? pointer : LegacyHome;
            return;
        }

        var beside = Path.Combine(ProgramDir, "Data");
        if (CanWriteTo(beside))
        {
            Home = beside;
            Secrets.Set("library_dir", beside);
        }
        else
        {
            // Installed somewhere read-only, such as Program Files.
            Home = LegacyHome;
        }
    }

    /// <summary>Fall back to the old location, used if moving the library fails.</summary>
    public static void UseLegacyHome()
    {
        Home = LegacyHome;
        Secrets.Set("library_dir", LegacyHome);
    }

    private static bool CanWriteTo(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var probe = Path.Combine(folder, ".write-test");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string DbPath => Path.Combine(Home, "library.db");
    public static string CacheDir => Path.Combine(Home, "cache");
    public static string ThumbDir => Path.Combine(CacheDir, "thumbs");
    public static string SizedDir => Path.Combine(ThumbDir, "sized");
    public static string PreviewDir => Path.Combine(CacheDir, "previews");
    public static string ActorDir => Path.Combine(Home, "images", "actors");
    public static string StudioDir => Path.Combine(Home, "images", "studios");
    public static string LogPath => Path.Combine(Home, "adult-zone.log");

    /// <summary>Folder the running program sits in.</summary>
    public static string ProgramDir { get; } = AppContext.BaseDirectory;

    public static string WebRoot { get; } = FindWebRoot();

    /// <summary>
    /// The interface files. Normally copied beside the program, but when run
    /// straight from Visual Studio they may only exist in the project folder,
    /// so walk up from the program until an index.html turns up.
    /// </summary>
    private static string FindWebRoot()
    {
        var beside = Path.Combine(ProgramDir, "wwwroot");
        if (File.Exists(Path.Combine(beside, "index.html"))) return beside;

        var folder = new DirectoryInfo(ProgramDir);
        for (var depth = 0; depth < 6 && folder != null; depth++, folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, "wwwroot");
            if (File.Exists(Path.Combine(candidate, "index.html"))) return candidate;
        }
        return beside;
    }

    public static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v",
        ".mpg", ".mpeg", ".webm", ".ts", ".m2ts", ".divx"
    };

    public static void EnsureFolders()
    {
        foreach (var folder in new[] { Home, CacheDir, ThumbDir, SizedDir, PreviewDir, ActorDir, StudioDir })
            Directory.CreateDirectory(folder);
    }

    private static string _ffmpeg;
    private static string _ffprobe;

    public static string FFmpeg => _ffmpeg ??= FindTool("ffmpeg", "FFMPEG_BIN");
    public static string FFprobe => _ffprobe ??= FindTool("ffprobe", "FFPROBE_BIN");

    /// <summary>
    /// A copy shipped beside the program wins, so the app works on a machine
    /// that has never had ffmpeg installed. Otherwise fall back to PATH.
    /// </summary>
    private static string FindTool(string name, string envVar)
    {
        var overrideValue = Environment.GetEnvironmentVariable(envVar);
        if (!string.IsNullOrWhiteSpace(overrideValue)) return overrideValue.Trim();

        var exe = name + ".exe";
        foreach (var folder in new[]
                 {
                     ProgramDir,
                     Path.Combine(ProgramDir, "ffmpeg"),
                     Path.Combine(ProgramDir, "ffmpeg", "bin")
                 })
        {
            var candidate = Path.Combine(folder, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return name; // let the OS resolve it from PATH
    }

    public static bool FFmpegAvailable()
    {
        try
        {
            var info = new ProcessStartInfo(FFprobe, "-version")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            using var process = Process.Start(info);
            if (process is null) return false;
            process.WaitForExit(8000);
            return process.HasExited && process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static void Log(string message)
    {
        try
        {
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
