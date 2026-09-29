using AdultZone.Core.Data;
using AdultZone.Core.Media;

namespace AdultZone.Core.Library;

public sealed record VideoQuery(string Search = "", long ActorId = 0, long StudioId = 0, string Tag = "",
                                string Quality = "", bool Favorite = false, string Sort = "added", int Limit = 0, int Offset = 0,
                                string Kind = "");

public sealed record Stats(long Videos, long Actors, long Studios, long Tags, long Missing, long NoArtwork, long BelowQuality);

/// <summary>A rename that landed on a name already in the library folds the two together.</summary>
public sealed record Renamed(long Id, long Merged, bool Folded);

/// <summary>Every question the pages ask of the library.</summary>
public static class Catalog
{
    public static event Action? Changed;

    public static void Touch() => Changed?.Invoke();

    /// <summary>Every list of videos sorts the same way, wherever it appears.</summary>
    public static readonly (string Key, string Label)[] VideoSorts =
    {
        ("added", "Recently added"),
        ("date", "Release date — newest"),
        ("date_asc", "Release date — oldest"),
        ("views", "Most viewed"),
        ("title", "Title A–Z"),
        ("duration", "Longest"),
        ("shortest", "Shortest"),
        ("random", "Shuffle"),
    };

    static string Order(string sort) => sort switch
    {
        "views" => "v.views DESC, v.added_at DESC",
        "title" => "v.title COLLATE NOCASE ASC",
        "date" => "COALESCE(v.release_date,'0000') DESC",
        "date_asc" => "COALESCE(v.release_date,'9999') ASC",
        "duration" => "v.duration DESC",
        "shortest" => "v.duration ASC",
        "random" => "RANDOM()",
        _ => "v.added_at DESC, v.id DESC",
    };

    const string VideoSelect = "SELECT v.*, s.name AS studio_name FROM videos v LEFT JOIN studios s ON s.id = v.studio_id";

    // --------------------------------------------------------------- stats
    public static Stats Stats()
    {
        var width = Assets.Profile.Width;
        return new Stats(
            Db.Count("SELECT COUNT(*) c FROM videos WHERE missing = 0"),
            Db.Count("SELECT COUNT(*) c FROM actors"),
            Db.Count("SELECT COUNT(*) c FROM studios"),
            Db.Count("SELECT COUNT(*) c FROM tags"),
            Db.Count("SELECT COUNT(*) c FROM videos WHERE missing = 1"),
            Db.Count("SELECT COUNT(*) c FROM videos WHERE missing = 0 AND (thumb IS NULL OR preview IS NULL)"),
            Db.Count("SELECT COUNT(*) c FROM videos WHERE missing = 0 AND preview IS NOT NULL " +
                     "AND preview_width > 0 AND preview_width < MIN(?, width)", width));
    }

    // -------------------------------------------------------------- videos
    /// <summary>Cast and tags for many videos in a fixed number of queries.</summary>
    static List<Row> WithRelations(List<Row> rows)
    {
        if (rows.Count == 0) return rows;
        var ids = rows.Select(r => r.Long("id") ?? 0).ToList();
        var cast = new Dictionary<long, List<Row>>();
        var tags = new Dictionary<long, List<string>>();
        for (var start = 0; start < ids.Count; start += 400)
        {
            var chunk = ids.Skip(start).Take(400).Cast<object?>().ToArray();
            var marks = string.Join(",", chunk.Select(_ => "?"));
            foreach (var r in Db.Query($"SELECT va.video_id, a.id, a.name, a.image, a.gender FROM actors a JOIN video_actors va ON va.actor_id = a.id " +
                                       $"WHERE va.video_id IN ({marks}) ORDER BY a.name COLLATE NOCASE", chunk))
            {
                var vid = r.Long("video_id") ?? 0;
                if (!cast.TryGetValue(vid, out var list)) cast[vid] = list = new();
                list.Add(r);
            }
            foreach (var r in Db.Query($"SELECT vt.video_id, t.name FROM tags t JOIN video_tags vt ON vt.tag_id = t.id " +
                                       $"WHERE vt.video_id IN ({marks}) ORDER BY t.name COLLATE NOCASE", chunk))
            {
                var vid = r.Long("video_id") ?? 0;
                if (!tags.TryGetValue(vid, out var list)) tags[vid] = list = new();
                list.Add(r.Str("name"));
            }
        }
        var order = Db.Setting("cast_order");
        foreach (var row in rows)
        {
            var id = row.Long("id") ?? 0;
            row["actors"] = cast.TryGetValue(id, out var c) ? OrderCast(c, order) : new List<Row>();
            row["tags"] = tags.TryGetValue(id, out var t) ? t : new List<string>();
        }
        return rows;
    }

    /// <summary>A performer's details beyond the basics, in the order they show, with ThePornDB's names for them.</summary>
    public static readonly (string Column, string Label, string Source)[] PerformerFacts =
    {
        ("birthplace", "Birthplace", "birthplace"),
        ("ethnicity", "Ethnicity", "ethnicity"),
        ("hair", "Hair colour", "hair_colour"),
        ("eyes", "Eye colour", "eye_colour"),
        ("height", "Height", "height"),
        ("weight", "Weight", "weight"),
        ("measurements", "Measurements", "measurements"),
        ("cupsize", "Cup size", "cupsize"),
        ("tattoos", "Tattoos", "tattoos"),
        ("piercings", "Piercings", "piercings"),
        ("fake_boobs", "Fake boobs", "fake_boobs"),
        ("career", "Career", ""),
        ("astrology", "Astrology", "astrology"),
    };

    /// <summary>Saves the details; blank clears one.</summary>
    public static void SetPerformerFacts(long id, IDictionary<string, string> facts)
    {
        foreach (var (column, _, _) in PerformerFacts)
            if (facts.TryGetValue(column, out var value))
                Db.Execute($"UPDATE actors SET {column} = ? WHERE id = ?", Blank(value), id);
        Touch();
    }

    public static readonly (string Key, string Label)[] Genders = { ("female", "Female"), ("male", "Male"), ("trans", "Trans") };

    public static readonly (string Key, string Label)[] CastOrders =
    {
        ("alpha", "Alphabetically"), ("female", "Females first"), ("trans", "Trans first"), ("male", "Males first"),
    };

    /// <summary>The cast of a video in the order chosen in Settings; the rest alphabetically within each group.</summary>
    public static List<Row> OrderCast(List<Row> cast, string order)
    {
        if (order is not ("female" or "trans" or "male")) return cast;
        string[] sequence = order switch
        {
            "female" => new[] { "female", "trans", "male" },
            "trans" => new[] { "trans", "female", "male" },
            _ => new[] { "male", "female", "trans" },
        };
        int Rank(Row a)
        {
            var i = Array.IndexOf(sequence, a.Str("gender"));
            return i < 0 ? sequence.Length : i;
        }
        return cast.OrderBy(Rank).ThenBy(a => a.Str("name"), StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<Row> Cast(Row video) => video.Get("actors") as List<Row> ?? new();
    public static List<string> TagsOf(Row video) => video.Get("tags") as List<string> ?? new();
    public static string Quality(Row video) => Assets.QualityLabel(video.Long("height") ?? 0);

    public static (long Total, List<Row> Items) Videos(VideoQuery q)
    {
        var where = new List<string> { "v.missing = 0" };
        var args = new List<object?>();
        if (q.Search.Length > 0)
        {
            where.Add("""
                (v.title LIKE ? OR v.description LIKE ? OR v.path LIKE ?
                 OR EXISTS (SELECT 1 FROM video_actors va JOIN actors a ON a.id = va.actor_id WHERE va.video_id = v.id AND a.name LIKE ?)
                 OR EXISTS (SELECT 1 FROM video_tags vt JOIN tags t ON t.id = vt.tag_id WHERE vt.video_id = v.id AND t.name LIKE ?)
                 OR EXISTS (SELECT 1 FROM studios s2 WHERE s2.id = v.studio_id AND s2.name LIKE ?)
                 OR v.subsite LIKE ?)
                """);
            for (var i = 0; i < 7; i++) args.Add($"%{q.Search}%");
        }
        if (q.ActorId > 0)
        {
            where.Add("EXISTS (SELECT 1 FROM video_actors va WHERE va.video_id = v.id AND va.actor_id = ?)");
            args.Add(q.ActorId);
        }
        if (q.StudioId > 0)
        {
            where.Add("v.studio_id = ?");
            args.Add(q.StudioId);
        }
        if (q.Tag.Length > 0)
        {
            // The sub-site behaves as a tag, though it has a column of its own.
            where.Add("(EXISTS (SELECT 1 FROM video_tags vt JOIN tags t ON t.id = vt.tag_id WHERE vt.video_id = v.id AND t.name = ? COLLATE NOCASE)" +
                      " OR v.subsite = ? COLLATE NOCASE)");
            args.Add(q.Tag);
            args.Add(q.Tag);
        }
        switch (q.Quality)
        {
            case "HD": where.Add("v.height >= 700 AND v.height < 1400"); break;
            case "SD": where.Add("v.height > 0 AND v.height < 700"); break;
            case "2K": where.Add("v.height >= 1400 AND v.height < 2000"); break;
            case "4K": where.Add("v.height >= 2000"); break;
        }
        if (q.Favorite) where.Add("v.favorite = 1");
        if (q.Kind.Length > 0)
        {
            where.Add("COALESCE(v.kind, 'scene') = ?");
            args.Add(q.Kind);
        }

        var filter = string.Join(" AND ", where);
        var sql = $"{VideoSelect} WHERE {filter} ORDER BY {Order(q.Sort)}";
        List<Row> rows;
        if (q.Limit > 0)
        {
            var paged = new List<object?>(args) { q.Limit, q.Offset };
            rows = Db.Query(sql + " LIMIT ? OFFSET ?", paged.ToArray());
        }
        else rows = Db.Query(sql, args.ToArray());
        var total = Db.Count($"SELECT COUNT(*) c FROM videos v WHERE {filter}", args.ToArray());
        return (total, WithRelations(rows));
    }

    public static Row? Video(long id)
    {
        var row = Db.QueryOne($"{VideoSelect} WHERE v.id = ?", id);
        if (row == null) return null;
        WithRelations(new List<Row> { row });
        return row;
    }

    /// <summary>
    /// Related videos from one source only, so each list can say honestly
    /// what it is: shared cast, the same studio, or shared tags.
    /// </summary>
    public static List<Row> Related(long id, string source, int limit = 24)
    {
        var baseRow = Db.QueryOne("SELECT id, studio_id FROM videos WHERE id = ?", id);
        if (baseRow == null) return new();
        List<Row> rows = source switch
        {
            "studio" => baseRow.Long("studio_id") is long sid
                ? Db.Query($"{VideoSelect} WHERE v.studio_id = ? AND v.id != ? AND v.missing = 0 ORDER BY v.added_at DESC LIMIT ?", sid, id, limit)
                : new(),
            "tags" => Db.Query("""
                SELECT v.*, s.name AS studio_name, COUNT(*) AS shared FROM videos v
                JOIN video_tags vt ON vt.video_id = v.id LEFT JOIN studios s ON s.id = v.studio_id
                WHERE v.id != ? AND v.missing = 0 AND vt.tag_id IN (SELECT tag_id FROM video_tags WHERE video_id = ?)
                GROUP BY v.id ORDER BY shared DESC, v.added_at DESC LIMIT ?
                """, id, id, limit),
            _ => Db.Query("""
                SELECT v.*, s.name AS studio_name, COUNT(*) AS shared FROM videos v
                JOIN video_actors va ON va.video_id = v.id LEFT JOIN studios s ON s.id = v.studio_id
                WHERE v.id != ? AND v.missing = 0 AND va.actor_id IN (SELECT actor_id FROM video_actors WHERE video_id = ?)
                GROUP BY v.id ORDER BY shared DESC, v.views DESC LIMIT ?
                """, id, id, limit),
        };
        return WithRelations(rows);
    }

    /// <summary>What plays after a video: more with this cast — the same studio only when nobody else shares the cast.</summary>
    public static List<Row> UpNext(long id, int limit = 30)
    {
        var cast = Related(id, "cast", limit);
        return cast.Count > 0 ? cast : Related(id, "studio", limit);
    }

    public static void UpdateVideo(long id, string title, string description, string studio, string subsite, string? releaseDate,
                                   IEnumerable<string> actors, IEnumerable<string> tags)
    {
        Db.InTransaction(() =>
        {
            Db.Execute("UPDATE videos SET title = ?, description = ?, studio_id = ?, subsite = ?, release_date = ? WHERE id = ?",
                title, description, Db.GetOrCreate("studios", studio), subsite.Length > 0 ? subsite : null,
                string.IsNullOrEmpty(releaseDate) ? null : releaseDate, id);
            SetCast(id, actors, true);
            SetTags(id, tags, true);
        });
        Touch();
    }

    static void SetCast(long id, IEnumerable<string> names, bool replace)
    {
        if (replace) Db.Execute("DELETE FROM video_actors WHERE video_id = ?", id);
        foreach (var name in names)
            if (Db.GetOrCreate("actors", name) is long aid)
                Db.Execute("INSERT OR IGNORE INTO video_actors VALUES (?,?)", id, aid);
    }

    static void SetTags(long id, IEnumerable<string> names, bool replace)
    {
        if (replace) Db.Execute("DELETE FROM video_tags WHERE video_id = ?", id);
        foreach (var name in names)
            if (Db.GetOrCreate("tags", name) is long tid)
                Db.Execute("INSERT OR IGNORE INTO video_tags VALUES (?,?)", id, tid);
    }

    public static void SetFavorite(long id, bool on)
    {
        Db.Execute("UPDATE videos SET favorite = ? WHERE id = ?", on ? 1 : 0, id);
        Touch();
    }

    /// <summary>The entry leaves the library. The file on disk is not touched.</summary>
    public static void RemoveVideo(long id)
    {
        Db.Execute("DELETE FROM videos WHERE id = ?", id);
        Touch();
    }

    public static void RegisterView(long id) =>
        Db.Execute("UPDATE videos SET views = views + 1, last_played = datetime('now') WHERE id = ?", id);

    public static void SavePosition(long id, double seconds) =>
        Db.Execute("UPDATE videos SET position = ? WHERE id = ?", seconds, id);

    public static void SetThumb(long id, string name, bool custom)
    {
        Db.Execute("UPDATE videos SET thumb = ?, thumb_custom = ? WHERE id = ?", name, custom ? 1 : 0, id);
        Touch();
    }

    public static string ThumbPath(Row video) =>
        video.Str("thumb") is { Length: > 0 } t ? Path.Combine(Config.ThumbDir, t) : "";

    /// <summary>A movie's box cover, portrait; blank when it has none.</summary>
    public static string CoverPath(Row video) =>
        video.Str("cover") is { Length: > 0 } c ? Path.Combine(Config.ThumbDir, c) : "";

    public static bool IsMovie(Row video) => video.Str("kind") == "movie";

    public static readonly (string Key, string Label)[] Kinds = { ("scene", "Scene"), ("movie", "Movie") };

    public static void SetVideoKind(long id, string kind)
    {
        Db.Execute("UPDATE videos SET kind = ? WHERE id = ?", kind == "movie" ? "movie" : "scene", id);
        Touch();
    }

    public static void SetCover(long id, byte[] data, string ext)
    {
        var path = Db.QueryOne("SELECT path FROM videos WHERE id = ?", id)?.Str("path") ?? "";
        if (path.Length == 0) return;
        SetPictureColumn("videos", "cover", Config.ThumbDir, id, SavePicture(Config.ThumbDir, Config.KeyFor(path) + "_cover", data, ext));
    }

    /// <summary>Everything in a storage folder becomes scenes or movies.</summary>
    public static void SetLocationKind(long id, string kind)
    {
        kind = kind == "movie" ? "movie" : "scene";
        Db.Execute("UPDATE locations SET kind = ? WHERE id = ?", kind, id);
        Db.Execute("UPDATE videos SET kind = ? WHERE location_id = ?", kind, id);
        Touch();
    }

    // ------------------------------------------------------------ home rows
    /// <summary>The rows the home page can show; tags are added as "tag:Name".</summary>
    public static readonly (string Key, string Label)[] HomeRowKinds =
    {
        ("latest", "Recently added scenes"), ("movies", "Movies"), ("popular", "Most viewed"),
        ("favorites", "Favourites"), ("stars", "Top stars"), ("studios", "Studios"), ("random", "Pick something at random"),
    };

    public static List<string> HomeRowKeys() =>
        Db.Setting("home_rows").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    public static void SetHomeRowKeys(IEnumerable<string> keys) => Db.SetSetting("home_rows", string.Join(",", keys));

    public static string PreviewPath(Row video) =>
        video.Str("preview") is { Length: > 0 } p ? Path.Combine(Config.PreviewDir, p) : "";

    // -------------------------------------------------------------- home
    public sealed class HomeRows
    {
        public Stats Stats = null!;
        public List<Row> Latest = new(), Popular = new(), Random = new(), Stars = new(), Studios = new(), Favorites = new(), Movies = new();
        public List<(string Tag, List<Row> Items)> Tagged = new();
    }

    public static HomeRows Home()
    {
        var rows = HomeBase();
        foreach (var key in HomeRowKeys().Where(k => k.StartsWith("tag:", StringComparison.Ordinal)))
        {
            var tag = key[4..];
            var items = Videos(new VideoQuery(Tag: tag, Sort: "added", Limit: 24)).Items;
            if (items.Count > 0) rows.Tagged.Add((tag, items));
        }
        return rows;
    }

    static HomeRows HomeBase() => new()
    {
        Stats = Stats(),
        Latest = Videos(new VideoQuery(Sort: "added", Limit: 24, Kind: "scene")).Items,
        Movies = Videos(new VideoQuery(Sort: "added", Limit: 24, Kind: "movie")).Items,
        Popular = Videos(new VideoQuery(Sort: "views", Limit: 24)).Items.Where(v => (v.Long("views") ?? 0) > 0).ToList(),
        Favorites = Videos(new VideoQuery(Favorite: true, Sort: "added", Limit: 24)).Items,
        Random = Videos(new VideoQuery(Sort: "random", Limit: 24)).Items,
        Stars = Actors("", "count", false).Items.Where(a => (a.Long("video_count") ?? 0) > 0).Take(14).ToList(),
        Studios = Studios("count").Where(s => (s.Long("video_count") ?? 0) > 0).Take(12).ToList(),
    };

    // -------------------------------------------------------------- actors
    public static (List<Row> Items, long Hidden) Actors(string search, string sort, bool includeHidden)
    {
        var order = sort == "count" ? "video_count DESC, a.name COLLATE NOCASE" : "a.name COLLATE NOCASE";
        var clauses = new List<string>();
        var args = new List<object?>();
        if (search.Length > 0)
        {
            clauses.Add("a.name LIKE ?");
            args.Add($"%{search}%");
        }
        if (!includeHidden) clauses.Add("COALESCE(a.hidden, 0) = 0");
        var where = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        var rows = Db.Query($"""
            SELECT a.*, (SELECT COUNT(*) FROM video_actors va JOIN videos v ON v.id = va.video_id
                         WHERE va.actor_id = a.id AND v.missing = 0) AS video_count
            FROM actors a {where} ORDER BY {order}
            """, args.ToArray());
        return (rows, Db.Count("SELECT COUNT(*) c FROM actors WHERE COALESCE(hidden, 0) = 1"));
    }

    public static Row? Actor(long id, string sort)
    {
        var row = Db.QueryOne("SELECT * FROM actors WHERE id = ?", id);
        if (row == null) return null;
        row["videos"] = WithRelations(Db.Query($"""
            {VideoSelect} JOIN video_actors va ON va.video_id = v.id
            WHERE va.actor_id = ? AND v.missing = 0 ORDER BY {Order(sort)}
            """, id));
        row["video_count"] = (long)((List<Row>)row["videos"]!).Count;
        return row;
    }

    public static List<Row> VideosOf(Row owner) => owner.Get("videos") as List<Row> ?? new();

    public static string ActorPhoto(Row actor) =>
        actor.Str("image") is { Length: > 0 } i ? Path.Combine(Config.ActorDir, i) : "";

    public static string ActorBanner(Row actor) =>
        actor.Str("banner") is { Length: > 0 } b ? Path.Combine(Config.ActorDir, b) : "";

    public static long CreateActor(string name) => Db.GetOrCreate("actors", name) ?? 0;

    /// <summary>Saves a profile. A new name that belongs to another profile folds this one into it.</summary>
    public static Renamed UpdateActor(long id, string name, string? age, string? birthdate, string? country, string? status, string description, string? gender = null)
    {
        name = name.Trim();
        if (name.Length > 0 && Db.QueryOne("SELECT id FROM actors WHERE name = ? COLLATE NOCASE AND id != ?", name, id) is { } clash)
        {
            var into = clash.Long("id") ?? 0;
            var credits = Db.Count("SELECT COUNT(*) c FROM video_actors WHERE actor_id = ?", id);
            Db.InTransaction(() =>
            {
                Db.Execute("UPDATE OR IGNORE video_actors SET actor_id = ? WHERE actor_id = ?", into, id);
                DropActor(id);
            });
            Touch();
            return new Renamed(into, credits, true);
        }
        long? ageValue = long.TryParse(age?.Trim(), out var a) ? a : null;
        Db.Execute("UPDATE actors SET gender = ? WHERE id = ?", Blank(gender), id);
        Db.Execute("UPDATE actors SET name = ?, age = ?, birthdate = ?, country = ?, status = ?, description = ? WHERE id = ?",
            name.Length > 0 ? name : Db.QueryOne("SELECT name FROM actors WHERE id = ?", id)?.Str("name"),
            ageValue, Blank(birthdate), Blank(country), Blank(status), description, id);
        Touch();
        return new Renamed(id, 0, false);
    }

    static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    public static void HideActor(long id, bool hidden)
    {
        Db.Execute("UPDATE actors SET hidden = ? WHERE id = ?", hidden ? 1 : 0, id);
        Touch();
    }

    /// <summary>
    /// One profile folded into another: its credits move over, and anything the
    /// kept profile has blank is filled from the one that goes.
    /// </summary>
    public static void MergeActors(long from, long into)
    {
        if (from == into) return;
        var columns = new[] { "description", "birthdate", "age", "image", "banner", "country", "status", "gender", "source" }
            .Concat(PerformerFacts.Select(f => f.Column)).ToList();
        Db.InTransaction(() =>
        {
            Db.Execute("UPDATE OR IGNORE video_actors SET actor_id = ? WHERE actor_id = ?", into, from);
            foreach (var column in columns)
            {
                var moved = Db.Execute($"""
                    UPDATE actors SET {column} = (SELECT {column} FROM actors WHERE id = ?)
                    WHERE id = ? AND ({column} IS NULL OR {column} = '' OR {column} = 0)
                      AND (SELECT COALESCE({column}, '') FROM actors WHERE id = ?) NOT IN ('', 0)
                    """, from, into, from);
                // A picture handed over is no longer the old profile's to delete.
                if (moved > 0 && column is "image" or "banner") Db.Execute($"UPDATE actors SET {column} = NULL WHERE id = ?", from);
            }
            DropActor(from);
        });
        Touch();
    }

    static void DropActor(long id)
    {
        var row = Db.QueryOne("SELECT image, banner FROM actors WHERE id = ?", id);
        foreach (var column in new[] { "image", "banner" })
            if (row?.Str(column) is { Length: > 0 } file)
                try { File.Delete(Path.Combine(Config.ActorDir, file)); } catch { }
        Db.Execute("DELETE FROM video_actors WHERE actor_id = ?", id);
        Db.Execute("DELETE FROM actors WHERE id = ?", id);
    }

    /// <summary>The profile goes; the videos stay, without this credit.</summary>
    public static void DeleteActor(long id)
    {
        DropActor(id);
        Touch();
    }

    public static int PruneActors()
    {
        var rows = Db.Query("""
            SELECT id FROM actors a WHERE NOT EXISTS
            (SELECT 1 FROM video_actors va JOIN videos v ON v.id = va.video_id WHERE va.actor_id = a.id AND v.missing = 0)
            """);
        foreach (var r in rows) DropActor(r.Long("id") ?? 0);
        Touch();
        return rows.Count;
    }

    /// <summary>A picture copied into the library as a performer's photo or wide photo.</summary>
    public static void SetActorPicture(long id, string column, string file)
    {
        var stem = column == "banner" ? $"actor_{id}_banner" : $"actor_{id}";
        var name = SavePicture(Config.ActorDir, stem, File.ReadAllBytes(file), Path.GetExtension(file));
        SetPictureColumn("actors", column, Config.ActorDir, id, name);
    }

    public static void SetPictureColumn(string table, string column, string folder, long id, string name)
    {
        var previous = Db.QueryOne($"SELECT {column} AS f FROM {table} WHERE id = ?", id)?.Str("f") ?? "";
        if (previous.Length > 0 && previous != name)
            try { File.Delete(Path.Combine(folder, previous)); } catch { }
        Db.Execute($"UPDATE {table} SET {column} = ? WHERE id = ?", name, id);
        Touch();
    }

    public static string SavePicture(string folder, string stem, byte[] data, string ext)
    {
        ext = ext.ToLowerInvariant();
        if (ext.Length is 0 or > 5) ext = ".jpg";
        var name = stem + ext;
        Directory.CreateDirectory(folder);
        foreach (var old in Directory.EnumerateFiles(folder, stem + ".*"))
            try { File.Delete(old); } catch { }
        File.WriteAllBytes(Path.Combine(folder, name), data);
        return name;
    }

    // ------------------------------------------------------------- studios
    public static List<Row> Studios(string sort)
    {
        var order = sort == "count" ? "video_count DESC, s.name COLLATE NOCASE" : "s.name COLLATE NOCASE";
        return Db.Query($"""
            SELECT s.*, (SELECT COUNT(*) FROM videos v WHERE v.studio_id = s.id AND v.missing = 0) AS video_count
            FROM studios s ORDER BY {order}
            """);
    }

    public static Row? Studio(long id, string sort)
    {
        var row = Db.QueryOne("SELECT * FROM studios WHERE id = ?", id);
        if (row == null) return null;
        row["videos"] = WithRelations(Db.Query($"{VideoSelect} WHERE v.studio_id = ? AND v.missing = 0 ORDER BY {Order(sort)}", id));
        row["video_count"] = (long)((List<Row>)row["videos"]!).Count;
        return row;
    }

    public static string StudioLogo(Row studio) =>
        studio.Str("image") is { Length: > 0 } i ? Path.Combine(Config.StudioDir, i) : "";

    public static Renamed UpdateStudio(long id, string name, string description)
    {
        name = name.Trim();
        if (name.Length > 0 && Db.QueryOne("SELECT id FROM studios WHERE name = ? COLLATE NOCASE AND id != ?", name, id) is { } clash)
        {
            var into = clash.Long("id") ?? 0;
            var moved = Db.Count("SELECT COUNT(*) c FROM videos WHERE studio_id = ?", id);
            Db.InTransaction(() =>
            {
                Db.Execute("UPDATE videos SET studio_id = ? WHERE studio_id = ?", into, id);
                DropStudio(id);
            });
            Touch();
            return new Renamed(into, moved, true);
        }
        if (name.Length > 0) Db.Execute("UPDATE studios SET name = ?, description = ? WHERE id = ?", name, description, id);
        else Db.Execute("UPDATE studios SET description = ? WHERE id = ?", description, id);
        Touch();
        return new Renamed(id, 0, false);
    }

    /// <summary>How the logo sits in its box: fit, zoom, position and what is behind it.</summary>
    public static void SetLogoLayout(long id, string fit, int zoom, int x, int y, string background)
    {
        Db.Execute("UPDATE studios SET logo_fit = ?, logo_zoom = ?, logo_x = ?, logo_y = ?, logo_bg = ? WHERE id = ?",
            fit, Math.Clamp(zoom, 25, 1000), Math.Clamp(x, 0, 100), Math.Clamp(y, 0, 100), background, id);
        Touch();
    }

    public static void SetStudioLogo(long id, string file)
    {
        var name = SavePicture(Config.StudioDir, $"studio_{id}", File.ReadAllBytes(file), Path.GetExtension(file));
        SetPictureColumn("studios", "image", Config.StudioDir, id, name);
    }

    static void DropStudio(long id)
    {
        if (Db.QueryOne("SELECT image FROM studios WHERE id = ?", id)?.Str("image") is { Length: > 0 } file)
            try { File.Delete(Path.Combine(Config.StudioDir, file)); } catch { }
        Db.Execute("UPDATE videos SET studio_id = NULL WHERE studio_id = ?", id);
        Db.Execute("DELETE FROM studios WHERE id = ?", id);
    }

    /// <summary>The studio goes; its videos stay, without a studio.</summary>
    public static void DeleteStudio(long id)
    {
        DropStudio(id);
        Touch();
    }

    public static int PruneStudios()
    {
        var rows = Db.Query("SELECT id FROM studios s WHERE NOT EXISTS (SELECT 1 FROM videos v WHERE v.studio_id = s.id AND v.missing = 0)");
        foreach (var r in rows) DropStudio(r.Long("id") ?? 0);
        Touch();
        return rows.Count;
    }

    // ---------------------------------------------------------------- tags
    public static List<Row> Tags() => Db.Query("""
        SELECT t.*, (SELECT COUNT(*) FROM video_tags vt JOIN videos v ON v.id = vt.video_id
                     WHERE vt.tag_id = t.id AND v.missing = 0) AS video_count
        FROM tags t ORDER BY video_count DESC, t.name COLLATE NOCASE
        """);

    public static int PruneTags()
    {
        var n = Db.Execute("""
            DELETE FROM tags WHERE id IN (SELECT id FROM tags t WHERE NOT EXISTS
            (SELECT 1 FROM video_tags vt JOIN videos v ON v.id = vt.video_id WHERE vt.tag_id = t.id AND v.missing = 0))
            """);
        Touch();
        return n;
    }

    // ----------------------------------------------------------- locations
    public static List<Row> Locations()
    {
        var rows = Db.Query("SELECT * FROM locations ORDER BY id");
        foreach (var r in rows)
        {
            r["exists"] = Directory.Exists(r.Str("path")) ? 1L : 0L;
            r["video_count"] = Db.Count("SELECT COUNT(*) c FROM videos WHERE location_id = ? AND missing = 0", r.Long("id"));
        }
        return rows;
    }

    /// <summary>Adds a folder to search. Returns what went wrong, or null.</summary>
    public static string? AddLocation(string path, string kind = "scene")
    {
        kind = kind == "movie" ? "movie" : "scene";
        path = path.Trim().Trim('"');
        if (path.Length == 0) return "No folder";
        if (!Directory.Exists(path)) return "No folder at " + path;
        path = Path.GetFullPath(path);
        if (Db.QueryOne("SELECT id FROM locations WHERE path = ? COLLATE NOCASE", path) != null) return "That folder is already in the library";
        var label = Path.GetFileName(path.TrimEnd('\\', '/'));
        var id = Db.Insert("INSERT INTO locations (path, label, kind) VALUES (?, ?, ?)", path, label.Length > 0 ? label : path, kind);
        // Videos already found through a folder above this one now belong to it.
        var prefix = path.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        Db.Execute("UPDATE videos SET location_id = ?, kind = ? WHERE substr(path, 1, ?) = ? COLLATE NOCASE", id, kind, prefix.Length, prefix);
        Touch();
        return null;
    }

    public static void EnableLocation(long id, bool on) => Db.Execute("UPDATE locations SET enabled = ? WHERE id = ?", on ? 1 : 0, id);

    public static void RemoveLocation(long id, bool purge)
    {
        if (purge) Db.Execute("DELETE FROM videos WHERE location_id = ?", id);
        Db.Execute("DELETE FROM locations WHERE id = ?", id);
        Touch();
    }
}
