using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AdultZone;

/// <summary>A problem the user should read, shown as the dialog's message.</summary>
public class ScrapeException : Exception
{
    public int Status { get; }
    public ScrapeException(string message, int status = 400) : base(message) => Status = status;
}

/// <summary>
/// Fetches performer, studio and scene metadata. Mirrors the Python build,
/// including everything learned the hard way: prefer database-hosted artwork
/// over studio CDNs, identify images by their bytes rather than their headers,
/// crop the padding thumbnail services add, and split a scene's origin into
/// studio and sub-site properly.
/// </summary>
public static class Scrape
{
    private const string UserAgent = "AdultZone/2.0 (personal media library; +local)";
    private const int MaxImageBytes = 12 * 1024 * 1024;

    private const string WikiApi = "https://en.wikipedia.org/w/api.php";
    private const string WikiRest = "https://en.wikipedia.org/api/rest_v1";
    private const string WikidataApi = "https://www.wikidata.org/w/api.php";
    private const string TpdbDefault = "https://api.theporndb.net";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return client;
    }

    // ------------------------------------------------------------ plumbing

    private static async Task<string> GetText(string url, string bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (bearer != null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        }

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request);
        }
        catch (TaskCanceledException)
        {
            throw new ScrapeException("That source took too long to answer.", 502);
        }
        catch (HttpRequestException ex)
        {
            throw new ScrapeException($"Could not reach that source: {ex.Message}", 502);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
                throw new ScrapeException(Explain(response, body), 502);
            return body;
        }
    }

    private static async Task<JsonNode> GetJson(string url, string bearer = null)
    {
        var text = await GetText(url, bearer);
        try
        {
            return JsonNode.Parse(text);
        }
        catch
        {
            throw new ScrapeException("That source sent back something that was not JSON.", 502);
        }
    }

    /// <summary>Turn an HTTP failure into something worth reading.</summary>
    private static string Explain(HttpResponseMessage response, string body)
    {
        // A network filter or proxy can refuse with its own reason header.
        // Say so rather than blaming the key.
        if (response.Headers.TryGetValues("x-deny-reason", out var reasons))
        {
            var reason = reasons.FirstOrDefault();
            if (!string.IsNullOrEmpty(reason))
                return $"A network filter blocked the request ({reason}). " +
                       "This is your network or proxy, not ThePornDB.";
        }

        var detail = "";
        try
        {
            var parsed = JsonNode.Parse(body ?? "") as JsonObject;
            detail = Str(parsed?["message"]) ?? Str(parsed?["error"]) ?? "";
        }
        catch { }

        var said = detail.Length > 0 ? $" It said: {detail}" : "";
        return (int)response.StatusCode switch
        {
            401 => "The source rejected the API key (401). Check that it was copied in full " +
                   "from your account's API tokens page, that the token is still active, and " +
                   "that your account is verified." + said,
            403 => "The key was recognised but is not allowed to do that (403). It may lack " +
                   "permission or your subscription may have lapsed." + said,
            429 => "That source is rate limiting you (429). Wait a minute and try again.",
            404 => "That address returned nothing (404).",
            var code => $"The source returned HTTP {code}.{said}"
        };
    }

    // ---------------------------------------------------------------- json

    private static string Str(JsonNode node)
    {
        if (node is null) return null;
        if (node is JsonValue value)
        {
            if (value.TryGetValue<string>(out var text)) return text;
            return value.ToJsonString();
        }
        return null;
    }

    private static string First(params string[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrEmpty(value)) ?? "";

    private static string Head(string text, int length) =>
        string.IsNullOrEmpty(text) ? "" : text.Length <= length ? text : text[..length];

    // -------------------------------------------------------------- tpdb key

    /// <summary>The saved key, tolerating the ways it usually gets pasted.</summary>
    public static string TpdbKey()
    {
        var raw = (Db.GetSetting("tpdb_key") ?? "").Trim().Trim('"', '\'').Trim();
        if (raw.StartsWith("bearer ", StringComparison.OrdinalIgnoreCase)) raw = raw[7..].Trim();
        return raw;
    }

    /// <summary>Saved address, or the default. Blank or whitespace falls back.</summary>
    public static string TpdbBase()
    {
        var saved = (Db.GetSetting("tpdb_base") ?? "").Trim().TrimEnd('/');
        return saved.Length > 0 ? saved : TpdbDefault;
    }

    public static async Task<object> TpdbTest()
    {
        var key = TpdbKey();
        if (key.Length == 0) return new { ok = false, message = "No key saved yet." };

        try
        {
            await GetText($"{TpdbBase()}/performers?q=test&per_page=1", key);
        }
        catch (ScrapeException ex)
        {
            return new { ok = false, message = ex.Message };
        }
        return new { ok = true, message = "The key works.", key_length = key.Length };
    }

    public static object Providers()
    {
        var ready = TpdbKey().Length > 0;
        return new object[]
        {
            new
            {
                id = "wikipedia", name = "Wikipedia", kinds = new[] { "performer", "studio" },
                ready = true, note = "Biographies and portraits, text under CC BY-SA."
            },
            new
            {
                id = "url", name = "Page address", kinds = new[] { "performer", "studio", "scene" },
                ready = true, note = "Paste any page address and read its own metadata tags."
            },
            new
            {
                id = "tpdb", name = "ThePornDB", kinds = new[] { "performer", "studio", "scene" },
                ready,
                readyNote = "Scene results include the sub-site, which you can keep as a tag.",
                note = "Needs your own API key, added below."
            }
        };
    }

    // ------------------------------------------------------------ wikipedia

    private static async Task<string> WikidataBirthdate(string title)
    {
        try
        {
            var found = await GetJson(
                $"{WikidataApi}?action=wbsearchentities&language=en&format=json&limit=1" +
                $"&search={Uri.EscapeDataString(title)}");
            var entity = Str(found?["search"]?[0]?["id"]);
            if (string.IsNullOrEmpty(entity)) return "";

            var claims = await GetJson(
                $"{WikidataApi}?action=wbgetclaims&property=P569&format=json" +
                $"&entity={Uri.EscapeDataString(entity)}");
            var stamp = Str(claims?["claims"]?["P569"]?[0]?["mainsnak"]?["datavalue"]?["value"]?["time"]);
            var match = Regex.Match(stamp ?? "", @"\+(\d{4})-(\d{2})-(\d{2})");
            if (match.Success && match.Groups[2].Value != "00" && match.Groups[3].Value != "00")
                return $"{match.Groups[1].Value}-{match.Groups[2].Value}-{match.Groups[3].Value}";
        }
        catch { }
        return "";
    }

    private static async Task<List<Dictionary<string, object>>> SearchWikipedia(string query, string kind)
    {
        var hits = await GetJson(
            $"{WikiApi}?action=query&list=search&format=json&srlimit=5" +
            $"&srsearch={Uri.EscapeDataString(query)}");

        var results = new List<Dictionary<string, object>>();
        if (hits?["query"]?["search"] is not JsonArray list) return results;

        foreach (var hit in list.Take(5))
        {
            var title = Str(hit?["title"]) ?? "";
            JsonNode page;
            try
            {
                page = await GetJson($"{WikiRest}/page/summary/{Uri.EscapeDataString(title)}");
            }
            catch
            {
                continue;
            }
            if (Str(page?["type"]) == "disambiguation") continue;

            var name = First(Str(page?["title"]), title);
            var result = new Dictionary<string, object>
            {
                ["source"] = "wikipedia",
                ["name"] = name,
                ["description"] = (Str(page?["extract"]) ?? "").Trim(),
                ["image"] = First(Str(page?["originalimage"]?["source"]), Str(page?["thumbnail"]?["source"])),
                ["banner"] = "",
                ["date"] = "",
                ["url"] = Str(page?["content_urls"]?["desktop"]?["page"]) ?? "",
                ["attribution"] = "Wikipedia, CC BY-SA",
                ["subtitle"] = Str(page?["description"]) ?? ""
            };
            if (kind == "performer") result["birthdate"] = await WikidataBirthdate(name);
            results.Add(result);
        }
        return results;
    }

    // ------------------------------------------------------------- any page

    private static readonly Regex Meta = new(
        @"<meta\s+[^>]*?(?:property|name)\s*=\s*[""']([^""']+)[""'][^>]*?content\s*=\s*[""']([^""']*)[""']",
        RegexOptions.IgnoreCase);
    private static readonly Regex MetaReversed = new(
        @"<meta\s+[^>]*?content\s*=\s*[""']([^""']*)[""'][^>]*?(?:property|name)\s*=\s*[""']([^""']+)[""']",
        RegexOptions.IgnoreCase);
    private static readonly Regex JsonLd = new(
        @"<script[^>]+application/ld\+json[^>]*>(.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex TitleTag = new(
        @"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static JsonObject FirstJsonLd(string html)
    {
        foreach (Match match in JsonLd.Matches(html))
        {
            try
            {
                var node = JsonNode.Parse(match.Groups[1].Value.Trim());
                if (node is JsonObject single) return single;
                if (node is JsonArray many)
                    foreach (var entry in many)
                        if (entry is JsonObject found) return found;
            }
            catch { }
        }
        return new JsonObject();
    }

    private static async Task<List<Dictionary<string, object>>> FetchUrl(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new ScrapeException("The address must start with http:// or https://");

        var html = await GetText(url);
        var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in Meta.Matches(html))
            tags[match.Groups[1].Value] = WebUtility.HtmlDecode(match.Groups[2].Value);
        foreach (Match match in MetaReversed.Matches(html))
            tags.TryAdd(match.Groups[2].Value, WebUtility.HtmlDecode(match.Groups[1].Value));
        var ld = FirstJsonLd(html);

        string Pick(params string[] keys)
        {
            foreach (var key in keys)
                if (tags.TryGetValue(key, out var value) && !string.IsNullOrEmpty(value)) return value;
            return "";
        }

        var title = First(Pick("og:title", "twitter:title"), Str(ld["name"]));
        if (title.Length == 0)
        {
            var match = TitleTag.Match(html);
            title = match.Success ? WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : url;
        }

        var image = Pick("og:image", "og:image:url", "twitter:image");
        // JSON-LD may give the image as a string, an object or a list; indexing
        // the wrong one by key throws, so read it through PickImage instead.
        if (image.Length == 0) image = PickImage(ld["image"]);

        var date = First(Pick("article:published_time", "og:video:release_date", "date"),
                         Str(ld["uploadDate"]), Str(ld["datePublished"]));
        date = Regex.IsMatch(date, @"^\d{4}-\d{2}-\d{2}") ? date[..10] : "";

        string absoluteImage = "";
        if (image.Length > 0)
        {
            try { absoluteImage = new Uri(new Uri(url), image).ToString(); } catch { absoluteImage = image; }
        }

        return new List<Dictionary<string, object>>
        {
            new()
            {
                ["source"] = "url",
                ["name"] = WebUtility.HtmlDecode(title).Trim(),
                ["description"] = First(Pick("og:description", "description", "twitter:description"),
                                        Str(ld["description"])).Trim(),
                ["image"] = absoluteImage,
                ["banner"] = "",
                ["date"] = date,
                ["url"] = url,
                ["attribution"] = new Uri(url).Host,
                ["subtitle"] = Pick("og:site_name")
            }
        };
    }

    // ---------------------------------------------------------- tpdb shapes

    /// <summary>
    /// A URL out of whichever shape the API used: a plain string, a dictionary
    /// of sizes, or a list of those. The shape differs between performers,
    /// sites and scenes.
    /// </summary>
    private static string PickImage(JsonNode node)
    {
        switch (node)
        {
            case JsonValue value:
                return value.TryGetValue<string>(out var text) ? text : "";
            case JsonObject map:
                foreach (var key in new[] { "large", "full", "medium", "url", "small", "thumb" })
                {
                    var found = Str(map[key]);
                    if (!string.IsNullOrEmpty(found)) return found;
                }
                foreach (var pair in map)
                {
                    var found = Str(pair.Value);
                    if (found != null && found.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return found;
                }
                return "";
            case JsonArray list:
                foreach (var entry in list)
                {
                    var found = PickImage(entry);
                    if (found.Length > 0) return found;
                }
                return "";
            default:
                return "";
        }
    }

    /// <summary>Collapse the doubled slashes some of these URLs carry.</summary>
    private static string TidyUrl(string url)
    {
        var index = url.IndexOf("://", StringComparison.Ordinal);
        if (index < 0) return url;
        var scheme = url[..index];
        var rest = url[(index + 3)..];
        while (rest.Contains("//")) rest = rest.Replace("//", "/");
        return $"{scheme}://{rest}";
    }

    /// <summary>
    /// Best artwork for a scene, preferring copies the database hosts itself.
    /// The `image` field usually points at the studio's CDN, which commonly
    /// refuses requests that did not come from its own site. `background` is
    /// landscape and hosted by the database, so it comes first.
    /// </summary>
    private static string SceneImage(JsonObject item)
    {
        foreach (var candidate in new[]
                 {
                     item["background"], item["posters"], item["poster"], item["poster_image"],
                     item["back_image"], item["image"], item["thumbnail"]
                 })
        {
            var found = PickImage(candidate);
            if (found.Length > 0) return TidyUrl(found);
        }
        return "";
    }

    private static string SiteName(JsonObject item) => item["site"] switch
    {
        JsonObject site => First(Str(site["name"]), Str(site["short_name"])),
        JsonValue value => Str(value) ?? "",
        _ => ""
    };

    private static string NetworkName(JsonObject item)
    {
        if (item["site"] is JsonObject site)
        {
            foreach (var key in new[] { "network", "parent" })
            {
                if (site[key] is JsonObject nested && !string.IsNullOrEmpty(Str(nested["name"])))
                    return Str(nested["name"]);
                if (site[key] is JsonValue plain && !string.IsNullOrEmpty(Str(plain)))
                    return Str(plain);
            }
        }
        foreach (var key in new[] { "network", "parent" })
        {
            if (item[key] is JsonObject nested && !string.IsNullOrEmpty(Str(nested["name"])))
                return Str(nested["name"]);
        }
        return "";
    }

    /// <summary>
    /// Split a scene's origin into a studio and, if there is one, a sub-site.
    /// Blacks On Blondes sits under Dogfart, so it is a genuine sub-site; a
    /// scene straight from Brazzers has none, and tagging it would only
    /// repeat the studio's own name.
    /// </summary>
    private static (string Studio, string Site) StudioAndSite(JsonObject item)
    {
        var site = SiteName(item);
        var network = NetworkName(item);

        if (network.Length == 0) return (site, "");
        if (string.Equals(site.Trim(), network.Trim(), StringComparison.OrdinalIgnoreCase))
            return (network, "");
        return (network, site);
    }

    private static List<string> NameList(JsonNode node)
    {
        var names = new List<string>();
        if (node is not JsonArray list) return names;
        foreach (var entry in list)
        {
            var name = entry is JsonObject map ? Str(map["name"]) : Str(entry);
            name = name?.Trim();
            if (!string.IsNullOrEmpty(name) && !names.Contains(name)) names.Add(name);
        }
        return names;
    }

    /// <summary>List entries are often trimmed; fetch the detail when there is no artwork.</summary>
    private static async Task<JsonObject> FillScene(JsonObject item, string key)
    {
        if (SceneImage(item).Length > 0) return item;

        var id = First(Str(item["id"]), Str(item["uuid"]), Str(item["_id"]));
        if (id.Length == 0) return item;

        try
        {
            var detail = await GetJson($"{TpdbBase()}/scenes/{Uri.EscapeDataString(id)}", key);
            var full = detail?["data"] as JsonObject ?? detail as JsonObject;
            if (full is null) return item;

            var merged = JsonNode.Parse(item.ToJsonString()) as JsonObject ?? new JsonObject();
            foreach (var pair in full)
            {
                if (pair.Value is null) continue;
                if (pair.Value is JsonValue value && Str(value) is "" or "null") continue;
                merged[pair.Key] = JsonNode.Parse(pair.Value.ToJsonString());
            }
            return merged;
        }
        catch
        {
            return item;
        }
    }

    private static async Task<List<Dictionary<string, object>>> SearchTpdb(string query, string kind)
    {
        var key = TpdbKey();
        if (key.Length == 0) throw new ScrapeException("Add your ThePornDB API key in Settings first.");

        var path = kind switch { "performer" => "performers", "studio" => "sites", _ => "scenes" };
        var payload = await GetJson(
            $"{TpdbBase()}/{path}?per_page=8&q={Uri.EscapeDataString(query)}", key);

        var items = new List<JsonObject>();
        if (payload?["data"] is JsonArray data)
            foreach (var entry in data.Take(8))
                if (entry is JsonObject map) items.Add(map);

        if (kind == "scene")
        {
            for (var i = 0; i < items.Count; i++) items[i] = await FillScene(items[i], key);
        }

        var results = new List<Dictionary<string, object>>();
        foreach (var item in items)
        {
            var extras = item["extras"] as JsonObject ?? new JsonObject();
            var (studio, site) = StudioAndSite(item);
            results.Add(new Dictionary<string, object>
            {
                ["source"] = "tpdb",
                ["name"] = First(Str(item["name"]), Str(item["title"])),
                ["description"] = First(Str(item["bio"]), Str(item["description"]), Str(extras["bio"])).Trim(),
                ["image"] = SceneImage(item),
                ["banner"] = PickImage(item["background"]),
                ["date"] = Head(First(Str(item["date"]), Str(extras["birthday"])), 10),
                ["birthdate"] = Head(Str(extras["birthday"]) ?? "", 10),
                ["url"] = Str(item["url"]) ?? "",
                ["attribution"] = "ThePornDB",
                ["performers"] = NameList(item["performers"]),
                ["tags"] = NameList(item["tags"]),
                ["site"] = site,
                ["studio"] = studio,
                ["subtitle"] = site.Length > 0 ? site : studio
            });
        }
        return results;
    }

    public static async Task<List<Dictionary<string, object>>> Search(string provider, string kind, string query)
    {
        query = (query ?? "").Trim();
        if (query.Length == 0) return new List<Dictionary<string, object>>();

        return provider switch
        {
            "wikipedia" => await SearchWikipedia(query, kind),
            "tpdb" => await SearchTpdb(query, kind),
            "url" => await FetchUrl(query),
            _ => throw new ScrapeException($"Unknown source: {provider}")
        };
    }

    // ------------------------------------------------------------ images

    /// <summary>
    /// Identify an image by its leading bytes, falling back to the header.
    /// Plenty of hosts serve pictures as application/octet-stream or with no
    /// type at all, and a browser displays them regardless.
    /// </summary>
    public static string ImageSuffix(byte[] data, string contentType = "")
    {
        if (data.Length >= 12)
        {
            if (data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF) return ".jpg";
            if (data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47) return ".png";
            if (data[0] == 'G' && data[1] == 'I' && data[2] == 'F' && data[3] == '8') return ".gif";
            if (data[0] == 'R' && data[1] == 'I' && data[2] == 'F' && data[3] == 'F' &&
                data[8] == 'W' && data[9] == 'E' && data[10] == 'B' && data[11] == 'P') return ".webp";
            if (data[0] == 'B' && data[1] == 'M') return ".bmp";
        }

        return (contentType ?? "").Split(';')[0].Trim().ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => null
        };
    }

    /// <summary>
    /// Crop flat bars from around an image. Thumbnail services fit a landscape
    /// still into a portrait frame by padding it, which otherwise arrives as a
    /// small picture marooned in a big white box.
    /// </summary>
    public static byte[] TrimPadding(byte[] data, string suffix)
    {
        try
        {
            using var original = SixLabors.ImageSharp.Image.Load(new MemoryStream(data));
            using var rgb = original.CloneAs<Rgb24>();
            var width = rgb.Width;
            var height = rgb.Height;
            if (width < 40 || height < 40) return data;

            // Corners agreeing on one colour is what padding looks like.
            var corners = new[] { rgb[0, 0], rgb[width - 1, 0], rgb[0, height - 1], rgb[width - 1, height - 1] };
            if (corners.Distinct().Count() > 2) return data;
            var background = corners[0];

            int left = width, top = height, right = -1, bottom = -1;
            rgb.ProcessPixelRows(accessor =>
            {
                for (var y = 0; y < accessor.Height; y++)
                {
                    var row = accessor.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++)
                    {
                        var pixel = row[x];
                        var dr = Math.Abs(pixel.R - background.R);
                        var dg = Math.Abs(pixel.G - background.G);
                        var db = Math.Abs(pixel.B - background.B);
                        var luma = 0.299 * dr + 0.587 * dg + 0.114 * db;
                        if (luma <= 18) continue;
                        if (x < left) left = x;
                        if (x > right) right = x;
                        if (y < top) top = y;
                        if (y > bottom) bottom = y;
                    }
                }
            });

            if (right < 0) return data;
            var newWidth = right - left + 1;
            var newHeight = bottom - top + 1;
            if (newWidth < 40 || newHeight < 40) return data;
            // Ignore a crop that barely changes anything.
            if (newWidth * (double)newHeight > 0.94 * width * height) return data;

            original.Mutate(context =>
                context.Crop(new SixLabors.ImageSharp.Rectangle(left, top, newWidth, newHeight)));

            using var output = new MemoryStream();
            if (suffix == ".png") original.SaveAsPng(output);
            else original.SaveAsJpeg(output, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 92 });
            return output.ToArray();
        }
        catch
        {
            return data;
        }
    }

    /// <summary>Save a remote image into the app's own image folder.</summary>
    public static async Task<string> DownloadImage(string url, string folder, string stem, bool trim = false)
    {
        if (string.IsNullOrEmpty(url) ||
            !(url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
              url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
            return null;

        var origin = new Uri(url);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.ParseAdd("image/avif,image/webp,image/apng,image/*,*/*;q=0.8");
        // Some hosts only serve images to requests that look like a page load.
        request.Headers.Referrer = new Uri($"{origin.Scheme}://{origin.Host}/");

        byte[] data;
        string contentType;
        try
        {
            using var response = await Http.SendAsync(request);
            if (!response.IsSuccessStatusCode)
                throw new ScrapeException($"The image host refused the request ({(int)response.StatusCode}).");
            contentType = response.Content.Headers.ContentType?.MediaType ?? "";
            data = await response.Content.ReadAsByteArrayAsync();
        }
        catch (ScrapeException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ScrapeException($"Could not fetch the image: {ex.Message}");
        }

        if (data.Length > MaxImageBytes) throw new ScrapeException("That image is too large.");
        if (data.Length == 0) throw new ScrapeException("The image came back empty.");

        var suffix = ImageSuffix(data, contentType);
        if (suffix is null)
            throw new ScrapeException(
                $"That address did not return an image ({(contentType.Length > 0 ? contentType : "no content type")}).");

        if (trim) data = TrimPadding(data, suffix);

        var name = stem + suffix;
        await File.WriteAllBytesAsync(Path.Combine(folder, name), data);
        return name;
    }
}
