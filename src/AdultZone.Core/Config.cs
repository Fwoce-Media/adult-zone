using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AdultZone.Core;

/// <summary>Where things live, and the few fixed numbers the app runs on.</summary>
public static class Config
{
    public const string AppName = "Adult Zone";
    public const string AppVersion = "3.1.1";

    /// <summary>
    /// Portable mode: a portable.txt beside the program keeps the whole library
    /// in a Data folder next to it, so a copy on a USB drive carries everything.
    /// </summary>
    public static bool IsPortable { get; private set; }

    public static readonly string AppHome = ResolveHome();

    /// <summary>Where 1.x kept everything, and where 2.x kept the PIN and API key.</summary>
    public static string ProfileHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".adultzone");

    /// <summary>Where the installed 2.x kept its library.</summary>
    public static string TwoDotXHome =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Adult Zone", "Data");

    /// <summary>
    /// The library opens where it already is. 1.x kept it in %USERPROFILE%\.adultzone;
    /// the installed 2.x moved it to Documents\Adult Zone\Data. Whichever holds
    /// a library is used, the 1.x folder first; a new library starts in the 1.x folder.
    /// </summary>
    static string ResolveHome()
    {
        if (Environment.GetEnvironmentVariable("ADULTZONE_HOME") is { Length: > 0 } custom) return custom;
        var here = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(here, "portable.txt")))
        {
            IsPortable = true;
            return Path.Combine(here, "Data");
        }
        if (File.Exists(Path.Combine(ProfileHome, "library.db"))) return ProfileHome;
        if (File.Exists(Path.Combine(TwoDotXHome, "library.db"))) return TwoDotXHome;
        return ProfileHome;
    }

    public static string DbPath => Path.Combine(AppHome, "library.db");
    public static string CacheDir => Path.Combine(AppHome, "cache");
    public static string ThumbDir => Path.Combine(CacheDir, "thumbs");
    public static string PreviewDir => Path.Combine(CacheDir, "previews");
    public static string SpriteDir => Path.Combine(CacheDir, "sprites");
    public static string ImageCacheDir => Path.Combine(CacheDir, "web");
    public static string ActorDir => Path.Combine(AppHome, "images", "actors");
    public static string StudioDir => Path.Combine(AppHome, "images", "studios");
    public static string LogPath => Path.Combine(AppHome, "adultzone.log");

    public static void EnsureFolders()
    {
        foreach (var dir in new[] { AppHome, CacheDir, ThumbDir, PreviewDir, SpriteDir, ImageCacheDir, ActorDir, StudioDir })
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// 2.x kept the PIN and the ThePornDB key in a secrets.json of their own
    /// rather than in the library. Read once, so they carry over.
    /// </summary>
    public static Dictionary<string, string> TwoDotXSecrets()
    {
        foreach (var folder in new[] { ProfileHome, AppContext.BaseDirectory, AppHome })
        {
            try
            {
                var path = Path.Combine(folder, "secrets.json");
                if (!File.Exists(path)) continue;
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path)) ?? new();
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        return new();
    }

    public static readonly HashSet<string> VideoExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg", ".webm", ".ts", ".m2ts", ".divx",
    };

    public static readonly HashSet<string> ImageExts = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp",
    };

    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Cache key for a source file, the same one 1.x used, so its thumbnails
    /// and previews are found under the same names.
    /// </summary>
    public static string KeyFor(string path) => Sha1Hex(path, 20);

    public static string Sha1Hex(string text, int length)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant()[..length];
    }
}

/// <summary>How big and how sharp the looping previews are built.</summary>
public sealed record PreviewProfile(string Name, int Width, int Crf, int Fps, string Preset, string Label);

public static class PreviewProfiles
{
    public const int Segments = 6;
    public const double SegmentSeconds = 1.4;
    public const int ThumbWidth = 1920;

    public static readonly PreviewProfile[] All =
    {
        new("sd", 640, 30, 24, "veryfast", "SD"),
        new("high", 1280, 22, 30, "fast", "720p"),
        new("max", 1920, 20, 30, "fast", "1080p"),
    };

    public static PreviewProfile Get(string name) => All.FirstOrDefault(p => p.Name == name) ?? All[1];
}
