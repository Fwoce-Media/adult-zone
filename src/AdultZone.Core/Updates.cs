using System.IO.Compression;
using System.Text.Json.Nodes;
using AdultZone.Core.Data;
using AdultZone.Core.Media;
using AdultZone.Core.Providers;

namespace AdultZone.Core;

/// <summary>A newer release of the app on GitHub.</summary>
public sealed record Release(string Version, string Page, string SetupUrl, string SetupName, long SetupSize);

/// <summary>
/// Looking on the app's GitHub page for a newer release, and fetching its
/// Setup.exe. Only published releases count; a draft is not seen.
/// </summary>
public static class Updates
{
    public const string Repo = "Fwoce-Media/adult-zone";
    public const string Setting = "update_check";

    /// <summary>Whether to look when the app starts. On unless switched off.</summary>
    public static bool OnStart => Db.Setting(Setting) != "0";

    /// <summary>The release found by the last look, when it is newer than this copy.</summary>
    public static Release? Available { get; private set; }

    static Version? Parse(string text) =>
        Version.TryParse(text.Trim().TrimStart('v', 'V').Split('-', '+')[0], out var v) ? v : null;

    public static bool Newer(string candidate, string current) =>
        Parse(candidate) is { } a && Parse(current) is { } b && a > b;

    /// <summary>A release read from GitHub's answer; null when it has no version on it.</summary>
    public static Release? Read(JsonNode? node)
    {
        var tag = node?["tag_name"]?.ToString() ?? "";
        if (Parse(tag) == null) return null;
        string url = "", name = "";
        long size = 0;
        foreach (var asset in node?["assets"] as JsonArray ?? new JsonArray())
        {
            var file = asset?["name"]?.ToString() ?? "";
            if (!file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            // The installer, over any other program attached.
            if (name.Length > 0 && !file.Contains("setup", StringComparison.OrdinalIgnoreCase)) continue;
            name = file;
            url = asset?["browser_download_url"]?.ToString() ?? "";
            size = long.TryParse(asset?["size"]?.ToString(), out var n) ? n : 0;
        }
        return new Release(tag.Trim().TrimStart('v', 'V'), node?["html_url"]?.ToString() ?? $"https://github.com/{Repo}/releases", url, name, size);
    }

    /// <summary>Looks on GitHub. Returns the release when it is newer than this copy, else null. Throws when GitHub cannot be reached.</summary>
    public static Release? Check()
    {
        var node = Http.GetJson($"https://api.github.com/repos/{Repo}/releases/latest",
            new Dictionary<string, string> { ["Accept"] = "application/vnd.github+json" });
        var latest = Read(node);
        Available = latest != null && Newer(latest.Version, Config.AppVersion) ? latest : null;
        return Available;
    }

    /// <summary>Downloads a release's installer to the temporary folder. Returns its path.</summary>
    public static string Download(Release release, Action<long, long> progress)
    {
        if (release.SetupUrl.Length == 0) throw new SourceError("That release has no installer attached.");
        var folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "AdultZone-update")).FullName;
        var file = Path.Combine(folder, release.SetupName.Length > 0 ? release.SetupName : "Setup.exe");
        Http.Download(release.SetupUrl, file, release.SetupSize, progress);
        return file;
    }
}

/// <summary>FFmpeg fetched for the app when the computer has none: ffmpeg.exe and ffprobe.exe, kept in the library folder.</summary>
public static class FfmpegInstall
{
    /// <summary>The standing address of the current Windows build from the BtbN project on GitHub.</summary>
    public const string Url = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip";

    public static string Folder => Path.Combine(Config.AppHome, "ffmpeg");

    /// <summary>Downloads the build and takes the two programs out of it. Progress is bytes so far and the total (0 when not known).</summary>
    public static void Install(Action<long, long> progress)
    {
        Directory.CreateDirectory(Folder);
        var zip = Path.Combine(Folder, "ffmpeg-download.zip");
        try
        {
            Http.Download(Url, zip, 0, progress);
            Extract(zip, Folder);
        }
        finally
        {
            try { File.Delete(zip); } catch { }
        }
        Ffmpeg.Forget();
        if (!Ffmpeg.HasFfmpeg) throw new SourceError("The download did not contain ffmpeg.");
    }

    /// <summary>ffmpeg.exe and ffprobe.exe from wherever they sit in the archive, into one folder.</summary>
    public static int Extract(string zip, string folder)
    {
        var taken = 0;
        using var archive = ZipFile.OpenRead(zip);
        foreach (var entry in archive.Entries)
        {
            var name = Path.GetFileName(entry.FullName);
            if (!name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) && !name.Equals("ffprobe.exe", StringComparison.OrdinalIgnoreCase)) continue;
            entry.ExtractToFile(Path.Combine(folder, name), true);
            taken++;
        }
        return taken;
    }
}
