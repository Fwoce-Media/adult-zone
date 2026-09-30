using System.Net;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AdultZone.Core.Data;
using AdultZone.Core.Library;

namespace AdultZone.Core.Providers;

/// <summary>One match a source offered.</summary>
public sealed class Found
{
    public string Source = "", Name = "", Description = "", Image = "", Banner = "", Date = "", Birthdate = "";
    public string Url = "", Attribution = "", Subtitle = "", Site = "", Studio = "", Gender = "";
    public string Id = "", Country = "", Status = "";
    public Dictionary<string, string> Facts = new();
    public List<string> Performers = new(), Tags = new(), Aliases = new();

    public Found Copy()
    {
        var f = (Found)MemberwiseClone();
        f.Facts = new Dictionary<string, string>(Facts);
        f.Performers = new List<string>(Performers);
        f.Tags = new List<string>(Tags);
        f.Aliases = new List<string>(Aliases);
        return f;
    }
}

public sealed record Provider(string Id, string Name, string[] Kinds, bool Ready);

/// <summary>
/// Details for performers, studios and scenes, from the sources chosen:
/// Wikipedia, any page address (its own metadata tags), or ThePornDB with your
/// own key. Nothing is written until a match is picked and its fields ticked.
/// </summary>
public static class Scrape
{
    const string WikiApi = "https://en.wikipedia.org/w/api.php";
    const string WikiRest = "https://en.wikipedia.org/api/rest_v1";
    const string WikidataApi = "https://www.wikidata.org/w/api.php";
    const string TpdbDefault = "https://api.theporndb.net";

    /// <summary>Crops flat bars from around a picture. Set by the app, which has the imaging.</summary>
    public static Func<byte[], byte[]>? Trim;

    static string Q(string s) => WebUtility.UrlEncode(s);

    public static string TpdbKey
    {
        get
        {
            var raw = Db.Setting("tpdb_key").Trim().Trim('"', '\'');
            if (raw.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) raw = raw[7..].Trim();
            return raw;
        }
    }

    public static string TpdbBase => Db.Setting("tpdb_base").Trim().TrimEnd('/') is { Length: > 0 } saved ? saved : TpdbDefault;

    static Dictionary<string, string> TpdbHeaders() => new()
    {
        ["Authorization"] = "Bearer " + TpdbKey,
        ["Accept"] = "application/json",
    };

    public static List<Provider> Providers() => new()
    {
        new("tpdb", "ThePornDB", new[] { "performer", "studio", "scene", "movie" }, TpdbKey.Length > 0),
        new("wikipedia", "Wikipedia", new[] { "performer", "studio" }, true),
        new("url", "Page address", new[] { "performer", "studio", "scene", "movie" }, true),
    };

    /// <summary>Whether the saved key works, and what the source said.</summary>
    public static (bool Ok, string Message) TestTpdb()
    {
        if (TpdbKey.Length == 0) return (false, "No key saved");
        try
        {
            Http.GetJson($"{TpdbBase}/performers?q=test&per_page=1", TpdbHeaders());
            return (true, "The key works");
        }
        catch (SourceError ex) { return (false, ex.Message); }
    }

    public static List<Found> Search(string provider, string kind, string query, int limit = 8)
    {
        query = query.Trim();
        if (query.Length == 0) return new();
        return provider switch
        {
            "wikipedia" => Wikipedia(query, kind),
            "tpdb" => Tpdb(query, kind, limit),
            "url" => FromPage(query),
            _ => throw new SourceError("Unknown source"),
        };
    }

    // ------------------------------------------------------------ wikipedia
    static List<Found> Wikipedia(string query, string kind, int limit = 5)
    {
        var hits = Http.GetJson($"{WikiApi}?action=query&list=search&srsearch={Q(query)}&format=json&srlimit={limit}");
        var output = new List<Found>();
        foreach (var hit in hits?["query"]?["search"] as JsonArray ?? new JsonArray())
        {
            var title = hit?["title"]?.ToString() ?? "";
            JsonNode? page;
            try { page = Http.GetJson($"{WikiRest}/page/summary/{Uri.EscapeDataString(title)}"); }
            catch (SourceError) { continue; }
            if (page?["type"]?.ToString() == "disambiguation") continue;
            var found = new Found
            {
                Source = "wikipedia",
                Name = page?["title"]?.ToString() ?? title,
                Description = (page?["extract"]?.ToString() ?? "").Trim(),
                Image = page?["originalimage"]?["source"]?.ToString() ?? page?["thumbnail"]?["source"]?.ToString() ?? "",
                Url = page?["content_urls"]?["desktop"]?["page"]?.ToString() ?? "",
                Attribution = "Wikipedia, CC BY-SA",
                Subtitle = page?["description"]?.ToString() ?? "",
            };
            if (kind == "performer") found.Birthdate = WikidataBirth(found.Name);
            output.Add(found);
        }
        return output;
    }

    static string WikidataBirth(string title)
    {
        try
        {
            var found = Http.GetJson($"{WikidataApi}?action=wbsearchentities&search={Q(title)}&language=en&format=json&limit=1");
            var id = (found?["search"] as JsonArray)?.FirstOrDefault()?["id"]?.ToString();
            if (id == null) return "";
            var data = Http.GetJson($"{WikidataApi}?action=wbgetclaims&entity={Q(id)}&property=P569&format=json");
            var stamp = data?["claims"]?["P569"]?[0]?["mainsnak"]?["datavalue"]?["value"]?["time"]?.ToString() ?? "";
            var m = Regex.Match(stamp, @"\+(\d{4})-(\d{2})-(\d{2})");
            if (m.Success && m.Groups[2].Value != "00" && m.Groups[3].Value != "00")
                return $"{m.Groups[1].Value}-{m.Groups[2].Value}-{m.Groups[3].Value}";
        }
        catch { }
        return "";
    }

    // -------------------------------------------------------------- any page
    static readonly Regex Meta = new(@"<meta\s+[^>]*?(?:property|name)\s*=\s*[""']([^""']+)[""'][^>]*?content\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);
    static readonly Regex MetaRev = new(@"<meta\s+[^>]*?content\s*=\s*[""']([^""']*)[""'][^>]*?(?:property|name)\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase);
    static readonly Regex LdJson = new(@"<script[^>]+application/ld\+json[^>]*>(.*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
    static readonly Regex TitleTag = new(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    static List<Found> FromPage(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new SourceError("The address must start with http:// or https://");
        var html = Http.GetText(url);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in Meta.Matches(html)) tags[m.Groups[1].Value] = WebUtility.HtmlDecode(m.Groups[2].Value);
        foreach (Match m in MetaRev.Matches(html)) tags.TryAdd(m.Groups[2].Value, WebUtility.HtmlDecode(m.Groups[1].Value));
        JsonObject? ld = null;
        foreach (Match m in LdJson.Matches(html))
        {
            try
            {
                var node = JsonNode.Parse(m.Groups[1].Value.Trim());
                ld = node as JsonObject ?? (node as JsonArray)?.OfType<JsonObject>().FirstOrDefault();
                if (ld != null) break;
            }
            catch { }
        }
        string Pick(params string[] keys) => keys.Select(k => tags.TryGetValue(k, out var v) ? v : "").FirstOrDefault(v => v.Length > 0) ?? "";
        string Ld(string key) => ld?[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

        var title = Pick("og:title", "twitter:title");
        if (title.Length == 0) title = Ld("name");
        if (title.Length == 0) title = TitleTag.Match(html) is { Success: true } t ? WebUtility.HtmlDecode(t.Groups[1].Value).Trim() : url;
        var image = Pick("og:image", "og:image:url", "twitter:image");
        if (image.Length == 0) image = Ld("image").Length > 0 ? Ld("image") : ld?["image"]?["url"]?.ToString() ?? "";
        var date = Pick("article:published_time", "og:video:release_date", "date");
        if (date.Length == 0) date = Ld("uploadDate").Length > 0 ? Ld("uploadDate") : Ld("datePublished");
        date = Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}") ? date[..10] : "";
        var description = Pick("og:description", "description", "twitter:description");
        if (description.Length == 0) description = Ld("description");
        return new List<Found>
        {
            new()
            {
                Source = "url",
                Name = WebUtility.HtmlDecode(title).Trim(),
                Description = description.Trim(),
                Image = image.Length > 0 ? new Uri(new Uri(url), image).ToString() : "",
                Date = date,
                Url = url,
                Attribution = new Uri(url).Host,
                Subtitle = Pick("og:site_name"),
            },
        };
    }

    // ------------------------------------------------------------------ tpdb
    /// <summary>A URL out of whichever shape the API used: a string, a set of sizes, or a list.</summary>
    static string PickImage(JsonNode? value)
    {
        switch (value)
        {
            case JsonValue v when v.TryGetValue<string>(out var s):
                return s;
            case JsonObject o:
                foreach (var key in new[] { "large", "full", "medium", "url", "small", "thumb" })
                    if (o[key] is JsonValue kv && kv.TryGetValue<string>(out var found) && found.Length > 0) return found;
                foreach (var (_, child) in o)
                    if (child is JsonValue cv && cv.TryGetValue<string>(out var any) && any.StartsWith("http", StringComparison.Ordinal)) return any;
                return "";
            case JsonArray a:
                foreach (var entry in a)
                    if (PickImage(entry) is { Length: > 0 } hit) return hit;
                return "";
            default:
                return "";
        }
    }

    /// <summary>Collapses the doubled slashes some of these URLs carry.</summary>
    static string Tidy(string url)
    {
        var i = url.IndexOf("://", StringComparison.Ordinal);
        if (i < 0) return url;
        var rest = url[(i + 3)..];
        while (rest.Contains("//")) rest = rest.Replace("//", "/");
        return url[..(i + 3)] + rest;
    }

    /// <summary>
    /// Best artwork for a scene. The database's own copies (background, posters)
    /// come first: the studio CDN in "image" often refuses requests from elsewhere.
    /// Background is landscape, which suits a 16:9 poster frame.
    /// </summary>
    static string SceneImage(JsonNode item)
    {
        foreach (var candidate in new[] { item["background"], item["posters"], item["poster"], item["poster_image"], item["back_image"], item["image"], item["thumbnail"] })
            if (PickImage(candidate) is { Length: > 0 } found) return Tidy(found);
        return "";
    }

    /// <summary>A movie's front cover, portrait: the posters first, the landscape still last.</summary>
    static string MovieCover(JsonNode item)
    {
        foreach (var candidate in new[] { item["posters"], item["poster"], item["poster_image"], item["image"], item["thumbnail"], item["background"] })
            if (PickImage(candidate) is { Length: > 0 } found) return Tidy(found);
        return "";
    }

    static string Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : "";

    static List<string> Names(JsonNode? list)
    {
        var output = new List<string>();
        foreach (var entry in list as JsonArray ?? new JsonArray())
        {
            var name = entry is JsonObject o ? Str(o["name"]) : Str(entry);
            if (name.Trim() is { Length: > 0 } n && !output.Contains(n)) output.Add(n);
        }
        return output;
    }

    /// <summary>
    /// A scene's origin as a studio and, when there is one, a sub-site: a scene
    /// from a site under a network has both; a site with nothing above it is the studio.
    /// </summary>
    static (string Studio, string Site) StudioAndSite(JsonNode item)
    {
        var siteNode = item["site"];
        var site = siteNode is JsonObject so ? (Str(so["name"]) is { Length: > 0 } sn ? sn : Str(so["short_name"])) : Str(siteNode);
        var network = "";
        if (siteNode is JsonObject s)
            foreach (var key in new[] { "network", "parent" })
            {
                var v = s[key];
                network = v is JsonObject vo ? Str(vo["name"]) : Str(v);
                if (network.Length > 0) break;
            }
        if (network.Length == 0)
            foreach (var key in new[] { "network", "parent" })
                if (item[key] is JsonObject io && Str(io["name"]) is { Length: > 0 } n) { network = n; break; }
        if (network.Length == 0) return (site, "");
        if (string.Equals(site.Trim(), network.Trim(), StringComparison.OrdinalIgnoreCase)) return (network, "");
        return (network, site);
    }

    /// <summary>A scene picked from a long list, read in full for its artwork, description, cast and tags.</summary>
    public static Found CompleteScene(Found f)
    {
        if (f.Source != "tpdb" || f.Id.Length == 0 || TpdbKey.Length == 0) return f;
        if (f.Image.Length > 0 && f.Description.Length > 0 && f.Tags.Count > 0) return f;
        try
        {
            var detail = Http.GetJson($"{TpdbBase}/scenes/{Uri.EscapeDataString(f.Id)}", TpdbHeaders());
            var full = detail?["data"] ?? detail;
            if (full == null) return f;
            if (f.Image.Length == 0) f.Image = SceneImage(full);
            if (f.Description.Length == 0) f.Description = (Str(full["description"]) is { Length: > 0 } d ? d : Str(full["bio"])).Trim();
            if (f.Tags.Count == 0) f.Tags = Names(full["tags"]);
            if (f.Performers.Count == 0) f.Performers = Names(full["performers"]);
        }
        catch (SourceError) { }
        return f;
    }

    static JsonNode Fill(JsonNode item)
    {
        // List results are often trimmed; the full record carries the artwork.
        if (SceneImage(item).Length > 0) return item;
        var id = Str(item["id"]) is { Length: > 0 } i ? i : item["id"]?.ToString() ?? Str(item["uuid"]);
        if (id.Length == 0) return item;
        try
        {
            var detail = Http.GetJson($"{TpdbBase}/scenes/{Uri.EscapeDataString(id)}", TpdbHeaders());
            var full = detail?["data"] as JsonObject ?? detail as JsonObject;
            if (full == null) return item;
            var merged = (JsonObject)item.DeepClone();
            foreach (var (k, v) in full)
                if (v != null && !(v is JsonValue jv && jv.TryGetValue<string>(out var sv) && sv.Length == 0)) merged[k] = v.DeepClone();
            return merged;
        }
        catch { return item; }
    }

    /// <summary>ThePornDB's gender words, as female, male or trans.</summary>
    static string GenderOf(string text)
    {
        var t = text.Trim().ToLowerInvariant();
        if (t.Contains("trans")) return "trans";
        if (t is "female" or "woman" or "f") return "female";
        if (t is "male" or "man" or "m") return "male";
        return "";
    }

    /// <summary>
    /// ThePornDB reading a whole "studio performer title" line the way it reads
    /// a file name, which narrows far better than a plain title search.
    /// </summary>
    public static List<Found> TpdbParse(string text, string kind = "scene") =>
        TpdbKey.Length == 0 || text.Trim().Length == 0 ? new() : Tpdb(text, kind, 8, "parse");

    static readonly Dictionary<string, string> SiteIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>ThePornDB's id for a studio or site by its name; blank when it has none by that exact name.</summary>
    static string SiteId(string name)
    {
        lock (SiteIds)
            if (SiteIds.TryGetValue(name, out var known)) return known;
        var id = "";
        var payload = Http.GetJson($"{TpdbBase}/sites?q={Q(name)}&per_page=10", TpdbHeaders());
        foreach (var site in payload?["data"] as JsonArray ?? new JsonArray())
        {
            if (site == null) continue;
            var names = new[] { Str(site["name"]), Str(site["short_name"]) };
            if (names.Any(n => Batch.Norm(n) == Batch.Norm(name)))
            {
                id = site["id"]?.ToString() ?? "";
                break;
            }
        }
        lock (SiteIds) SiteIds[name] = id;
        return id;
    }

    /// <summary>
    /// Scenes from one studio or site only. Blank when ThePornDB has no site by
    /// that name; results from anywhere else are dropped in case the filter is ignored.
    /// </summary>
    public static List<Found> TpdbSiteScenes(string site, string query, int limit = 25)
    {
        if (TpdbKey.Length == 0 || site.Trim().Length == 0) return new();
        var id = SiteId(site.Trim());
        if (id.Length == 0) return new();
        var q = query.Trim().Length > 0 ? $"&q={Q(query)}" : "";
        var hits = Tpdb("", "scene", limit, "", $"site_id={Uri.EscapeDataString(id)}{q}");
        var want = Batch.Norm(site);
        return hits.Where(h => Batch.Norm(h.Site) == want || Batch.Norm(h.Studio) == want).ToList();
    }

    static List<Found> Tpdb(string query, string kind, int limit = 8, string param = "q", string? raw = null)
    {
        if (TpdbKey.Length == 0) throw new SourceError("Add your ThePornDB API key in Settings first.");
        var path = kind switch { "performer" => "performers", "studio" => "sites", "movie" => "movies", _ => "scenes" };
        var filter = raw ?? $"{param}={Q(query)}";
        var payload = Http.GetJson($"{TpdbBase}/{path}?{filter}&per_page={limit}", TpdbHeaders());
        var items = (payload?["data"] as JsonArray ?? new JsonArray()).Where(n => n != null).Take(limit).Select(n => n!).ToList();
        // Filling each list entry costs a request apiece: only for the short lists a person reads.
        if (kind == "scene" && limit <= 8) items = items.Select(Fill).ToList();
        var output = new List<Found>();
        foreach (var item in items)
        {
            var extras = item["extras"];
            var (studio, site) = StudioAndSite(item);
            var date = Str(item["date"]) is { Length: > 0 } d ? d : Str(extras?["birthday"]);
            output.Add(new Found
            {
                Source = "tpdb",
                Name = Str(item["name"]) is { Length: > 0 } n ? n : Str(item["title"]),
                Description = (Str(item["bio"]) is { Length: > 0 } b ? b : Str(item["description"]) is { Length: > 0 } ds ? ds : Str(extras?["bio"])).Trim(),
                Image = kind == "movie" ? MovieCover(item) : SceneImage(item),
                Banner = PickImage(item["background"]),
                Date = date.Length >= 10 ? date[..10] : date,
                Birthdate = Str(extras?["birthday"]) is { Length: >= 10 } bd ? bd[..10] : "",
                Gender = GenderOf(Str(extras?["gender"])),
                Id = item["id"]?.ToString() ?? "",
                Url = Str(item["url"]),
                Attribution = "ThePornDB",
                Performers = Names(item["performers"]),
                Tags = Names(item["tags"]),
                Studio = studio,
                Site = site,
                Subtitle = site.Length > 0 ? site : studio,
            });
        }
        if (kind == "performer")
            for (var i = 0; i < output.Count; i++) ReadPerformer(output[i], items[i]);
        return output;
    }

    /// <summary>A value of any shape as text: yes/no for a flag, the number as written.</summary>
    static string Text(JsonNode? n)
    {
        if (n is not JsonValue v) return "";
        if (v.TryGetValue<bool>(out var b)) return b ? "Yes" : "No";
        if (v.TryGetValue<string>(out var s)) return s.Trim();
        return v.ToJsonString().Trim('"');
    }

    /// <summary>A performer's nationality, career and details from their record.</summary>
    static void ReadPerformer(Found f, JsonNode item)
    {
        foreach (var alias in Names(item["aliases"]))
            if (!f.Aliases.Contains(alias, StringComparer.OrdinalIgnoreCase)) f.Aliases.Add(alias);
        var extras = item["extras"];
        if (extras == null) return;
        foreach (var (column, _, source) in Catalog.PerformerFacts)
            if (source.Length > 0 && Text(extras[source]) is { Length: > 0 } value && value != "0") f.Facts[column] = value;
        var start = Text(extras["career_start_year"]);
        var end = Text(extras["career_end_year"]);
        if (start.Length > 0 || end.Length > 0) f.Facts["career"] = $"{start}\u2013{end}";
        var code = Text(extras["birthplace_code"]).ToLowerInvariant();
        if (code.Length == 2) f.Country = code;
        if (Text(extras["deathday"]).Length > 0) f.Status = "died";
        else if (end.Length > 0 && int.TryParse(end, out var y) && y < DateTime.Now.Year) f.Status = "retired";
        else if (start.Length > 0) f.Status = "active";
        if (f.Gender.Length == 0) f.Gender = GenderOf(Text(extras["gender"]));
        if (f.Birthdate.Length == 0 && Text(extras["birthday"]) is { Length: >= 10 } bd) f.Birthdate = bd[..10];
    }

    /// <summary>
    /// A performer picked from ThePornDB's results, read in full: the list
    /// often carries only part of the record.
    /// </summary>
    public static Found Complete(Found f)
    {
        if (f.Source != "tpdb" || f.Id.Length == 0 || TpdbKey.Length == 0) return f;
        try
        {
            var detail = Http.GetJson($"{TpdbBase}/performers/{Uri.EscapeDataString(f.Id)}", TpdbHeaders());
            var full = detail?["data"] ?? detail;
            if (full != null)
            {
                ReadPerformer(f, full);
                if (f.Description.Length == 0 && Str(full["bio"]) is { Length: > 0 } bio) f.Description = bio.Trim();
                if (f.Banner.Length == 0) f.Banner = Tidy(PickImage(full["background"]) is { Length: > 0 } bg ? bg : PickImage(full["backgrounds"]));
            }
        }
        catch (SourceError) { }
        if (f.Banner.Length == 0) f.Banner = WideShot(f);
        return f;
    }

    /// <summary>A landscape still of the performer, taken from one of their scenes.</summary>
    static string WideShot(Found f)
    {
        string FromList(JsonNode? payload, bool check)
        {
            foreach (var item in payload?["data"] as JsonArray ?? new JsonArray())
            {
                if (item == null) continue;
                if (check && !Names(item["performers"]).Any(n => string.Equals(n, f.Name, StringComparison.OrdinalIgnoreCase))) continue;
                if (PickImage(item["background"]) is { Length: > 0 } bg) return Tidy(bg);
            }
            return "";
        }
        try
        {
            var hit = FromList(Http.GetJson($"{TpdbBase}/performers/{Uri.EscapeDataString(f.Id)}/scenes?per_page=12", TpdbHeaders()), false);
            if (hit.Length > 0) return hit;
        }
        catch (Exception) { }
        try
        {
            if (f.Name.Length > 0)
                return FromList(Http.GetJson($"{TpdbBase}/scenes?q={Q(f.Name)}&per_page=24", TpdbHeaders()), true);
        }
        catch (Exception) { }
        return "";
    }

    // ----------------------------------------------------------------- apply
    /// <summary>A picture fetched and saved into the library's own folders.</summary>
    public static string SaveImage(string url, string folder, string stem, bool trim)
    {
        var (data, ext) = Http.GetImage(url);
        if (trim && Trim != null)
        {
            var trimmed = Trim(data);
            if (!ReferenceEquals(trimmed, data))
            {
                data = trimmed;
                ext = Http.ImageExt(data) ?? ext;
            }
        }
        return Catalog.SavePicture(folder, stem, data, ext);
    }

    /// <summary>The ticked fields of a match, written onto a performer. Returns what was applied.</summary>
    public static List<string> ApplyActor(long id, Found f, ISet<string> pick)
    {
        var applied = new List<string>();
        void Set(string column, string value, string label)
        {
            Db.Execute($"UPDATE actors SET {column} = ? WHERE id = ?", value, id);
            applied.Add(label);
        }
        if (pick.Contains("name") && f.Name.Length > 0 &&
            Db.QueryOne("SELECT id FROM actors WHERE name = ? COLLATE NOCASE AND id != ?", f.Name, id) == null)
            Set("name", f.Name, "name");
        if (pick.Contains("description") && f.Description.Length > 0) Set("description", f.Description, "biography");
        if (pick.Contains("birthdate") && f.Birthdate.Length > 0) Set("birthdate", f.Birthdate, "date of birth");
        if (pick.Contains("gender") && f.Gender.Length > 0) Set("gender", f.Gender, "gender");
        if (pick.Contains("country") && f.Country.Length > 0) Set("country", f.Country, "nationality");
        if (pick.Contains("status") && f.Status.Length > 0) Set("status", f.Status, "career status");
        if (pick.Contains("facts") && f.Facts.Count > 0)
        {
            foreach (var (column, value) in f.Facts) Db.Execute($"UPDATE actors SET {column} = ? WHERE id = ?", value, id);
            applied.Add("details");
        }
        if (pick.Contains("image") && f.Image.Length > 0)
        {
            Catalog.SetPictureColumn("actors", "image", Config.ActorDir, id, SaveImage(f.Image, Config.ActorDir, $"actor_{id}", false));
            applied.Add("photo");
        }
        if (pick.Contains("banner") && f.Banner.Length > 0)
        {
            Catalog.SetPictureColumn("actors", "banner", Config.ActorDir, id, SaveImage(f.Banner, Config.ActorDir, $"actor_{id}_banner", false));
            applied.Add("wide photo");
        }
        var source = f.Url.Length > 0 ? f.Url : f.Attribution;
        if (source.Length > 0) Db.Execute("UPDATE actors SET source = ? WHERE id = ?", source, id);
        if (applied.Count > 0) Db.Execute("UPDATE actors SET scraped = 1 WHERE id = ?", id);
        Catalog.Touch();
        return applied;
    }

    public static List<string> ApplyStudio(long id, Found f, ISet<string> pick)
    {
        var applied = new List<string>();
        if (pick.Contains("name") && f.Name.Length > 0 &&
            Db.QueryOne("SELECT id FROM studios WHERE name = ? COLLATE NOCASE AND id != ?", f.Name, id) == null)
        {
            Db.Execute("UPDATE studios SET name = ? WHERE id = ?", f.Name, id);
            applied.Add("name");
        }
        if (pick.Contains("description") && f.Description.Length > 0)
        {
            Db.Execute("UPDATE studios SET description = ? WHERE id = ?", f.Description, id);
            applied.Add("description");
        }
        if (pick.Contains("image") && f.Image.Length > 0)
        {
            Catalog.SetPictureColumn("studios", "image", Config.StudioDir, id, SaveImage(f.Image, Config.StudioDir, $"studio_{id}", true));
            applied.Add("logo");
        }
        var source = f.Url.Length > 0 ? f.Url : f.Attribution;
        if (source.Length > 0) Db.Execute("UPDATE studios SET source = ? WHERE id = ?", source, id);
        Catalog.Touch();
        return applied;
    }

    /// <summary>
    /// A scene's ticked fields onto a video. By default the cast and tags it
    /// brings replace what was there, so a second import never leaves a wrong
    /// match's data behind.
    /// </summary>
    public static List<string> ApplyVideo(long id, Found f, ISet<string> pick, bool replace)
    {
        var applied = new List<string>();
        var row = Db.QueryOne("SELECT path FROM videos WHERE id = ?", id);
        if (row == null) return applied;
        string? thumb = null;
        var movie = Db.QueryOne("SELECT kind FROM videos WHERE id = ?", id)?.Str("kind") == "movie";
        if (pick.Contains("image") && f.Image.Length > 0)
        {
            if (movie)
            {
                Catalog.SetPictureColumn("videos", "cover", Config.ThumbDir, id,
                    SaveImage(f.Image, Config.ThumbDir, Config.KeyFor(row.Str("path")) + "_cover", false));
                applied.Add("cover");
            }
            else thumb = SaveImage(f.Image, Config.ThumbDir, Config.KeyFor(row.Str("path")) + "_custom", true);
        }
        Db.InTransaction(() =>
        {
            if (pick.Contains("name") && f.Name.Length > 0) { Db.Execute("UPDATE videos SET title = ? WHERE id = ?", f.Name, id); applied.Add("title"); }
            if (pick.Contains("description") && f.Description.Length > 0) { Db.Execute("UPDATE videos SET description = ? WHERE id = ?", f.Description, id); applied.Add("description"); }
            if (pick.Contains("date") && f.Date.Length > 0) { Db.Execute("UPDATE videos SET release_date = ? WHERE id = ?", f.Date, id); applied.Add("release date"); }
            if (pick.Contains("studio") && f.Studio.Length > 0) { Db.Execute("UPDATE videos SET studio_id = ? WHERE id = ?", Db.GetOrCreate("studios", f.Studio), id); applied.Add("studio"); }
            if (pick.Contains("site") && f.Site.Length > 0) { Db.Execute("UPDATE videos SET subsite = ? WHERE id = ?", f.Site, id); applied.Add("sub-site"); }
            if (pick.Contains("performers") && f.Performers.Count > 0)
            {
                if (replace) Db.Execute("DELETE FROM video_actors WHERE video_id = ?", id);
                foreach (var name in f.Performers)
                    if (Db.GetOrCreate("actors", name) is long aid) Db.Execute("INSERT OR IGNORE INTO video_actors VALUES (?,?)", id, aid);
                applied.Add("cast");
            }
            if (pick.Contains("tags") && f.Tags.Count > 0)
            {
                if (replace) Db.Execute("DELETE FROM video_tags WHERE video_id = ?", id);
                foreach (var name in f.Tags)
                    if (Db.GetOrCreate("tags", name) is long tid) Db.Execute("INSERT OR IGNORE INTO video_tags VALUES (?,?)", id, tid);
                applied.Add("tags");
            }
            if (thumb != null)
            {
                Db.Execute("UPDATE videos SET thumb = ?, thumb_custom = 1 WHERE id = ?", thumb, id);
                applied.Add("thumbnail");
            }
            if (applied.Count > 0) Db.Execute("UPDATE videos SET scraped = 1 WHERE id = ?", id);
        });
        Catalog.Touch();
        return applied;
    }
}
