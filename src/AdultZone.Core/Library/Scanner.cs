using System.Collections.Concurrent;
using AdultZone.Core.Data;
using AdultZone.Core.Media;

namespace AdultZone.Core.Library;

/// <summary>What the scan and the artwork builder are doing right now.</summary>
public sealed record ScanState(bool Running, string Phase, int Found, int Added, int Updated, int Removed,
                               int AssetsTotal, int AssetsDone, string Current, string Error)
{
    public bool Building => AssetsTotal > AssetsDone;
    public bool Busy => Running || Building;
}

/// <summary>
/// Walks the storage folders and records new videos. Poster frames and
/// previews are made afterwards by two background workers, so the library
/// can be browsed long before every one is ready.
/// </summary>
public static class Scanner
{
    static readonly object Gate = new();
    static bool _running;
    static string _phase = "idle", _current = "", _error = "";
    static int _found, _added, _updated, _removed, _assetsTotal, _assetsDone;

    static readonly BlockingCollection<(long Id, string Path, double Duration, bool Force)> Jobs = new();
    static bool _workersStarted;

    /// <summary>Raised whenever a poster or preview is finished, and when a scan ends.</summary>
    public static event Action? Changed;

    public static ScanState State
    {
        get
        {
            lock (Gate) return new ScanState(_running, _phase, _found, _added, _updated, _removed, _assetsTotal, _assetsDone, _current, _error);
        }
    }

    static void Set(Action change)
    {
        lock (Gate) change();
    }

    // ------------------------------------------------------------ artwork
    static void StartWorkers()
    {
        lock (Gate)
        {
            if (_workersStarted) return;
            _workersStarted = true;
        }
        for (var i = 0; i < 2; i++)
            new Thread(Work) { IsBackground = true, Name = "Adult Zone artwork", Priority = ThreadPriority.BelowNormal }.Start();
    }

    static void Work()
    {
        foreach (var job in Jobs.GetConsumingEnumerable())
        {
            try { Generate(job.Id, job.Path, job.Duration, job.Force); }
            catch (Exception ex) { Log?.Invoke($"[artwork] {job.Path}: {ex.Message}"); }
            finally
            {
                Set(() => _assetsDone++);
                Changed?.Invoke();
            }
        }
    }

    public static Action<string>? Log;

    static void Generate(long id, string path, double duration, bool force)
    {
        Set(() => _current = System.IO.Path.GetFileName(path));
        if (!Ffmpeg.Available)
        {
            Set(() => _error = "ffmpeg was not found");
            return;
        }
        var row = Db.QueryOne("SELECT thumb_custom, width FROM videos WHERE id = ?", id);
        if (row == null) return;
        var custom = row.Truthy("thumb_custom");
        var width = row.Int("width") ?? 0;

        // Probing may have failed before (no ffmpeg then). Try again, so the
        // length and the HD/SD badge fill in.
        if (duration <= 0 || width <= 0)
        {
            var info = Assets.Probe(path);
            if (info.Duration > 0 || info.Width > 0)
            {
                duration = info.Duration > 0 ? info.Duration : duration;
                width = info.Width > 0 ? info.Width : width;
                Db.Execute("UPDATE videos SET duration = ?, width = ?, height = ? WHERE id = ?", info.Duration, info.Width, info.Height, id);
            }
        }
        if (!custom && Assets.MakeThumbnail(path, duration, force, width) is { } thumb)
            Db.Execute("UPDATE videos SET thumb = ? WHERE id = ?", thumb, id);
        if (Assets.MakePreview(path, duration, force, width) is { } preview)
            Db.Execute("UPDATE videos SET preview = ?, preview_width = ? WHERE id = ?", preview, Assets.PreviewWidthFor(width), id);
    }

    public static void Queue(long id, string path, double duration, bool force = false)
    {
        StartWorkers();
        Set(() => _assetsTotal++);
        Jobs.Add((id, path, duration, force));
    }

    /// <summary>Poster and preview made for one video again from scratch.</summary>
    public static void Rebuild(long id)
    {
        var row = Db.QueryOne("SELECT path, duration FROM videos WHERE id = ?", id);
        if (row == null) return;
        Db.Execute("UPDATE videos SET thumb_custom = 0 WHERE id = ?", id);
        Set(() => _phase = "assets");
        Queue(id, row.Str("path"), row.Double("duration") ?? 0, true);
    }

    /// <summary>Artwork for every video missing some — or, with <paramref name="onlyMissing"/> off, for all of them.</summary>
    public static int RebuildAll(bool onlyMissing)
    {
        var sql = "SELECT id, path, duration FROM videos WHERE missing = 0";
        if (onlyMissing) sql += " AND (thumb IS NULL OR preview IS NULL)";
        var rows = Db.Query(sql);
        Set(() =>
        {
            if (_assetsDone >= _assetsTotal) { _assetsTotal = 0; _assetsDone = 0; }
            _error = "";
            _phase = "assets";
        });
        foreach (var r in rows) Queue(r.Long("id") ?? 0, r.Str("path"), r.Double("duration") ?? 0, !onlyMissing);
        return rows.Count;
    }

    // -------------------------------------------------------------- scanning
    public static bool ScanAll()
    {
        lock (Gate)
        {
            if (_running) return false;
            _running = true;
            _phase = "scanning";
            _found = _added = _updated = _removed = 0;
            if (_assetsDone >= _assetsTotal) { _assetsTotal = 0; _assetsDone = 0; }
            _current = "";
            _error = "";
        }
        new Thread(ScanWorker) { IsBackground = true, Name = "Adult Zone scan" }.Start();
        return true;
    }

    static void ScanWorker()
    {
        var folderAsStudio = Db.SettingOn("folder_as_studio");
        var twoPart = Db.SettingOn("two_part_actor");
        try
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var loc in Db.Query("SELECT * FROM locations WHERE enabled = 1"))
            {
                var root = loc.Str("path");
                if (!Directory.Exists(root)) continue;
                foreach (var file in Files(root))
                {
                    Set(() => { _found++; _current = System.IO.Path.GetFileName(file); });
                    seen.Add(file);
                    try { Ingest(file, root, loc.Long("id") ?? 0, folderAsStudio, twoPart); }
                    catch (Exception ex) { Log?.Invoke($"[scan] {file}: {ex.Message}"); }
                }
            }

            // A file counts as gone only when its folder can be reached: an
            // unplugged drive never empties the library.
            var roots = Db.Query("SELECT id, path FROM locations").ToDictionary(r => r.Long("id") ?? 0, r => r.Str("path"));
            foreach (var row in Db.Query("SELECT id, path, location_id FROM videos WHERE missing = 0"))
            {
                var path = row.Str("path");
                if (seen.Contains(path) || File.Exists(path)) continue;
                if (roots.TryGetValue(row.Long("location_id") ?? 0, out var root) && !Directory.Exists(root)) continue;
                var drive = System.IO.Path.GetPathRoot(path);
                if (!string.IsNullOrEmpty(drive) && !Directory.Exists(drive)) continue;
                Db.Execute("UPDATE videos SET missing = 1 WHERE id = ?", row.Long("id"));
                Set(() => _removed++);
            }
            Set(() => _phase = "assets");
        }
        catch (Exception ex)
        {
            Set(() => _error = ex.Message);
            Log?.Invoke("[scan] " + ex);
        }
        finally
        {
            Set(() => { _running = false; _current = ""; if (_phase == "scanning") _phase = "done"; });
            Changed?.Invoke();
        }
    }

    static IEnumerable<string> Files(string root)
    {
        IEnumerable<string> all;
        try
        {
            all = Directory.EnumerateFiles(root, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            yield break;
        }
        foreach (var file in all.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var name = System.IO.Path.GetFileName(file);
            if (name.StartsWith('.') || !Config.VideoExts.Contains(System.IO.Path.GetExtension(file))) continue;
            yield return file;
        }
    }

    /// <summary>What a file's own name and folder say about it.</summary>
    static Parsed Read(string file, string root, string kind, bool folderAsStudio, bool twoPart)
    {
        var meta = Names.Parse(file, root, folderAsStudio, twoPart);
        if (kind == "movie")
        {
            // A movie's file name is its title; "Studio - Actor" reading is for scenes.
            var full = Names.Parse(file, root, false, false);
            meta = new Parsed { Title = Names.TitleCase(Names.Clean(Path.GetFileNameWithoutExtension(file))), ReleaseDate = full.ReleaseDate };
            if (meta.ReleaseDate.Length >= 4) meta.Title = meta.Title.Replace(meta.ReleaseDate[..4], "").Trim(' ', '-');
            if (meta.Title.Length == 0) meta.Title = Path.GetFileNameWithoutExtension(file);
        }
        return meta;
    }

    /// <summary>
    /// A video put back to what its file name says: fetched title, description, studio,
    /// cast, tags and picture all dropped, and a frame from the video as its thumbnail again.
    /// </summary>
    public static void Reset(long id)
    {
        var row = Db.QueryOne("SELECT path, kind, cover FROM videos WHERE id = ?", id);
        if (row == null) return;
        var file = row.Str("path");
        var root = Db.Query("SELECT path FROM locations").Select(l => l.Str("path"))
            .Where(l => file.StartsWith(l.TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Length).FirstOrDefault() ?? Path.GetDirectoryName(file) ?? "";
        var meta = Read(file, root, row.Str("kind") == "movie" ? "movie" : "scene", Db.SettingOn("folder_as_studio"), Db.SettingOn("two_part_actor"));
        Db.InTransaction(() =>
        {
            Db.Execute("UPDATE videos SET title = ?, description = '', studio_id = ?, subsite = NULL, release_date = ?, cover = NULL WHERE id = ?",
                meta.Title, Db.GetOrCreate("studios", meta.Studio), meta.ReleaseDate.Length > 0 ? meta.ReleaseDate : null, id);
            Db.Execute("DELETE FROM video_actors WHERE video_id = ?", id);
            Db.Execute("DELETE FROM video_tags WHERE video_id = ?", id);
            foreach (var actor in meta.Actors)
                if (Db.GetOrCreate("actors", actor) is long actorId)
                    Db.Execute("INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (?,?)", id, actorId);
        });
        if (row.Str("cover") is { Length: > 0 } cover)
            try { File.Delete(Path.Combine(Config.ThumbDir, cover)); } catch { }
        Catalog.Touch();
        Rebuild(id);
    }

    static void Ingest(string file, string root, long locationId, bool folderAsStudio, bool twoPart)
    {
        var existing = Db.QueryOne("SELECT id, missing FROM videos WHERE path = ?", file);
        if (existing != null)
        {
            if (existing.Truthy("missing"))
            {
                Db.Execute("UPDATE videos SET missing = 0 WHERE id = ?", existing.Long("id"));
                Set(() => _updated++);
            }
            return;
        }

        var info = Assets.Probe(file);
        // A movies folder inside a scenes folder (or the other way round): the closest one decides.
        var owner = Db.Query("SELECT id, path, kind FROM locations")
            .Where(l => file.StartsWith(l.Str("path").TrimEnd('\\', '/') + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(l => l.Str("path").Length).FirstOrDefault();
        var ownerId = owner?.Long("id") ?? locationId;
        var kind = owner?.Str("kind") is "movie" ? "movie" : "scene";
        var meta = Read(file, root, kind, folderAsStudio, twoPart);
        var studioId = Db.GetOrCreate("studios", meta.Studio);
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        var id = Db.Insert(
            "INSERT INTO videos (path, location_id, title, studio_id, release_date, duration, width, height, filesize, kind) " +
            "VALUES (?,?,?,?,?,?,?,?,?,?)",
            file, ownerId, meta.Title, studioId, meta.ReleaseDate.Length > 0 ? meta.ReleaseDate : null,
            info.Duration, info.Width, info.Height, size, kind);
        foreach (var actor in meta.Actors)
            if (Db.GetOrCreate("actors", actor) is long actorId)
                Db.Execute("INSERT OR IGNORE INTO video_actors (video_id, actor_id) VALUES (?,?)", id, actorId);
        Set(() => _added++);
        Queue(id, file, info.Duration);
    }
}
