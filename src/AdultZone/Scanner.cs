using System.Globalization;
using System.Text.RegularExpressions;

namespace AdultZone;

public class ScanState
{
    public bool Running { get; set; }
    public string Phase { get; set; } = "idle";
    public int Found { get; set; }
    public int Added { get; set; }
    public int Updated { get; set; }
    public int Removed { get; set; }
    public int AssetsTotal { get; set; }
    public int AssetsDone { get; set; }
    public string Current { get; set; } = "";
    public string Error { get; set; } = "";
}

public record ParsedName(string Title, string Studio, List<string> Actors, string ReleaseDate);

/// <summary>
/// Walks the storage folders, records new videos and queues artwork jobs.
/// Filename parsing mirrors the Python build so an existing library keeps
/// being read the same way.
/// </summary>
public static class Scanner
{
    public static readonly ScanState State = new();
    private static readonly object Gate = new();
    private static readonly SemaphoreSlim AssetWorkers = new(2, 2);

    private static readonly Regex Junk = new(
        @"\b(1080p|720p|480p|2160p|4k|uhd|hdrip|webrip|web-dl|bluray|x264|x265|h264|h265|hevc|aac|mp4|xxx|hd|sd|hq|rq|sample|part\d+|cd\d+)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex Bracketed = new(@"[\[{]([^\]}]+)[\]}]", RegexOptions.Compiled);
    private static readonly Regex SplitActors = new(@"\s*(?:,|&|\+|\band\b)\s*",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Kind)[] DatePatterns =
    {
        (new Regex(@"(?<!\d)(20\d{2}|19\d{2})[-._/](\d{1,2})[-._/](\d{1,2})(?!\d)"), "ymd"),
        (new Regex(@"(?<!\d)(\d{1,2})[-._/](\d{1,2})[-._/](20\d{2}|19\d{2})(?!\d)"), "dmy"),
        (new Regex(@"(?<!\d)(\d{2})[-._](\d{2})[-._](\d{2})(?!\d)"), "yymmdd"),
        (new Regex(@"[(\[]?(20\d{2}|19\d{2})[)\]]?"), "year")
    };

    // -------------------------------------------------------- name parsing

    private static string Clean(string text)
    {
        text = (text ?? "").Replace('_', ' ').Replace('.', ' ');
        text = Junk.Replace(text, " ");
        text = Regex.Replace(text, @"[\[\](){}]", " ");
        text = Regex.Replace(text, @"\s{2,}", " ");
        return text.Trim(' ', '-', '.');
    }

    private static string TitleCase(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        var isUniform = text == text.ToUpperInvariant() || text == text.ToLowerInvariant();
        if (!isUniform) return text;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(word => word.Length > 0 && char.IsLetter(word[0])
                ? char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant()
                : word);
        return string.Join(" ", words);
    }

    /// <summary>Short, wordy and digit-free reads as a name rather than a title.</summary>
    private static bool LooksLikeName(string text)
    {
        text = (text ?? "").Trim();
        if (text.Length == 0 || text.Length > 40 || text.Any(char.IsDigit)) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words >= 1 && words <= 4;
    }

    public static ParsedName ParseFilename(string filePath, string root, bool folderAsStudio = true,
                                           bool twoPartActor = true)
    {
        var stem = Path.GetFileNameWithoutExtension(filePath) ?? "";
        var work = stem;
        var actors = new List<string>();
        string studio = "", title = "", releaseDate = "";

        // A bracketed studio is taken first, since Clean() strips brackets.
        var bracketStudio = "";
        foreach (Match match in Bracketed.Matches(stem))
        {
            var candidate = Clean(match.Groups[1].Value);
            if (candidate.Length < 2 || candidate.All(char.IsDigit)) continue;
            bracketStudio = TitleCase(candidate);
            var index = work.IndexOf(match.Value, StringComparison.Ordinal);
            if (index >= 0) work = work.Remove(index, match.Value.Length).Insert(index, " - ");
            break;
        }

        foreach (var (pattern, kind) in DatePatterns)
        {
            var match = pattern.Match(work);
            if (!match.Success) continue;

            try
            {
                if (kind == "ymd")
                {
                    var year = match.Groups[1].Value;
                    var month = int.Parse(match.Groups[2].Value);
                    var day = int.Parse(match.Groups[3].Value);
                    if (month is < 1 or > 12 || day is < 1 or > 31) continue;
                    releaseDate = $"{year}-{month:00}-{day:00}";
                }
                else if (kind == "dmy")
                {
                    var day = int.Parse(match.Groups[1].Value);
                    var month = int.Parse(match.Groups[2].Value);
                    var year = match.Groups[3].Value;
                    // Day-first unless the numbers say otherwise.
                    if (month > 12 && day <= 12) (day, month) = (month, day);
                    if (month is < 1 or > 12 || day is < 1 or > 31) continue;
                    releaseDate = $"{year}-{month:00}-{day:00}";
                }
                else if (kind == "yymmdd")
                {
                    var yy = int.Parse(match.Groups[1].Value);
                    var month = int.Parse(match.Groups[2].Value);
                    var day = int.Parse(match.Groups[3].Value);
                    if (month is < 1 or > 12 || day is < 1 or > 31) continue;
                    var year = yy < 70 ? 2000 + yy : 1900 + yy;
                    releaseDate = $"{year}-{month:00}-{day:00}";
                }
                else
                {
                    releaseDate = $"{match.Groups[1].Value}-01-01";
                }
            }
            catch
            {
                continue;
            }

            work = work[..match.Index] + " - " + work[(match.Index + match.Length)..];
            break;
        }

        var parts = Regex.Split(work, @"\s+-\s+|\s+-\s*|\s*-\s+")
            .Select(Clean)
            .Where(part => part.Length > 0)
            .ToList();

        if (bracketStudio.Length > 0)
        {
            studio = bracketStudio;
            if (parts.Count >= 2)
            {
                actors = SplitActors.Split(parts[0])
                    .Where(name => name.Length > 0).Select(TitleCase).ToList();
                title = TitleCase(string.Join(" - ", parts.Skip(1)));
            }
            else if (parts.Count == 1)
            {
                title = TitleCase(parts[0]);
            }
        }
        else if (parts.Count >= 3)
        {
            studio = TitleCase(parts[0]);
            actors = SplitActors.Split(parts[1])
                .Where(name => name.Length > 0).Select(TitleCase).ToList();
            title = TitleCase(string.Join(" - ", parts.Skip(2)));
        }
        else if (parts.Count == 2)
        {
            studio = TitleCase(parts[0]);
            var second = parts[1];
            var names = SplitActors.Split(second).Where(name => name.Length > 0).ToList();

            // Only trust this shape when the filename really used a dash: a
            // stripped date can leave a title looking like a name.
            var allowTwoPart = twoPartActor && stem.Contains('-');
            if ((names.Count > 1 || allowTwoPart) && names.All(LooksLikeName))
                actors = names.Select(TitleCase).ToList();

            title = TitleCase(second);
        }
        else if (parts.Count == 1)
        {
            title = TitleCase(parts[0]);
        }

        if (string.IsNullOrWhiteSpace(title)) title = TitleCase(Clean(stem));
        if (string.IsNullOrWhiteSpace(title)) title = stem;

        if (folderAsStudio && studio.Length == 0)
        {
            var parent = Path.GetDirectoryName(filePath);
            if (parent != null && !string.Equals(parent, root, StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Clean(Path.GetFileName(parent));
                if (candidate.Length > 0 && candidate.Length < 60 && !candidate.All(char.IsDigit))
                    studio = TitleCase(candidate);
            }
        }

        return new ParsedName(title, studio, actors, releaseDate);
    }

    // ------------------------------------------------------------ scanning

    public static void ScanAll(bool prune = true, bool folderAsStudio = true, bool twoPartActor = true)
    {
        lock (Gate)
        {
            if (State.Running) return;
            State.Running = true;
            State.Phase = "scanning";
            State.Found = State.Added = State.Updated = State.Removed = 0;
            State.AssetsTotal = State.AssetsDone = 0;
            State.Current = "";
            State.Error = "";
        }

        Task.Run(() => Worker(prune, folderAsStudio, twoPartActor));
    }

    private static void Worker(bool prune, bool folderAsStudio, bool twoPartActor)
    {
        try
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var locations = Db.Query("SELECT * FROM locations WHERE enabled = 1");

            foreach (var location in locations)
            {
                var root = location["path"] as string;
                if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) continue;

                IEnumerable<string> files;
                try
                {
                    files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
                }
                catch (Exception ex)
                {
                    AppPaths.Log($"[scan] {root}: {ex.Message}");
                    continue;
                }

                foreach (var file in files)
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith('.')) continue;
                    if (!AppPaths.VideoExtensions.Contains(Path.GetExtension(file))) continue;

                    lock (Gate)
                    {
                        State.Found++;
                        State.Current = name;
                    }
                    seen.Add(file);
                    Ingest(file, root, Convert.ToInt64(location["id"]), folderAsStudio, twoPartActor);
                }
            }

            if (prune)
            {
                foreach (var row in Db.Query("SELECT id, path FROM videos"))
                {
                    var path = row["path"] as string;
                    if (path is null || seen.Contains(path) || File.Exists(path)) continue;
                    Db.Execute("UPDATE videos SET missing = 1 WHERE id = @p0", row["id"]);
                    lock (Gate) State.Removed++;
                }
            }

            lock (Gate) State.Phase = "assets";
        }
        catch (Exception ex)
        {
            lock (Gate) State.Error = ex.Message;
            AppPaths.Log($"[scan] {ex}");
        }
        finally
        {
            lock (Gate)
            {
                State.Running = false;
                State.Phase = "done";
                State.Current = "";
            }
        }
    }

    private static void Ingest(string file, string root, long locationId, bool folderAsStudio,
                               bool twoPartActor)
    {
        using var connection = Db.Open();

        var existing = Db.QueryOne(connection, "SELECT id, missing FROM videos WHERE path = @p0", file);
        if (existing != null)
        {
            if (Convert.ToInt64(existing["missing"]) != 0)
            {
                Db.Execute(connection, "UPDATE videos SET missing = 0 WHERE id = @p0", existing["id"]);
                lock (Gate) State.Updated++;
            }
            return;
        }

        var info = Media.Probe(file);
        var parsed = ParseFilename(file, root, folderAsStudio, twoPartActor);

        object studioId = DBNull.Value;
        if (parsed.Studio.Length > 0)
        {
            var id = Db.GetOrCreate(connection, "studios", parsed.Studio);
            if (id > 0) studioId = id;
        }

        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }

        var videoId = Db.Insert(connection,
            @"INSERT INTO videos (path, location_id, title, studio_id, release_date,
                                  duration, width, height, filesize)
              VALUES (@p0,@p1,@p2,@p3,@p4,@p5,@p6,@p7,@p8)",
            file, locationId, parsed.Title, studioId,
            string.IsNullOrEmpty(parsed.ReleaseDate) ? DBNull.Value : parsed.ReleaseDate,
            info.Duration, info.Width, info.Height, size);

        foreach (var actor in parsed.Actors)
        {
            var actorId = Db.GetOrCreate(connection, "actors", actor);
            if (actorId > 0)
                Db.Execute(connection,
                    "INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (@p0,@p1)",
                    videoId, actorId);
        }

        lock (Gate) State.Added++;
        QueueAssets(videoId, file, info.Duration);
    }

    // -------------------------------------------------------- artwork jobs

    public static void QueueAssets(long videoId, string path, double duration, bool force = false)
    {
        lock (Gate) State.AssetsTotal++;

        Task.Run(async () =>
        {
            await AssetWorkers.WaitAsync();
            try
            {
                GenerateAssets(videoId, path, duration, force);
            }
            finally
            {
                AssetWorkers.Release();
                lock (Gate) State.AssetsDone++;
            }
        });
    }

    private static void GenerateAssets(long videoId, string path, double duration, bool force)
    {
        try
        {
            lock (Gate) State.Current = Path.GetFileName(path);

            var row = Db.QueryOne("SELECT thumb_custom, width FROM videos WHERE id = @p0", videoId);
            var custom = row != null && Convert.ToInt64(row["thumb_custom"]) != 0;
            var sourceWidth = row != null && row["width"] != null ? Convert.ToInt32(row["width"]) : 0;

            // A failed earlier probe leaves these at zero; retry so the HD/SD
            // badge and duration fill in on a rebuild.
            if (duration <= 0 || sourceWidth <= 0)
            {
                var info = Media.Probe(path);
                if (info.Duration > 0 || info.Width > 0)
                {
                    duration = info.Duration > 0 ? info.Duration : duration;
                    sourceWidth = info.Width > 0 ? info.Width : sourceWidth;
                    Db.Execute("UPDATE videos SET duration = @p0, width = @p1, height = @p2 WHERE id = @p3",
                               info.Duration, info.Width, info.Height, videoId);
                }
            }

            if (!custom)
            {
                var thumb = Media.MakeThumbnail(path, duration, force, sourceWidth);
                if (thumb != null)
                    Db.Execute("UPDATE videos SET thumb = @p0 WHERE id = @p1", thumb, videoId);
            }

            var preview = Media.MakePreview(path, duration, force, sourceWidth);
            if (preview != null)
                Db.Execute("UPDATE videos SET preview = @p0, preview_width = @p1 WHERE id = @p2",
                           preview, Media.PreviewWidthFor(sourceWidth), videoId);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[assets] {path}: {ex.Message}");
        }
    }

    public static int RebuildAssets(bool onlyMissing = true)
    {
        var sql = "SELECT id, path, duration FROM videos WHERE missing = 0";
        if (onlyMissing) sql += " AND (thumb IS NULL OR preview IS NULL)";

        var rows = Db.Query(sql);
        lock (Gate)
        {
            State.AssetsTotal = 0;
            State.AssetsDone = 0;
            State.Error = "";
            State.Phase = "assets";
        }

        foreach (var row in rows)
        {
            QueueAssets(Convert.ToInt64(row["id"]), row["path"] as string,
                        row["duration"] is null ? 0 : Convert.ToDouble(row["duration"]), !onlyMissing);
        }
        return rows.Count;
    }
}
