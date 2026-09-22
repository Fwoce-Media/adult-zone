using System.Globalization;
using System.Text.Json.Nodes;

namespace AdultZone;

/// <summary>Reading JSON request bodies whose values may be strings, numbers or nulls.</summary>
internal static class Body
{
    public static async Task<JsonObject> Read(HttpRequest request)
    {
        try
        {
            using var reader = new StreamReader(request.Body);
            var text = await reader.ReadToEndAsync();
            return JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text) as JsonObject
                   ?? new JsonObject();
        }
        catch
        {
            return new JsonObject();
        }
    }

    public static bool Has(JsonObject body, string key) => body != null && body.ContainsKey(key);

    public static string Str(JsonObject body, string key)
    {
        var node = body?[key];
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToJsonString().Trim('"');
        }
        return null;
    }

    public static int? Int(JsonObject body, string key)
    {
        var text = Str(body, key);
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (int.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var whole))
            return whole;
        if (double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var real))
            return (int)Math.Round(real);
        return null;
    }

    public static double Double(JsonObject body, string key)
    {
        var text = Str(body, key);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;
    }

    /// <summary>The way Python's truthiness read these: 1, true, "1" and non-empty text are on.</summary>
    public static bool Truthy(JsonObject body, string key)
    {
        var node = body?[key];
        if (node is null) return false;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<bool>(out var flag)) return flag;
            var text = Str(body, key) ?? "";
            return text.Length > 0 && text != "0" && !text.Equals("false", StringComparison.OrdinalIgnoreCase);
        }
        return true;
    }

    public static List<string> List(JsonObject body, string key)
    {
        var names = new List<string>();
        if (body?[key] is not JsonArray array) return names;
        foreach (var entry in array)
        {
            var text = entry is JsonValue value && value.TryGetValue<string>(out var s) ? s : entry?.ToJsonString();
            if (!string.IsNullOrWhiteSpace(text)) names.Add(text.Trim());
        }
        return names;
    }

    public static JsonObject Object(JsonObject body, string key) => body?[key] as JsonObject ?? new JsonObject();
}

/// <summary>
/// Everything that changes the library: editing, uploads, pruning, the PIN
/// lock and the metadata importer. Behaviour mirrors the Python build route
/// for route, including the rename-merges-duplicates rule.
/// </summary>
public static class Editing
{
    private static IResult Error(string message, int status = 400) =>
        Results.Json(new { detail = message }, statusCode: status);

    private static object DbValue(string text) =>
        string.IsNullOrEmpty(text) ? DBNull.Value : text;

    private static void DeleteFile(string folder, object name)
    {
        if (name is not string file || file.Length == 0) return;
        try { File.Delete(Path.Combine(folder, file)); } catch { }
    }

    private static async Task<(byte[] Data, string FileName)> ReadUpload(HttpRequest request)
    {
        if (!request.HasFormContentType) return (null, null);
        var form = await request.ReadFormAsync();
        var file = form.Files["file"] ?? form.Files.FirstOrDefault();
        if (file is null || file.Length == 0) return (null, null);

        using var buffer = new MemoryStream();
        await file.CopyToAsync(buffer);
        return (buffer.ToArray(), file.FileName);
    }

    private static string SafeSuffix(string fileName, params string[] allowed)
    {
        var suffix = (Path.GetExtension(fileName ?? "") ?? "").ToLowerInvariant();
        if (allowed.Length == 0) return suffix.Length > 0 ? suffix : ".jpg";
        return allowed.Contains(suffix) ? suffix : ".jpg";
    }

    public static void Map(WebApplication app)
    {
        MapVideos(app);
        MapActors(app);
        MapStudiosAndTags(app);
        MapSettings(app);
        MapLock(app);
        MapImport(app);
    }

    // --------------------------------------------------------------- videos

    private static void MapVideos(WebApplication app)
    {
        app.MapPut("/api/videos/{id:long}", async (long id, HttpRequest request) =>
        {
            var data = await Body.Read(request);
            using var connection = Db.Open();
            if (Db.QueryOne(connection, "SELECT id FROM videos WHERE id = @p0", id) is null)
                return Error("Video not found", 404);

            var sets = new List<string>();
            var values = new List<object>();

            foreach (var key in new[] { "title", "description", "release_date", "subsite" })
            {
                if (!Body.Has(data, key)) continue;
                sets.Add($"{key} = @p{values.Count}");
                values.Add(DbValue(Body.Str(data, key)));
            }
            foreach (var key in new[] { "rating", "favorite" })
            {
                if (!Body.Has(data, key)) continue;
                sets.Add($"{key} = @p{values.Count}");
                values.Add(Body.Int(data, key) ?? 0);
            }
            if (Body.Has(data, "studio"))
            {
                var name = (Body.Str(data, "studio") ?? "").Trim();
                sets.Add($"studio_id = @p{values.Count}");
                values.Add(name.Length > 0 ? Db.GetOrCreate(connection, "studios", name) : DBNull.Value);
            }

            if (sets.Count > 0)
            {
                values.Add(id);
                Db.Execute(connection,
                    $"UPDATE videos SET {string.Join(", ", sets)} WHERE id = @p{values.Count - 1}",
                    values.ToArray());
            }

            if (Body.Has(data, "actors"))
            {
                Db.Execute(connection, "DELETE FROM video_actors WHERE video_id = @p0", id);
                foreach (var name in Body.List(data, "actors"))
                {
                    var actorId = Db.GetOrCreate(connection, "actors", name);
                    if (actorId > 0)
                        Db.Execute(connection, "INSERT OR IGNORE INTO video_actors VALUES (@p0,@p1)", id, actorId);
                }
            }

            if (Body.Has(data, "tags"))
            {
                Db.Execute(connection, "DELETE FROM video_tags WHERE video_id = @p0", id);
                foreach (var name in Body.List(data, "tags"))
                {
                    var tagId = Db.GetOrCreate(connection, "tags", name);
                    if (tagId > 0)
                        Db.Execute(connection, "INSERT OR IGNORE INTO video_tags VALUES (@p0,@p1)", id, tagId);
                }
            }

            return Results.Ok(Api.VideoPayload(id));
        });

        // Removes the library entry only. The file on disk is never touched.
        app.MapDelete("/api/videos/{id:long}", (long id) =>
        {
            Db.Execute("DELETE FROM videos WHERE id = @p0", id);
            return Results.Ok(new { ok = true });
        });

        app.MapPost("/api/videos/{id:long}/thumbnail", async (long id, HttpRequest request) =>
        {
            var row = Db.QueryOne("SELECT path FROM videos WHERE id = @p0", id);
            if (row is null) return Error("Video not found", 404);

            var (data, fileName) = await ReadUpload(request);
            if (data is null) return Error("No image was attached.");

            var suffix = SafeSuffix(fileName, ".jpg", ".jpeg", ".png", ".webp");
            var name = $"{Media.KeyFor(row["path"] as string)}_custom{suffix}";
            await File.WriteAllBytesAsync(Path.Combine(AppPaths.ThumbDir, name), data);
            Db.Execute("UPDATE videos SET thumb = @p0, thumb_custom = 1 WHERE id = @p1", name, id);
            return Results.Ok(new { thumb = name });
        });

        app.MapPost("/api/videos/{id:long}/thumbnail/frame", async (long id, HttpRequest request) =>
        {
            var data = await Body.Read(request);
            var row = Db.QueryOne("SELECT path FROM videos WHERE id = @p0", id);
            if (row is null) return Error("Video not found", 404);

            var name = await Task.Run(() => Media.ThumbFromTimestamp(row["path"] as string, Body.Double(data, "time")));
            if (name is null) return Error("ffmpeg could not read that frame", 500);

            Db.Execute("UPDATE videos SET thumb = @p0, thumb_custom = 0 WHERE id = @p1", name, id);
            return Results.Ok(new { thumb = name });
        });
    }

    // --------------------------------------------------------------- actors

    private static void MapActors(WebApplication app)
    {
        app.MapPost("/api/actors", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            var name = (Body.Str(data, "name") ?? "").Trim();
            if (name.Length == 0) return Error("Enter a name");

            using var connection = Db.Open();
            return Results.Ok(new { id = Db.GetOrCreate(connection, "actors", name) });
        });

        app.MapPut("/api/actors/{id:long}", async (long id, HttpRequest request) =>
        {
            var data = await Body.Read(request);
            using var connection = Db.Open();

            // Renaming onto an existing performer folds the two together.
            var newName = (Body.Str(data, "name") ?? "").Trim();
            if (newName.Length > 0)
            {
                var clash = Db.QueryOne(connection,
                    "SELECT id FROM actors WHERE name = @p0 COLLATE NOCASE AND id != @p1", newName, id);
                if (clash != null)
                {
                    var target = Convert.ToInt64(clash["id"]);
                    var credits = Db.Scalar(connection,
                        "SELECT COUNT(*) FROM video_actors WHERE actor_id = @p0", id);
                    Db.Execute(connection,
                        "UPDATE OR IGNORE video_actors SET actor_id = @p0 WHERE actor_id = @p1", target, id);
                    Db.Execute(connection, "DELETE FROM video_actors WHERE actor_id = @p0", id);

                    var files = Db.QueryOne(connection, "SELECT image, banner FROM actors WHERE id = @p0", id);
                    DeleteFile(AppPaths.ActorDir, files?["image"]);
                    DeleteFile(AppPaths.ActorDir, files?["banner"]);
                    Db.Execute(connection, "DELETE FROM actors WHERE id = @p0", id);

                    return Results.Ok(new { id = target, name = newName, merged = credits, merged_into = target });
                }
            }

            if (Body.Has(data, "hidden"))
                Db.Execute(connection, "UPDATE actors SET hidden = @p0 WHERE id = @p1",
                           Body.Truthy(data, "hidden") ? 1 : 0, id);

            var sets = new List<string>();
            var values = new List<object>();
            foreach (var key in new[] { "name", "description", "birthdate", "country", "status" })
            {
                if (!Body.Has(data, key)) continue;
                sets.Add($"{key} = @p{values.Count}");
                values.Add(DbValue(Body.Str(data, key)));
            }
            if (Body.Has(data, "age"))
            {
                var age = Body.Int(data, "age");
                sets.Add($"age = @p{values.Count}");
                values.Add(age is > 0 ? age.Value : DBNull.Value);
            }
            if (sets.Count > 0)
            {
                values.Add(id);
                Db.Execute(connection,
                    $"UPDATE actors SET {string.Join(", ", sets)} WHERE id = @p{values.Count - 1}",
                    values.ToArray());
            }

            return Results.Ok(Db.QueryOne(connection, "SELECT * FROM actors WHERE id = @p0", id));
        });

        // The performer goes; their videos stay, minus the credit.
        app.MapDelete("/api/actors/{id:long}", (long id) =>
        {
            using var connection = Db.Open();
            var row = Db.QueryOne(connection, "SELECT image, banner FROM actors WHERE id = @p0", id);
            if (row is null) return Error("Star not found", 404);

            var credits = Db.Scalar(connection, "SELECT COUNT(*) FROM video_actors WHERE actor_id = @p0", id);
            DeleteFile(AppPaths.ActorDir, row["image"]);
            DeleteFile(AppPaths.ActorDir, row["banner"]);
            Db.Execute(connection, "DELETE FROM actors WHERE id = @p0", id);
            return Results.Ok(new { ok = true, credits_removed = credits });
        });

        app.MapPost("/api/actors/prune", () =>
        {
            using var connection = Db.Open();
            var rows = Db.Query(connection,
                @"SELECT id, image, banner FROM actors a WHERE NOT EXISTS
                  (SELECT 1 FROM video_actors va JOIN videos v ON v.id = va.video_id
                   WHERE va.actor_id = a.id AND v.missing = 0)");
            foreach (var row in rows)
            {
                DeleteFile(AppPaths.ActorDir, row["image"]);
                DeleteFile(AppPaths.ActorDir, row["banner"]);
                Db.Execute(connection, "DELETE FROM actors WHERE id = @p0", row["id"]);
            }
            return Results.Ok(new { removed = rows.Count });
        });

        app.MapPost("/api/actors/{id:long}/photo", async (long id, HttpRequest request) =>
            await SaveActorImage(id, request, "image", ""));

        app.MapPost("/api/actors/{id:long}/banner", async (long id, HttpRequest request) =>
            await SaveActorImage(id, request, "banner", "_banner"));
    }

    private static async Task<IResult> SaveActorImage(long id, HttpRequest request, string column, string tag)
    {
        var previous = Db.QueryOne($"SELECT {column} AS f FROM actors WHERE id = @p0", id);
        if (previous is null) return Error("Star not found", 404);

        var (data, fileName) = await ReadUpload(request);
        if (data is null) return Error("No image was attached.");

        var name = $"actor_{id}{tag}{SafeSuffix(fileName)}";
        if (previous["f"] is string old && old != name) DeleteFile(AppPaths.ActorDir, old);

        await File.WriteAllBytesAsync(Path.Combine(AppPaths.ActorDir, name), data);
        Db.Execute($"UPDATE actors SET {column} = @p0 WHERE id = @p1", name, id);

        return column == "image" ? Results.Ok(new { image = name }) : Results.Ok(new { banner = name });
    }

    // -------------------------------------------------------- studios, tags

    private static void MapStudiosAndTags(WebApplication app)
    {
        app.MapPut("/api/studios/{id:long}", async (long id, HttpRequest request) =>
        {
            var data = await Body.Read(request);
            using var connection = Db.Open();

            // Renaming onto an existing studio folds the two together rather
            // than failing on the unique name.
            var newName = (Body.Str(data, "name") ?? "").Trim();
            if (newName.Length > 0)
            {
                var clash = Db.QueryOne(connection,
                    "SELECT id FROM studios WHERE name = @p0 COLLATE NOCASE AND id != @p1", newName, id);
                if (clash != null)
                {
                    var target = Convert.ToInt64(clash["id"]);
                    var moved = Db.Scalar(connection, "SELECT COUNT(*) FROM videos WHERE studio_id = @p0", id);
                    Db.Execute(connection, "UPDATE videos SET studio_id = @p0 WHERE studio_id = @p1", target, id);
                    DeleteFile(AppPaths.StudioDir,
                        Db.QueryOne(connection, "SELECT image FROM studios WHERE id = @p0", id)?["image"]);
                    Db.Execute(connection, "DELETE FROM studios WHERE id = @p0", id);
                    return Results.Ok(new { id = target, name = newName, merged = moved, merged_into = target });
                }
            }

            var sets = new List<string>();
            var values = new List<object>();
            foreach (var key in new[] { "name", "description" })
            {
                if (!Body.Has(data, key)) continue;
                sets.Add($"{key} = @p{values.Count}");
                values.Add(Body.Str(data, key) ?? "");
            }
            foreach (var key in new[] { "logo_fit", "logo_bg" })
            {
                if (!Body.Has(data, key)) continue;
                sets.Add($"{key} = @p{values.Count}");
                values.Add(DbValue(Body.Str(data, key)));
            }
            foreach (var (key, low, high) in new[] { ("logo_zoom", 25, 400), ("logo_x", 0, 100), ("logo_y", 0, 100) })
            {
                if (!Body.Has(data, key)) continue;
                var value = Body.Int(data, key);
                sets.Add($"{key} = @p{values.Count}");
                values.Add(value.HasValue ? Math.Clamp(value.Value, low, high) : DBNull.Value);
            }
            if (sets.Count > 0)
            {
                values.Add(id);
                Db.Execute(connection,
                    $"UPDATE studios SET {string.Join(", ", sets)} WHERE id = @p{values.Count - 1}",
                    values.ToArray());
            }

            // Deliberately light: the page reloads, so there is no need to
            // rebuild a large studio's whole video list here.
            var studio = Db.QueryOne(connection, "SELECT * FROM studios WHERE id = @p0", id)
                         ?? new Dictionary<string, object>();
            studio["merged"] = 0;
            return Results.Ok(studio);
        });

        // The studio goes; its videos stay in the library, unlabelled.
        app.MapDelete("/api/studios/{id:long}", (long id) =>
        {
            using var connection = Db.Open();
            var row = Db.QueryOne(connection, "SELECT image FROM studios WHERE id = @p0", id);
            if (row is null) return Error("Studio not found", 404);

            var freed = Db.Scalar(connection, "SELECT COUNT(*) FROM videos WHERE studio_id = @p0", id);
            DeleteFile(AppPaths.StudioDir, row["image"]);
            Db.Execute(connection, "DELETE FROM studios WHERE id = @p0", id);
            return Results.Ok(new { ok = true, videos_unlabelled = freed });
        });

        app.MapPost("/api/studios/prune", () =>
        {
            using var connection = Db.Open();
            var rows = Db.Query(connection,
                @"SELECT id, image FROM studios s WHERE NOT EXISTS
                  (SELECT 1 FROM videos v WHERE v.studio_id = s.id AND v.missing = 0)");
            foreach (var row in rows)
            {
                DeleteFile(AppPaths.StudioDir, row["image"]);
                Db.Execute(connection, "DELETE FROM studios WHERE id = @p0", row["id"]);
            }
            return Results.Ok(new { removed = rows.Count });
        });

        app.MapPost("/api/studios/{id:long}/image", async (long id, HttpRequest request) =>
        {
            var previous = Db.QueryOne("SELECT image FROM studios WHERE id = @p0", id);
            if (previous is null) return Error("Studio not found", 404);

            var (data, fileName) = await ReadUpload(request);
            if (data is null) return Error("No image was attached.");

            var name = $"studio_{id}{SafeSuffix(fileName)}";
            if (previous["image"] is string old && old != name) DeleteFile(AppPaths.StudioDir, old);

            await File.WriteAllBytesAsync(Path.Combine(AppPaths.StudioDir, name), data);
            Db.Execute("UPDATE studios SET image = @p0 WHERE id = @p1", name, id);
            return Results.Ok(new { image = name });
        });

        app.MapPost("/api/tags/prune", () =>
        {
            using var connection = Db.Open();
            var rows = Db.Query(connection,
                @"SELECT id FROM tags t WHERE NOT EXISTS
                  (SELECT 1 FROM video_tags vt JOIN videos v ON v.id = vt.video_id
                   WHERE vt.tag_id = t.id AND v.missing = 0)");
            foreach (var row in rows) Db.Execute(connection, "DELETE FROM tags WHERE id = @p0", row["id"]);
            return Results.Ok(new { removed = rows.Count });
        });

        app.MapDelete("/api/tags/{id:long}", (long id) =>
        {
            Db.Execute("DELETE FROM tags WHERE id = @p0", id);
            return Results.Ok(new { ok = true });
        });
    }

    // ------------------------------------------------------------- settings

    private static void MapSettings(WebApplication app)
    {
        app.MapPut("/api/settings/preview-quality", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            var name = (Body.Str(data, "quality") ?? "").Trim();
            if (name is not ("sd" or "high" or "max")) return Error($"Unknown quality: {name}");

            Db.SetSetting("preview_quality", name);
            return Results.Ok(new { preview_quality = name });
        });
    }

    // ----------------------------------------------------------------- lock

    private static void MapLock(WebApplication app)
    {
        app.MapGet("/api/lock/status", () => Results.Ok(Lock.Status()));

        app.MapPost("/api/lock/unlock", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            return Results.Ok(Lock.Unlock(Body.Str(data, "pin") ?? ""));
        });

        // Changing a PIN requires the current one.
        app.MapPost("/api/lock/set", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            if (Lock.IsEnabled && !Lock.Verify(Body.Str(data, "current") ?? ""))
                return Error("That is not the current PIN.", 403);

            var problem = Lock.SetPin(Body.Str(data, "pin") ?? "");
            return problem is null ? Results.Ok(Lock.Status()) : Error(problem);
        });

        app.MapPost("/api/lock/disable", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            if (Lock.IsEnabled && !Lock.Verify(Body.Str(data, "current") ?? ""))
                return Error("That is not the current PIN.", 403);

            Lock.ClearPin();
            return Results.Ok(Lock.Status());
        });

        app.MapPost("/api/lock/now", () =>
        {
            Lock.LockNow();
            return Results.Ok(Lock.Status());
        });
    }

    // --------------------------------------------------------------- import

    private static void MapImport(WebApplication app)
    {
        app.MapGet("/api/scrape/providers", () => Results.Ok(new
        {
            items = Scrape.Providers(),
            tpdb_key_set = Scrape.TpdbKey().Length > 0,
            tpdb_base = Scrape.TpdbBase()
        }));

        app.MapPut("/api/scrape/tpdb-key", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            if (Body.Has(data, "key")) Db.SetSetting("tpdb_key", (Body.Str(data, "key") ?? "").Trim());
            var address = (Body.Str(data, "base") ?? "").Trim().TrimEnd('/');
            if (address.Length > 0) Db.SetSetting("tpdb_base", address);
            return Results.Ok(new { ready = Scrape.TpdbKey().Length > 0, @base = Scrape.TpdbBase() });
        });

        app.MapGet("/api/scrape/tpdb-test", async () => Results.Ok(await Scrape.TpdbTest()));

        app.MapPost("/api/scrape/search", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            try
            {
                var items = await Scrape.Search(
                    Body.Str(data, "provider") ?? "wikipedia",
                    Body.Str(data, "kind") ?? "performer",
                    Body.Str(data, "query") ?? "");
                return Results.Ok(new { items });
            }
            catch (ScrapeException ex)
            {
                return Error(ex.Message, ex.Status);
            }
            catch (Exception ex)
            {
                return Error($"Could not reach that source: {ex.Message}", 502);
            }
        });

        app.MapPost("/api/scrape/apply", async (HttpRequest request) =>
        {
            var data = await Body.Read(request);
            var kind = Body.Str(data, "kind");
            var id = (long)(Body.Int(data, "id") ?? 0);
            var fields = Body.Object(data, "fields");
            var applied = new List<string>();

            try
            {
                return kind switch
                {
                    "actor" => await ApplyActor(id, fields, applied),
                    "studio" => await ApplyStudio(id, fields, applied),
                    "video" => await ApplyVideo(id, fields, applied, Body.Has(data, "replace")
                        ? Body.Truthy(data, "replace")
                        : true),
                    _ => Error("Unknown target")
                };
            }
            catch (ScrapeException ex)
            {
                return Error($"Could not save that image: {ex.Message}");
            }
        });
    }

    private static async Task<IResult> ApplyActor(long id, JsonObject fields, List<string> applied)
    {
        if (Db.QueryOne("SELECT id FROM actors WHERE id = @p0", id) is null) return Error("Star not found", 404);

        foreach (var column in new[] { "name", "description", "birthdate" })
        {
            var value = Body.Str(fields, column);
            if (string.IsNullOrEmpty(value)) continue;
            Db.Execute($"UPDATE actors SET {column} = @p0 WHERE id = @p1", value, id);
            applied.Add(column);
        }

        var age = Body.Int(fields, "age");
        if (age is > 0)
        {
            Db.Execute("UPDATE actors SET age = @p0 WHERE id = @p1", age.Value, id);
            applied.Add("age");
        }

        foreach (var (key, column, stem) in new[] { ("image", "image", $"actor_{id}"), ("banner", "banner", $"actor_{id}_banner") })
        {
            var url = Body.Str(fields, key);
            if (string.IsNullOrEmpty(url)) continue;
            var name = await Scrape.DownloadImage(url, AppPaths.ActorDir, stem);
            if (name is null) continue;
            Db.Execute($"UPDATE actors SET {column} = @p0 WHERE id = @p1", name, id);
            applied.Add(column);
        }

        var source = Body.Str(fields, "source");
        if (!string.IsNullOrEmpty(source)) Db.Execute("UPDATE actors SET source = @p0 WHERE id = @p1", source, id);

        return Results.Ok(new { applied });
    }

    private static async Task<IResult> ApplyStudio(long id, JsonObject fields, List<string> applied)
    {
        if (Db.QueryOne("SELECT id FROM studios WHERE id = @p0", id) is null) return Error("Studio not found", 404);

        foreach (var column in new[] { "name", "description" })
        {
            var value = Body.Str(fields, column);
            if (string.IsNullOrEmpty(value)) continue;
            Db.Execute($"UPDATE studios SET {column} = @p0 WHERE id = @p1", value, id);
            applied.Add(column);
        }

        var image = Body.Str(fields, "image");
        if (!string.IsNullOrEmpty(image))
        {
            var name = await Scrape.DownloadImage(image, AppPaths.StudioDir, $"studio_{id}", trim: true);
            if (name != null)
            {
                Db.Execute("UPDATE studios SET image = @p0 WHERE id = @p1", name, id);
                applied.Add("image");
            }
        }

        var source = Body.Str(fields, "source");
        if (!string.IsNullOrEmpty(source)) Db.Execute("UPDATE studios SET source = @p0 WHERE id = @p1", source, id);

        return Results.Ok(new { applied });
    }

    private static async Task<IResult> ApplyVideo(long id, JsonObject fields, List<string> applied, bool replace)
    {
        var row = Db.QueryOne("SELECT path FROM videos WHERE id = @p0", id);
        if (row is null) return Error("Video not found", 404);

        using var connection = Db.Open();

        foreach (var column in new[] { "title", "description" })
        {
            var value = Body.Str(fields, column);
            if (string.IsNullOrEmpty(value)) continue;
            Db.Execute(connection, $"UPDATE videos SET {column} = @p0 WHERE id = @p1", value, id);
            applied.Add(column);
        }

        var date = Body.Str(fields, "release_date");
        if (!string.IsNullOrEmpty(date))
        {
            Db.Execute(connection, "UPDATE videos SET release_date = @p0 WHERE id = @p1", date, id);
            applied.Add("release_date");
        }

        var studio = Body.Str(fields, "studio");
        if (!string.IsNullOrEmpty(studio))
        {
            var studioId = Db.GetOrCreate(connection, "studios", studio);
            Db.Execute(connection, "UPDATE videos SET studio_id = @p0 WHERE id = @p1", studioId, id);
            applied.Add("studio");
        }

        var subsite = Body.Str(fields, "subsite");
        if (!string.IsNullOrEmpty(subsite))
        {
            Db.Execute(connection, "UPDATE videos SET subsite = @p0 WHERE id = @p1", subsite, id);
            applied.Add("sub-site");
        }

        // By default an import replaces the cast and tags it brings, so a wrong
        // match followed by the right one leaves only the right one.
        var actors = Body.List(fields, "actors");
        var tags = Body.List(fields, "tags");
        if (replace && actors.Count > 0)
            Db.Execute(connection, "DELETE FROM video_actors WHERE video_id = @p0", id);
        if (replace && tags.Count > 0)
            Db.Execute(connection, "DELETE FROM video_tags WHERE video_id = @p0", id);

        foreach (var name in actors)
        {
            var actorId = Db.GetOrCreate(connection, "actors", name);
            if (actorId <= 0) continue;
            Db.Execute(connection, "INSERT OR IGNORE INTO video_actors VALUES (@p0,@p1)", id, actorId);
            applied.Add($"cast: {name}");
        }
        foreach (var name in tags)
        {
            var tagId = Db.GetOrCreate(connection, "tags", name);
            if (tagId <= 0) continue;
            Db.Execute(connection, "INSERT OR IGNORE INTO video_tags VALUES (@p0,@p1)", id, tagId);
            applied.Add($"tag: {name}");
        }

        var image = Body.Str(fields, "image");
        if (!string.IsNullOrEmpty(image))
        {
            var name = await Scrape.DownloadImage(image, AppPaths.ThumbDir,
                $"{Media.KeyFor(row["path"] as string)}_custom", trim: true);
            if (name != null)
            {
                Db.Execute(connection, "UPDATE videos SET thumb = @p0, thumb_custom = 1 WHERE id = @p1", name, id);
                applied.Add("thumb");
            }
        }

        return Results.Ok(new { applied, video = Api.VideoPayload(id) });
    }
}
