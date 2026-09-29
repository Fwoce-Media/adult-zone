using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AdultZone.Core.Providers;

/// <summary>A source answered, but not with what was asked for — or could not be reached.</summary>
public sealed class SourceError : Exception
{
    public int Status { get; }
    public SourceError(string message, int status = 0) : base(message) => Status = status;
}

public static class Http
{
    public const string UserAgent = "AdultZone/3.0 (personal media library)";
    public const int MaxImageBytes = 12 * 1024 * 1024;

    static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Config.ProviderTimeout,
    };

    static HttpResponseMessage Send(string url, IDictionary<string, string>? headers)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        if (headers != null)
            foreach (var (k, v) in headers) request.Headers.TryAddWithoutValidation(k, v);
        try { return Client.Send(request); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            throw new SourceError("Could not reach that source: " + ex.Message);
        }
    }

    static string Body(HttpResponseMessage response)
    {
        using var stream = response.Content.ReadAsStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    public static string GetText(string url, IDictionary<string, string>? headers = null)
    {
        using var response = Send(url, headers);
        var text = Body(response);
        if (!response.IsSuccessStatusCode) throw new SourceError(Explain(response, text), (int)response.StatusCode);
        return text;
    }

    public static JsonNode? GetJson(string url, IDictionary<string, string>? headers = null)
    {
        var text = GetText(url, headers);
        try { return JsonNode.Parse(text); }
        catch (JsonException) { throw new SourceError("The source sent something that was not JSON."); }
    }

    /// <summary>An error worth reading: what the source said, and what it usually means.</summary>
    static string Explain(HttpResponseMessage response, string body)
    {
        if (response.Headers.TryGetValues("x-deny-reason", out var reasons) && reasons.FirstOrDefault() is { Length: > 0 } reason)
            return $"A network filter blocked the request ({reason}). This is your network or proxy, not the source.";
        var detail = "";
        try
        {
            var parsed = JsonNode.Parse(body);
            detail = parsed?["message"]?.ToString() ?? parsed?["error"]?.ToString() ?? "";
        }
        catch { }
        var said = detail.Length > 0 ? " It said: " + detail : "";
        return (int)response.StatusCode switch
        {
            401 => "The source rejected the API key (401). Check it was copied in full and is still active." + said,
            403 => "The key is not allowed to do that (403). It may lack permission, or the subscription may have lapsed." + said,
            429 => "That source is rate limiting you (429). Wait a minute and try again.",
            404 => "That address returned nothing (404).",
            var code => $"The source returned HTTP {code}." + said,
        };
    }

    /// <summary>A picture's bytes and what kind of picture it is.</summary>
    public static (byte[] Data, string Ext) GetImage(string url)
    {
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            throw new SourceError("That is not a web address.");
        var origin = new Uri(url);
        var headers = new Dictionary<string, string>
        {
            ["Accept"] = "image/avif,image/webp,image/apng,image/*,*/*;q=0.8",
            // Some hosts only hand out pictures to what looks like a page load.
            ["Referer"] = $"{origin.Scheme}://{origin.Host}/",
        };
        using var response = Send(url, headers);
        if (!response.IsSuccessStatusCode) throw new SourceError($"The image host refused the request ({(int)response.StatusCode}).");
        using var stream = response.Content.ReadAsStream();
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            memory.Write(buffer, 0, read);
            if (memory.Length > MaxImageBytes) throw new SourceError("That image is too large.");
        }
        var data = memory.ToArray();
        if (data.Length == 0) throw new SourceError("The image came back empty.");
        var type = response.Content.Headers.ContentType?.MediaType ?? "";
        var ext = ImageExt(data, type) ?? throw new SourceError($"That address did not return an image ({(type.Length > 0 ? type : "no content type")}).");
        return (data, ext);
    }

    /// <summary>What a picture is, from its first bytes — many hosts label pictures wrongly or not at all.</summary>
    public static string? ImageExt(byte[] d, string contentType = "")
    {
        if (d.Length >= 12)
        {
            if (d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF) return ".jpg";
            if (d[0] == 0x89 && d[1] == (byte)'P' && d[2] == (byte)'N' && d[3] == (byte)'G') return ".png";
            if (d[0] == (byte)'G' && d[1] == (byte)'I' && d[2] == (byte)'F') return ".gif";
            if (Encoding.ASCII.GetString(d, 0, 4) == "RIFF" && Encoding.ASCII.GetString(d, 8, 4) == "WEBP") return ".webp";
            if (d[0] == (byte)'B' && d[1] == (byte)'M') return ".bmp";
        }
        return contentType.ToLowerInvariant() switch
        {
            "image/jpeg" or "image/jpg" => ".jpg",
            "image/png" => ".png",
            "image/webp" => ".webp",
            "image/gif" => ".gif",
            "image/bmp" => ".bmp",
            _ => null,
        };
    }
}
