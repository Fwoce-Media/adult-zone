using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;

namespace AdultZone;

/// <summary>
/// The HTTP surface the interface talks to. Route names and payload shapes
/// match the Python build, because the frontend is carried over unchanged.
/// </summary>
public static class Api
{
    private static readonly Dictionary<string, string> VideoOrders = new()
    {
        ["added"] = "v.added_at DESC, v.id DESC",
        ["views"] = "v.views DESC, v.added_at DESC",
        ["title"] = "v.title COLLATE NOCASE ASC",
        ["date"] = "COALESCE(v.release_date,'0000') DESC",
        ["date_asc"] = "COALESCE(v.release_date,'9999') ASC",
        ["duration"] = "v.duration DESC",
        ["shortest"] = "v.duration ASC",
        ["random"] = "RANDOM()"
    };

    private static string Order(string sort) =>
        VideoOrders.TryGetValue(sort ?? "", out var clause) ? clause : VideoOrders["added"];

    internal static long AssetVersion(string folder, string name)
    {
        if (string.IsNullOrEmpty(name)) return 0;
        try
        {
            var path = Path.Combine(folder, name);
            return File.Exists(path)
                ? new DateTimeOffset(File.GetLastWriteTimeUtc(path)).ToUnixTimeSeconds()
                : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static int AsInt(object value) => value is null ? 0 : Convert.ToInt32(value);
    private static long AsLong(object value) => value is null ? 0 : Convert.ToInt64(value);
    private static double AsDouble(object value) => value is null ? 0 : Convert.ToDouble(value);

    // --------------------------------------------------------- serializers

    private static Dictionary<string, object> VideoRow(Dictionary<string, object> row)
    {
        var video = new Dictionary<string, object>(row, StringComparer.OrdinalIgnoreCase);
        var width = AsInt(row.GetValueOrDefault("width"));
        var height = AsInt(row.GetValueOrDefault("height"));

        video["quality"] = Media.QualityLabel(height);
        video["resolution"] = $"{width}x{height}";
        video["has_preview"] = row.GetValueOrDefault("preview") != null;
        video["thumb_v"] = AssetVersion(AppPaths.ThumbDir, row.GetValueOrDefault("thumb") as string);
        video["preview_v"] = AssetVersion(AppPaths.PreviewDir, row.GetValueOrDefault("preview") as string);
        video["filename"] = row.GetValueOrDefault("path") is string path ? Path.GetFileName(path) : "";
        video["actors"] = new List<object>();
        video["tags"] = new List<string>();
        return video;
    }

    /// <summary>
    /// Builds many video payloads with a fixed number of queries. One at a time
    /// costs two extra per video, which is hundreds on a large studio page.
    /// </summary>
    internal static List<Dictionary<string, object>> VideoRows(List<Dictionary<string, object>> rows)
    {
        var items = rows.Select(VideoRow).ToList();
        if (items.Count == 0) return items;

        var ids = items.Select(item => AsLong(item["id"])).ToList();
        var cast = new Dictionary<long, List<object>>();
        var tags = new Dictionary<long, List<string>>();

        using var connection = Db.Open();
        for (var start = 0; start < ids.Count; start += 400)
        {
            var chunk = ids.Skip(start).Take(400).ToArray();
            var marks = string.Join(",", chunk.Select((_, i) => $"@p{i}"));
            var parameters = chunk.Cast<object>().ToArray();

            foreach (var row in Db.Query(connection,
                $@"SELECT va.video_id, a.id, a.name, a.image FROM actors a
                   JOIN video_actors va ON va.actor_id = a.id
                   WHERE va.video_id IN ({marks}) ORDER BY a.name COLLATE NOCASE", parameters))
            {
                var videoId = AsLong(row["video_id"]);
                if (!cast.TryGetValue(videoId, out var list)) cast[videoId] = list = new List<object>();
                var image = row["image"] as string;
                list.Add(new Dictionary<string, object>
                {
                    ["id"] = row["id"],
                    ["name"] = row["name"],
                    ["image"] = image,
                    ["image_v"] = AssetVersion(AppPaths.ActorDir, image)
                });
            }

            foreach (var row in Db.Query(connection,
                $@"SELECT vt.video_id, t.name FROM tags t
                   JOIN video_tags vt ON vt.tag_id = t.id
                   WHERE vt.video_id IN ({marks}) ORDER BY t.name COLLATE NOCASE", parameters))
            {
                var videoId = AsLong(row["video_id"]);
                if (!tags.TryGetValue(videoId, out var list)) tags[videoId] = list = new List<string>();
                list.Add(row["name"] as string);
            }
        }

        foreach (var item in items)
        {
            var id = AsLong(item["id"]);
            if (cast.TryGetValue(id, out var castList)) item["actors"] = castList;
            if (tags.TryGetValue(id, out var tagList)) item["tags"] = tagList;
        }
        return items;
    }

    /// <summary>One video in the shape GET /api/videos/{id} returns, or null.</summary>
    internal static Dictionary<string, object> VideoPayload(long id)
    {
        var row = Db.QueryOne(
            @"SELECT v.*, s.name AS studio_name FROM videos v
              LEFT JOIN studios s ON s.id = v.studio_id WHERE v.id = @p0", id);
        if (row is null) return null;

        var video = VideoRows(new List<Dictionary<string, object>> { row })[0];
        video["exists"] = row["path"] is string path && File.Exists(path);
        return video;
    }

    // ------------------------------------------------------------- routing

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/version", () => Results.Ok(new
        {
            version = AppPaths.Version,
            assets = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        }));

        app.MapGet("/api/stats", () => Results.Ok(new
        {
            videos = Db.Scalar("SELECT COUNT(*) FROM videos WHERE missing = 0"),
            actors = Db.Scalar("SELECT COUNT(*) FROM actors"),
            studios = Db.Scalar("SELECT COUNT(*) FROM studios"),
            tags = Db.Scalar("SELECT COUNT(*) FROM tags"),
            missing = Db.Scalar("SELECT COUNT(*) FROM videos WHERE missing = 1"),
            ffmpeg = AppPaths.FFmpegAvailable(),
            ffmpeg_bin = AppPaths.FFmpeg,
            ffprobe_bin = AppPaths.FFprobe,
            no_artwork = Db.Scalar(
                "SELECT COUNT(*) FROM videos WHERE missing = 0 AND (thumb IS NULL OR preview IS NULL)"),
            preview_quality = Media.ProfileName(),
            preview_profiles = new Dictionary<string, object>
            {
                ["sd"] = new { width = 640 },
                ["high"] = new { width = 1280 },
                ["max"] = new { width = 1920 }
            },
            below_quality = 0
        }));

        MapVideos(app);
        MapPeople(app);
        MapLibrary(app);
        MapMedia(app);
        MapShell(app);
        Editing.Map(app);
    }

    private static void MapVideos(WebApplication app)
    {
        app.MapGet("/api/videos", (string search, int? actor_id, int? studio_id, string tag,
                                   string quality, int? favorite, string sort, int? limit, int? offset) =>
        {
            var where = new List<string> { "v.missing = 0" };
            var parameters = new List<object>();

            void Add(string clause, params object[] values)
            {
                where.Add(clause);
                parameters.AddRange(values);
            }

            if (!string.IsNullOrWhiteSpace(search))
            {
                var like = $"%{search}%";
                Add(@"(v.title LIKE @@ OR v.description LIKE @@ OR v.path LIKE @@
                       OR EXISTS (SELECT 1 FROM video_actors va JOIN actors a ON a.id = va.actor_id
                                  WHERE va.video_id = v.id AND a.name LIKE @@)
                       OR EXISTS (SELECT 1 FROM video_tags vt JOIN tags t ON t.id = vt.tag_id
                                  WHERE vt.video_id = v.id AND t.name LIKE @@)
                       OR EXISTS (SELECT 1 FROM studios s WHERE s.id = v.studio_id AND s.name LIKE @@)
                       OR v.subsite LIKE @@)",
                    like, like, like, like, like, like, like);
            }

            if (actor_id is > 0)
                Add("EXISTS (SELECT 1 FROM video_actors va WHERE va.video_id = v.id AND va.actor_id = @@)",
                    actor_id.Value);

            if (studio_id is > 0)
                Add("v.studio_id = @@", studio_id.Value);

            if (!string.IsNullOrWhiteSpace(tag))
                Add(@"(EXISTS (SELECT 1 FROM video_tags vt JOIN tags t ON t.id = vt.tag_id
                               WHERE vt.video_id = v.id AND t.name = @@ COLLATE NOCASE)
                       OR v.subsite = @@ COLLATE NOCASE)", tag, tag);

            switch (quality)
            {
                case "HD": Add("v.height >= 700 AND v.height < 1400"); break;
                case "SD": Add("v.height > 0 AND v.height < 700"); break;
                case "2K": Add("v.height >= 1400 AND v.height < 2000"); break;
                case "4K": Add("v.height >= 2000"); break;
            }

            if (favorite is 1) Add("v.favorite = 1");

            var clause = string.Join(" AND ", where);
            var numbered = Number(clause);

            var sql = $@"SELECT v.*, s.name AS studio_name FROM videos v
                         LEFT JOIN studios s ON s.id = v.studio_id
                         WHERE {numbered} ORDER BY {Order(sort)}";

            List<Dictionary<string, object>> rows;
            if (limit is > 0)
            {
                var all = parameters.ToList();
                all.Add(limit.Value);
                all.Add(offset ?? 0);
                rows = Db.Query(sql + $" LIMIT @p{parameters.Count} OFFSET @p{parameters.Count + 1}",
                                all.ToArray());
            }
            else
            {
                rows = Db.Query(sql, parameters.ToArray());
            }

            var total = Db.Scalar($"SELECT COUNT(*) FROM videos v WHERE {numbered}", parameters.ToArray());
            return Results.Ok(new { total, items = VideoRows(rows) });
        });

        app.MapGet("/api/videos/{id:long}", (long id) =>
        {
            var video = VideoPayload(id);
            return video is null ? Results.NotFound(new { detail = "Video not found" }) : Results.Ok(video);
        });

        app.MapPost("/api/videos/{id:long}/view", (long id) =>
        {
            Db.Execute("UPDATE videos SET views = views + 1, last_played = datetime('now') WHERE id = @p0", id);
            return Results.Ok(new { views = Db.Scalar("SELECT views FROM videos WHERE id = @p0", id) });
        });

        app.MapGet("/api/videos/{id:long}/related", (long id, string source, int? limit) =>
        {
            var take = limit ?? 24;
            var baseRow = Db.QueryOne("SELECT id, studio_id FROM videos WHERE id = @p0", id);
            if (baseRow is null) return Results.NotFound(new { detail = "Video not found" });

            List<Dictionary<string, object>> rows;
            if (source == "studio")
            {
                rows = baseRow["studio_id"] is null
                    ? new List<Dictionary<string, object>>()
                    : Db.Query(
                        @"SELECT v.*, s.name AS studio_name FROM videos v
                          LEFT JOIN studios s ON s.id = v.studio_id
                          WHERE v.studio_id = @p0 AND v.id != @p1 AND v.missing = 0
                          ORDER BY v.added_at DESC LIMIT @p2",
                        baseRow["studio_id"], id, take);
            }
            else if (source == "tags")
            {
                rows = Db.Query(
                    @"SELECT v.*, s.name AS studio_name, COUNT(*) AS shared FROM videos v
                      JOIN video_tags vt ON vt.video_id = v.id
                      LEFT JOIN studios s ON s.id = v.studio_id
                      WHERE v.id != @p0 AND v.missing = 0 AND vt.tag_id IN
                            (SELECT tag_id FROM video_tags WHERE video_id = @p1)
                      GROUP BY v.id ORDER BY shared DESC, v.added_at DESC LIMIT @p2", id, id, take);
            }
            else
            {
                rows = Db.Query(
                    @"SELECT v.*, s.name AS studio_name, COUNT(*) AS shared FROM videos v
                      JOIN video_actors va ON va.video_id = v.id
                      LEFT JOIN studios s ON s.id = v.studio_id
                      WHERE v.id != @p0 AND v.missing = 0 AND va.actor_id IN
                            (SELECT actor_id FROM video_actors WHERE video_id = @p1)
                      GROUP BY v.id ORDER BY shared DESC, v.views DESC LIMIT @p2", id, id, take);
            }

            return Results.Ok(new { source = source ?? "cast", items = VideoRows(rows) });
        });
    }

    private static void MapPeople(WebApplication app)
    {
        // Instant results under the search box: performers with their photo,
        // studios with their logo, and videos with their thumbnail. Names that
        // start with what was typed rank above names that merely contain it.
        app.MapGet("/api/suggest", (string q) =>
        {
            q = (q ?? "").Trim();
            if (q.Length == 0)
                return Results.Ok(new { performers = Array.Empty<object>(), studios = Array.Empty<object>(),
                                        videos = Array.Empty<object>() });

            var like = $"%{q}%";
            var prefix = $"{q}%";

            var performers = Db.Query(
                @"SELECT a.id, a.name, a.image,
                         (SELECT COUNT(*) FROM video_actors va JOIN videos v ON v.id = va.video_id
                          WHERE va.actor_id = a.id AND v.missing = 0) AS video_count
                  FROM actors a
                  WHERE a.name LIKE @p0 AND COALESCE(a.hidden, 0) = 0
                  ORDER BY (a.name LIKE @p1) DESC, video_count DESC, a.name COLLATE NOCASE
                  LIMIT 5", like, prefix);
            foreach (var row in performers)
                row["image_v"] = AssetVersion(AppPaths.ActorDir, row["image"] as string);

            var studios = Db.Query(
                @"SELECT s.id, s.name, s.image, s.logo_fit, s.logo_zoom, s.logo_x, s.logo_y, s.logo_bg,
                         (SELECT COUNT(*) FROM videos v WHERE v.studio_id = s.id AND v.missing = 0) AS video_count
                  FROM studios s WHERE s.name LIKE @p0
                  ORDER BY (s.name LIKE @p1) DESC, video_count DESC
                  LIMIT 3", like, prefix);
            foreach (var row in studios)
                row["image_v"] = AssetVersion(AppPaths.StudioDir, row["image"] as string);

            // Titles that match first, then videos whose cast matches, newest first.
            var videos = Db.Query(
                @"SELECT v.id, v.title, v.thumb, v.release_date, v.height, s.name AS studio_name,
                         (v.title LIKE @p0) AS title_hit
                  FROM videos v LEFT JOIN studios s ON s.id = v.studio_id
                  WHERE v.missing = 0 AND (
                        v.title LIKE @p0
                        OR v.subsite LIKE @p0
                        OR EXISTS (SELECT 1 FROM video_actors va JOIN actors a ON a.id = va.actor_id
                                   WHERE va.video_id = v.id AND a.name LIKE @p0))
                  ORDER BY title_hit DESC, v.added_at DESC
                  LIMIT 6", like);
            foreach (var row in videos)
            {
                row["thumb_v"] = AssetVersion(AppPaths.ThumbDir, row["thumb"] as string);
                row["quality"] = Media.QualityLabel(AsInt(row["height"]));
            }

            return Results.Ok(new { performers, studios, videos });
        });

        app.MapGet("/api/actors", (string search, string sort, int? limit, int? include_hidden) =>
        {
            var clauses = new List<string>();
            var parameters = new List<object>();

            if (!string.IsNullOrWhiteSpace(search))
            {
                clauses.Add("a.name LIKE @@");
                parameters.Add($"%{search}%");
            }
            if (include_hidden is not 1) clauses.Add("COALESCE(a.hidden, 0) = 0");

            var where = clauses.Count > 0 ? "WHERE " + Number(string.Join(" AND ", clauses)) : "";
            var order = sort == "count"
                ? "video_count DESC, a.name COLLATE NOCASE"
                : "a.name COLLATE NOCASE";

            var sql = $@"SELECT a.*, (SELECT COUNT(*) FROM video_actors va JOIN videos v ON v.id = va.video_id
                                      WHERE va.actor_id = a.id AND v.missing = 0) AS video_count
                         FROM actors a {where} ORDER BY {order}";

            var rows = limit is > 0
                ? Db.Query(sql + $" LIMIT @p{parameters.Count}",
                           parameters.Append((object)limit.Value).ToArray())
                : Db.Query(sql, parameters.ToArray());

            foreach (var row in rows)
            {
                row["image_v"] = AssetVersion(AppPaths.ActorDir, row.GetValueOrDefault("image") as string);
                row["banner_v"] = AssetVersion(AppPaths.ActorDir, row.GetValueOrDefault("banner") as string);
            }

            return Results.Ok(new
            {
                items = rows,
                total = rows.Count,
                hidden = Db.Scalar("SELECT COUNT(*) FROM actors WHERE COALESCE(hidden, 0) = 1")
            });
        });

        app.MapGet("/api/actors/{id:long}", (long id, string sort) =>
        {
            var row = Db.QueryOne("SELECT * FROM actors WHERE id = @p0", id);
            if (row is null) return Results.NotFound(new { detail = "Star not found" });

            row["image_v"] = AssetVersion(AppPaths.ActorDir, row.GetValueOrDefault("image") as string);
            row["banner_v"] = AssetVersion(AppPaths.ActorDir, row.GetValueOrDefault("banner") as string);
            row["video_count"] = Db.Scalar(
                @"SELECT COUNT(*) FROM video_actors va JOIN videos v ON v.id = va.video_id
                  WHERE va.actor_id = @p0 AND v.missing = 0", id);
            row["sort"] = VideoOrders.ContainsKey(sort ?? "") ? sort : "added";
            row["videos"] = VideoRows(Db.Query(
                $@"SELECT v.*, s.name AS studio_name FROM videos v
                   JOIN video_actors va ON va.video_id = v.id
                   LEFT JOIN studios s ON s.id = v.studio_id
                   WHERE va.actor_id = @p0 AND v.missing = 0
                   ORDER BY {Order(sort)}", id));

            return Results.Ok(row);
        });

        app.MapGet("/api/studios", (string search, string sort) =>
        {
            var where = string.IsNullOrWhiteSpace(search) ? "" : "WHERE s.name LIKE @p0";
            var order = sort == "count"
                ? "video_count DESC, s.name COLLATE NOCASE"
                : "s.name COLLATE NOCASE";

            var sql = $@"SELECT s.*, (SELECT COUNT(*) FROM videos v
                                      WHERE v.studio_id = s.id AND v.missing = 0) AS video_count
                         FROM studios s {where} ORDER BY {order}";

            var rows = string.IsNullOrWhiteSpace(search)
                ? Db.Query(sql)
                : Db.Query(sql, $"%{search}%");

            foreach (var row in rows)
                row["image_v"] = AssetVersion(AppPaths.StudioDir, row.GetValueOrDefault("image") as string);

            return Results.Ok(new { items = rows });
        });

        app.MapGet("/api/studios/{id:long}", (long id, string sort) =>
        {
            var row = Db.QueryOne("SELECT * FROM studios WHERE id = @p0", id);
            if (row is null) return Results.NotFound(new { detail = "Studio not found" });

            row["image_v"] = AssetVersion(AppPaths.StudioDir, row.GetValueOrDefault("image") as string);
            row["sort"] = VideoOrders.ContainsKey(sort ?? "") ? sort : "added";
            var videos = VideoRows(Db.Query(
                $@"SELECT v.*, s.name AS studio_name FROM videos v
                   LEFT JOIN studios s ON s.id = v.studio_id
                   WHERE v.studio_id = @p0 AND v.missing = 0
                   ORDER BY {Order(sort)}", id));
            row["videos"] = videos;
            row["video_count"] = videos.Count;
            return Results.Ok(row);
        });

        app.MapGet("/api/tags", () => Results.Ok(new
        {
            items = Db.Query(
                @"SELECT t.*, (SELECT COUNT(*) FROM video_tags vt JOIN videos v ON v.id = vt.video_id
                               WHERE vt.tag_id = t.id AND v.missing = 0) AS video_count
                  FROM tags t ORDER BY video_count DESC, t.name COLLATE NOCASE")
        }));
    }

    private static void MapLibrary(WebApplication app)
    {
        app.MapGet("/api/locations", () =>
        {
            var rows = Db.Query("SELECT * FROM locations ORDER BY id");
            foreach (var row in rows)
            {
                var path = row["path"] as string;
                row["exists"] = path != null && Directory.Exists(path);
                row["video_count"] = Db.Scalar(
                    "SELECT COUNT(*) FROM videos WHERE location_id = @p0 AND missing = 0", row["id"]);
            }
            return Results.Ok(new { items = rows });
        });

        app.MapPost("/api/locations", async (HttpRequest request) =>
        {
            var body = await ReadJson(request);
            var raw = (GetString(body, "path") ?? "").Trim().Trim('"');
            if (raw.Length == 0) return Results.BadRequest(new { detail = "Enter a folder path" });

            var path = Environment.ExpandEnvironmentVariables(raw);
            if (!Directory.Exists(path))
                return Results.BadRequest(new { detail = $"No folder at {path}" });

            try
            {
                using var connection = Db.Open();
                var id = Db.Insert(connection, "INSERT INTO locations (path, label) VALUES (@p0,@p1)",
                                   path, GetString(body, "label") ?? Path.GetFileName(path));
                return Results.Ok(new { id });
            }
            catch
            {
                return Results.BadRequest(new { detail = "That folder is already in the library" });
            }
        });

        app.MapPut("/api/locations/{id:long}", async (long id, HttpRequest request) =>
        {
            var body = await ReadJson(request);
            var enabled = body.TryGetValue("enabled", out var value) &&
                          value is JsonElement element &&
                          element.ValueKind == JsonValueKind.True;
            Db.Execute("UPDATE locations SET enabled = @p0 WHERE id = @p1", enabled ? 1 : 0, id);
            return Results.Ok(new { ok = true });
        });

        app.MapDelete("/api/locations/{id:long}", (long id, int? purge) =>
        {
            if (purge is 1) Db.Execute("DELETE FROM videos WHERE location_id = @p0", id);
            Db.Execute("DELETE FROM locations WHERE id = @p0", id);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/browse", async (HttpRequest request) =>
        {
            var body = await ReadJson(request);
            var raw = (GetString(body, "path") ?? "").Trim();
            var path = raw.Length > 0 && Directory.Exists(raw)
                ? raw
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var directories = new List<object>();
            try
            {
                directories = Directory.EnumerateDirectories(path)
                    .Where(dir => !Path.GetFileName(dir).StartsWith('.'))
                    .OrderBy(dir => Path.GetFileName(dir), StringComparer.OrdinalIgnoreCase)
                    .Take(400)
                    .Select(dir => (object)new { name = Path.GetFileName(dir), path = dir })
                    .ToList();
            }
            catch { }

            var parent = Directory.GetParent(path)?.FullName;
            var drives = DriveInfo.GetDrives().Where(drive => drive.IsReady)
                .Select(drive => drive.Name).ToList();

            return Results.Ok(new { path, parent, dirs = directories, drives });
        });

        app.MapPost("/api/scan", (int? prune, int? folder_as_studio, int? two_part_actor) =>
        {
            Scanner.ScanAll(prune is not 0, folder_as_studio is not 0, two_part_actor is not 0);
            return Results.Ok(new { started = true });
        });

        app.MapGet("/api/scan/status", () =>
        {
            var state = Scanner.State;
            return Results.Ok(new
            {
                running = state.Running,
                phase = state.Phase,
                found = state.Found,
                added = state.Added,
                updated = state.Updated,
                removed = state.Removed,
                assets_total = state.AssetsTotal,
                assets_done = state.AssetsDone,
                current = state.Current,
                error = state.Error
            });
        });

        app.MapPost("/api/assets/rebuild", (int? only_missing) =>
        {
            if (!AppPaths.FFmpegAvailable())
                return Results.BadRequest(new
                {
                    detail = $"Cannot run '{AppPaths.FFprobe}'. Install ffmpeg, put it on PATH, and restart."
                });
            return Results.Ok(new { queued = Scanner.RebuildAssets(only_missing is not 0) });
        });

        app.MapGet("/api/warm/plan", (int? width, int? limit) =>
        {
            var targetWidth = width ?? 640;
            var rows = Db.Query(
                @"SELECT thumb FROM videos WHERE missing = 0 AND thumb IS NOT NULL
                  ORDER BY added_at DESC LIMIT @p0", limit ?? 400);

            var pending = rows.Count(row =>
            {
                var name = row["thumb"] as string;
                if (name is null) return false;
                var file = Path.Combine(AppPaths.SizedDir,
                    $"{Path.GetFileNameWithoutExtension(name)}_{targetWidth}.jpg");
                return !File.Exists(file);
            });

            return Results.Ok(new { total = rows.Count, pending });
        });

        app.MapPost("/api/warm", async (int? width, int? limit) =>
        {
            var targetWidth = width ?? 640;
            var rows = Db.Query(
                @"SELECT thumb FROM videos WHERE missing = 0 AND thumb IS NOT NULL
                  ORDER BY added_at DESC LIMIT @p0", limit ?? 400);

            // Resampling is CPU work; keep it off the request thread.
            var built = await Task.Run(() =>
                rows.Count(row => Media.SizedThumb(row["thumb"] as string, targetWidth) != null));

            return Results.Ok(new { built, considered = rows.Count });
        });
    }

    private static void MapMedia(WebApplication app)
    {
        app.MapGet("/api/thumb/{id:long}", (long id, int? w) =>
        {
            var row = Db.QueryOne("SELECT thumb FROM videos WHERE id = @p0", id);
            var name = row?["thumb"] as string;
            if (name is null) return Results.NotFound();

            if (w is > 0)
            {
                var sized = Media.SizedThumb(name, w.Value);
                if (sized != null) return ServeFile(Path.Combine(AppPaths.SizedDir, sized));
            }
            return ServeFile(Path.Combine(AppPaths.ThumbDir, name));
        });

        app.MapGet("/api/preview/{id:long}", (long id) =>
        {
            var row = Db.QueryOne("SELECT preview FROM videos WHERE id = @p0", id);
            var name = row?["preview"] as string;
            return name is null ? Results.NotFound() : ServeFile(Path.Combine(AppPaths.PreviewDir, name));
        });

        app.MapGet("/api/actor-photo/{id:long}", (long id) =>
        {
            var row = Db.QueryOne("SELECT image FROM actors WHERE id = @p0", id);
            var name = row?["image"] as string;
            return name is null ? Results.NotFound() : ServeFile(Path.Combine(AppPaths.ActorDir, name));
        });

        app.MapGet("/api/actor-banner/{id:long}", (long id) =>
        {
            var row = Db.QueryOne("SELECT banner FROM actors WHERE id = @p0", id);
            var name = row?["banner"] as string;
            return name is null ? Results.NotFound() : ServeFile(Path.Combine(AppPaths.ActorDir, name));
        });

        app.MapGet("/api/studio-image/{id:long}", (long id) =>
        {
            var row = Db.QueryOne("SELECT image FROM studios WHERE id = @p0", id);
            var name = row?["image"] as string;
            return name is null ? Results.NotFound() : ServeFile(Path.Combine(AppPaths.StudioDir, name));
        });

        // Range requests are what make seeking in the player instant.
        app.MapGet("/api/stream/{id:long}", (long id) =>
        {
            var row = Db.QueryOne("SELECT path FROM videos WHERE id = @p0", id);
            var path = row?["path"] as string;
            if (path is null) return Results.NotFound(new { detail = "Video not found" });
            if (!File.Exists(path))
                return Results.NotFound(new { detail = "The file is no longer at that location" });

            var contentType = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".mp4" or ".m4v" => "video/mp4",
                ".webm" => "video/webm",
                ".mkv" => "video/x-matroska",
                ".avi" => "video/x-msvideo",
                ".mov" => "video/quicktime",
                _ => "application/octet-stream"
            };

            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return Results.Stream(stream, contentType, enableRangeProcessing: true);
        });

        app.MapPost("/api/videos/{id:long}/regenerate", (long id) =>
        {
            var row = Db.QueryOne("SELECT id, path, duration FROM videos WHERE id = @p0", id);
            if (row is null) return Results.NotFound(new { detail = "Video not found" });

            Db.Execute("UPDATE videos SET thumb_custom = 0 WHERE id = @p0", id);
            Scanner.QueueAssets(id, row["path"] as string, AsDouble(row["duration"]), true);
            return Results.Ok(new { queued = true });
        });

        app.MapPost("/api/videos/{id:long}/reveal", (long id) =>
        {
            var row = Db.QueryOne("SELECT path FROM videos WHERE id = @p0", id);
            var path = row?["path"] as string;
            if (path is null) return Results.NotFound(new { detail = "Video not found" });

            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                return Results.Json(new { detail = $"Could not open the folder: {ex.Message}" },
                                    statusCode: 500);
            }
            return Results.Ok(new { ok = true });
        });
    }

    private static void MapShell(WebApplication app)
    {
        app.MapPost("/api/heartbeat", () => Results.Ok(new { ok = true, autoclose = false, clients = 1 }));
        app.MapPost("/api/goodbye", () => Results.Ok(new { ok = true }));

        app.MapGet("/api/live", async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-cache, no-transform";
            context.Response.ContentType = "text/event-stream";
            await context.Response.WriteAsync(": connected\n\n");
            await context.Response.Body.FlushAsync();

            while (!context.RequestAborted.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(1000, context.RequestAborted);
                    await context.Response.WriteAsync(": ping\n\n");
                    await context.Response.Body.FlushAsync();
                }
                catch
                {
                    break;
                }
            }
        });

        app.MapPost("/api/quit", () =>
        {
            Task.Run(async () =>
            {
                await Task.Delay(400);
                Shell.RequestExit();
            });
            return Results.Ok(new { ok = true, windows_closed = 1 });
        });

        app.MapGet("/api/selfcheck", () => Results.Ok(new
        {
            ok = true,
            static_dir = AppPaths.WebRoot,
            files = new { },
            checks = Array.Empty<object>(),
            stale_files = Array.Empty<string>()
        }));

    }

    // ------------------------------------------------------------- helpers

    /// <summary>Replace the @@ placeholders with numbered parameters, in order.</summary>
    private static string Number(string clause)
    {
        var index = 0;
        while (clause.Contains("@@"))
        {
            var position = clause.IndexOf("@@", StringComparison.Ordinal);
            clause = clause[..position] + $"@p{index}" + clause[(position + 2)..];
            index++;
        }
        return clause;
    }

    internal static IResult ServeFile(string path)
    {
        if (!File.Exists(path)) return Results.NotFound();

        var contentType = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };

        return Results.File(path, contentType, enableRangeProcessing: true);
    }

    private static async Task<Dictionary<string, object>> ReadJson(HttpRequest request)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, object>>(request.Body);
            return body ?? new Dictionary<string, object>();
        }
        catch
        {
            return new Dictionary<string, object>();
        }
    }

    private static string GetString(Dictionary<string, object> body, string key)
    {
        if (!body.TryGetValue(key, out var value) || value is null) return null;
        if (value is JsonElement element)
            return element.ValueKind == JsonValueKind.String ? element.GetString() : element.ToString();
        return value.ToString();
    }
}
