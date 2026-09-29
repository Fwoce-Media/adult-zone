using System.Globalization;
using System.Text.Json.Nodes;
using AdultZone.Core.Data;

namespace AdultZone.Core.Media;

public sealed record ProbeInfo(double Duration, int Width, int Height);

/// <summary>
/// Everything made with ffmpeg: probing, poster frames, the looping previews
/// and the seek-bar frames. Files are named by a hash of the source path, as
/// 1.x named them, so a rescan never rebuilds what already exists.
/// </summary>
public static class Assets
{
    static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    static string F(double v) => v.ToString("0.00", Inv);

    public static PreviewProfile Profile => PreviewProfiles.Get(Db.Setting("preview_quality"));

    /// <summary>Output no wider than the source: upscaling only wastes disk.</summary>
    public static int TargetWidth(int sourceWidth, int ceiling) =>
        sourceWidth > 0 && sourceWidth < ceiling ? sourceWidth - sourceWidth % 2 : ceiling;

    public static int PreviewWidthFor(int sourceWidth) => TargetWidth(sourceWidth, Profile.Width);

    public static string QualityLabel(long height) => height switch
    {
        >= 2000 => "4K",
        >= 1400 => "2K",
        >= 700 => "HD",
        > 0 => "SD",
        _ => "",
    };

    public static ProbeInfo Probe(string path)
    {
        var empty = new ProbeInfo(0, 0, 0);
        var r = Ffmpeg.RunFfprobe(new[] { "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path }, 90);
        if (r.ExitCode != 0) return empty;
        JsonNode? data;
        try { data = JsonNode.Parse(r.StdOut.Length > 0 ? r.StdOut : "{}"); }
        catch { return empty; }
        double duration = Num(data?["format"]?["duration"]);
        int width = 0, height = 0;
        if (data?["streams"] is JsonArray streams)
            foreach (var s in streams)
            {
                if (s?["codec_type"]?.ToString() != "video" || width > 0) continue;
                width = (int)Num(s["width"]);
                height = (int)Num(s["height"]);
                // Portrait clips carry a rotation; report them the way they play.
                if (s["side_data_list"] is JsonArray sides)
                    foreach (var side in sides)
                        if (side?["rotation"] is { } rot && Math.Abs((int)Num(rot)) % 180 == 90)
                            (width, height) = (height, width);
                if (duration <= 0) duration = Num(s["duration"]);
            }
        return new ProbeInfo(duration, width, height);
    }

    static double Num(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<long>(out var l)) return l;
        if (v.TryGetValue<string>(out var s) && double.TryParse(s, NumberStyles.Float, Inv, out var p)) return p;
        return 0;
    }

    static bool Made(string path) => File.Exists(path) && new FileInfo(path).Length > 0;

    /// <summary>One frame about 30% in. Returns its file name, or null.</summary>
    public static string? MakeThumbnail(string source, double duration, bool force = false, int sourceWidth = 0)
    {
        var name = Config.KeyFor(source) + ".jpg";
        var dest = Path.Combine(Config.ThumbDir, name);
        if (!force && Made(dest)) return name;
        var at = duration > 0 ? Math.Max(1.0, duration * 0.3) : 5.0;
        foreach (var seek in new[] { at, 3.0, 0.5 })
        {
            var r = Ffmpeg.RunFfmpeg(new[]
            {
                "-y", "-ss", F(seek), "-i", source, "-frames:v", "1",
                "-vf", $"scale={TargetWidth(sourceWidth, PreviewProfiles.ThumbWidth)}:-2", "-q:v", "2", dest,
            }, 120);
            if (r.ExitCode == 0 && Made(dest)) return name;
        }
        return null;
    }

    /// <summary>The poster frame taken again from a chosen moment.</summary>
    public static string? ThumbFromTime(string source, double seconds)
    {
        var name = Config.KeyFor(source) + ".jpg";
        var dest = Path.Combine(Config.ThumbDir, name);
        var r = Ffmpeg.RunFfmpeg(new[]
        {
            "-y", "-ss", F(Math.Max(0, seconds)), "-i", source, "-frames:v", "1",
            "-vf", $"scale={PreviewProfiles.ThumbWidth}:-2", "-q:v", "2", dest,
        }, 120);
        return r.ExitCode == 0 && Made(dest) ? name : null;
    }

    /// <summary>A picture chosen by hand, kept as the video's poster.</summary>
    public static string ImportThumb(string source, string imageFile)
    {
        var ext = Path.GetExtension(imageFile).ToLowerInvariant();
        if (ext is not (".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")) ext = ".jpg";
        var name = $"{Config.KeyFor(source)}_custom{ext}";
        File.Copy(imageFile, Path.Combine(Config.ThumbDir, name), true);
        return name;
    }

    /// <summary>
    /// Several short moments from across the middle of the video, stitched
    /// into one silent loop. Each moment is its own seek, so long files stay fast.
    /// </summary>
    public static string? MakePreview(string source, double duration, bool force = false, int sourceWidth = 0)
    {
        var name = Config.KeyFor(source) + ".mp4";
        var dest = Path.Combine(Config.PreviewDir, name);
        if (!force && Made(dest)) return name;

        List<double> starts;
        if (duration < 8) starts = new() { Math.Max(0, duration * 0.2) };
        else
        {
            var head = duration * 0.1;
            var step = duration * 0.8 / PreviewProfiles.Segments;
            starts = Enumerable.Range(0, PreviewProfiles.Segments).Select(i => head + step * i).ToList();
        }
        var length = Math.Min(PreviewProfiles.SegmentSeconds, Math.Max(0.6, duration / Math.Max(starts.Count, 1)));

        var p = Profile;
        var width = TargetWidth(sourceWidth, p.Width);
        var args = new List<string> { "-y" };
        foreach (var s in starts) args.AddRange(new[] { "-ss", F(s), "-t", F(length), "-i", source });
        var chains = starts.Select((_, i) =>
            $"[{i}:v]scale={width}:-2:force_original_aspect_ratio=decrease:flags=lanczos," +
            $"pad={width}:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={p.Fps},format=yuv420p[v{i}]");
        var concat = string.Concat(starts.Select((_, i) => $"[v{i}]"));
        args.AddRange(new[]
        {
            "-filter_complex", string.Join(";", chains) + $";{concat}concat=n={starts.Count}:v=1:a=0[out]",
            "-map", "[out]", "-an", "-c:v", "libx264", "-preset", p.Preset, "-crf", p.Crf.ToString(Inv),
            "-tune", "film", "-movflags", "+faststart", "-pix_fmt", "yuv420p", dest,
        });
        var r = Ffmpeg.RunFfmpeg(args, 900);
        if (r.ExitCode == 0 && Made(dest)) return name;
        try { File.Delete(dest); } catch { }
        return null;
    }

    // ------------------------------------------------------------ seek frames
    public const int SeekFrames = 100;
    const int SeekFrameWidth = 240;

    public static string SeekFolder(string source) => Path.Combine(Config.SpriteDir, Config.KeyFor(source));

    /// <summary>Seconds between the frames shown over the seek bar.</summary>
    public static double SeekStep(double duration) => Math.Max(1, duration / SeekFrames);

    /// <summary>The frame nearest a moment, or null while they are still being made.</summary>
    public static string? SeekFrame(string source, double duration, double seconds)
    {
        if (duration <= 0) return null;
        var index = Math.Clamp((int)Math.Round(seconds / SeekStep(duration)), 0, SeekFrames - 1);
        var path = Path.Combine(SeekFolder(source), index.ToString("000", Inv) + ".jpg");
        return File.Exists(path) ? path : null;
    }

    public static bool HasSeekFrames(string source) => File.Exists(Path.Combine(SeekFolder(source), "done"));

    /// <summary>
    /// A small still every hundredth of the way through, for the picture that
    /// follows the mouse along the seek bar. Built once, the first time a video plays.
    /// </summary>
    public static void MakeSeekFrames(string source, double duration, CancellationToken cancel)
    {
        if (duration <= 0 || !Ffmpeg.HasFfmpeg || HasSeekFrames(source)) return;
        var folder = SeekFolder(source);
        Directory.CreateDirectory(folder);
        var step = SeekStep(duration);
        var count = Math.Min(SeekFrames, (int)Math.Ceiling(duration / step));
        Parallel.For(0, count, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = cancel }, i =>
        {
            var dest = Path.Combine(folder, i.ToString("000", Inv) + ".jpg");
            if (Made(dest)) return;
            var at = Math.Min(Math.Max(0, duration - 1), i * step);
            Ffmpeg.RunFfmpeg(new[]
            {
                "-y", "-ss", F(at), "-i", source, "-frames:v", "1",
                "-vf", $"scale={SeekFrameWidth}:-2", "-q:v", "5", dest,
            }, 60);
        });
        if (!cancel.IsCancellationRequested) File.WriteAllText(Path.Combine(folder, "done"), count.ToString(Inv));
    }

    /// <summary>Everything made for a video, removed.</summary>
    public static void Forget(string source)
    {
        var key = Config.KeyFor(source);
        foreach (var file in new[] { Path.Combine(Config.ThumbDir, key + ".jpg"), Path.Combine(Config.PreviewDir, key + ".mp4") })
            try { File.Delete(file); } catch { }
        try { Directory.Delete(SeekFolder(source), true); } catch { }
    }
}
