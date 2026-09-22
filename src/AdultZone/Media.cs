using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;

namespace AdultZone;

public record ProbeResult(double Duration, int Width, int Height);

/// <summary>
/// Everything that shells out to ffmpeg, plus the image work Pillow used to do.
/// Generated assets are cached on disk and keyed by a hash of the source path,
/// exactly as in the Python build, so an existing cache is reused rather than
/// rebuilt.
/// </summary>
public static class Media
{
    public static int PreviewSegments = 6;
    public static double PreviewSegmentSeconds = 1.4;
    public const int ThumbWidth = 1920;

    private static readonly int[] AllowedWidths = { 320, 480, 640, 960, 1280 };

    private static readonly Dictionary<string, (int Width, int Crf, int Fps, string Preset)> Profiles = new()
    {
        ["sd"] = (640, 30, 24, "veryfast"),
        ["high"] = (1280, 22, 30, "fast"),
        ["max"] = (1920, 20, 30, "fast")
    };

    public static (int Width, int Crf, int Fps, string Preset) ActiveProfile()
    {
        var name = Db.GetSetting("preview_quality", "high");
        return Profiles.TryGetValue(name ?? "high", out var profile) ? profile : Profiles["high"];
    }

    public static string ProfileName()
    {
        var name = Db.GetSetting("preview_quality", "high");
        return Profiles.ContainsKey(name ?? "") ? name : "high";
    }

    /// <summary>Stable cache key for a source file, matching the Python build.</summary>
    public static string KeyFor(string path)
    {
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(path ?? string.Empty));
        return Convert.ToHexString(bytes).ToLowerInvariant()[..20];
    }

    public static string QualityLabel(int height) => height switch
    {
        >= 2000 => "4K",
        >= 1400 => "2K",
        >= 700 => "HD",
        > 0 => "SD",
        _ => ""
    };

    // ----------------------------------------------------------- processes

    private static (int ExitCode, string StdOut) Run(string exe, IEnumerable<string> args, int timeoutMs)
    {
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var arg in args) info.ArgumentList.Add(arg);

        try
        {
            using var process = Process.Start(info);
            if (process is null) return (-1, string.Empty);

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();

            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(true); } catch { }
                return (-1, string.Empty);
            }

            stderr.Wait(1000);
            return (process.ExitCode, stdout.Result ?? string.Empty);
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[media] {exe}: {ex.Message}");
            return (-1, string.Empty);
        }
    }

    public static ProbeResult Probe(string path)
    {
        var (code, output) = Run(AppPaths.FFprobe, new[]
        {
            "-v", "quiet", "-print_format", "json", "-show_format", "-show_streams", path
        }, 90_000);

        if (code != 0 || string.IsNullOrWhiteSpace(output)) return new ProbeResult(0, 0, 0);

        try
        {
            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement;

            double duration = 0;
            if (root.TryGetProperty("format", out var format) &&
                format.TryGetProperty("duration", out var durationValue) &&
                double.TryParse(durationValue.GetString(), NumberStyles.Float,
                                CultureInfo.InvariantCulture, out var parsed))
            {
                duration = parsed;
            }

            int width = 0, height = 0;
            if (root.TryGetProperty("streams", out var streams))
            {
                foreach (var stream in streams.EnumerateArray())
                {
                    if (!stream.TryGetProperty("codec_type", out var type) ||
                        type.GetString() != "video") continue;

                    width = stream.TryGetProperty("width", out var w) ? w.GetInt32() : 0;
                    height = stream.TryGetProperty("height", out var h) ? h.GetInt32() : 0;

                    // Respect rotation so portrait clips report their true shape.
                    if (stream.TryGetProperty("side_data_list", out var sideData))
                    {
                        foreach (var entry in sideData.EnumerateArray())
                        {
                            if (!entry.TryGetProperty("rotation", out var rotation)) continue;
                            var degrees = Math.Abs(rotation.GetInt32()) % 180;
                            if (degrees == 90) (width, height) = (height, width);
                        }
                    }
                    break;
                }
            }

            return new ProbeResult(duration, width, height);
        }
        catch
        {
            return new ProbeResult(0, 0, 0);
        }
    }

    /// <summary>Target width capped at the source; upscaling only wastes disk.</summary>
    private static int TargetWidth(int sourceWidth, int ceiling)
    {
        if (sourceWidth > 0 && sourceWidth < ceiling) return sourceWidth - (sourceWidth % 2);
        return ceiling;
    }

    public static string MakeThumbnail(string source, double duration, bool force = false, int sourceWidth = 0)
    {
        var name = KeyFor(source) + ".jpg";
        var destination = Path.Combine(AppPaths.ThumbDir, name);

        if (!force && File.Exists(destination) && new FileInfo(destination).Length > 0) return name;

        var primary = duration > 0 ? Math.Max(1.0, duration * 0.3) : 5.0;
        foreach (var seek in new[] { primary, 3.0, 0.5 })
        {
            var (code, _) = Run(AppPaths.FFmpeg, new[]
            {
                "-y", "-ss", seek.ToString("0.00", CultureInfo.InvariantCulture), "-i", source,
                "-frames:v", "1", "-vf", $"scale={TargetWidth(sourceWidth, ThumbWidth)}:-2",
                "-q:v", "2", destination
            }, 120_000);

            if (code == 0 && File.Exists(destination) && new FileInfo(destination).Length > 0) return name;
        }
        return null;
    }

    /// <summary>
    /// Several short moments stitched into one silent looping clip. Input-level
    /// seeking keeps long files fast: ffmpeg jumps straight to each keyframe
    /// rather than decoding the whole video.
    /// </summary>
    public static string MakePreview(string source, double duration, bool force = false, int sourceWidth = 0)
    {
        var name = KeyFor(source) + ".mp4";
        var destination = Path.Combine(AppPaths.PreviewDir, name);

        if (!force && File.Exists(destination) && new FileInfo(destination).Length > 0) return name;

        var starts = new List<double>();
        int segments;
        if (duration < 8)
        {
            segments = 1;
            starts.Add(Math.Max(0, duration * 0.2));
        }
        else
        {
            segments = PreviewSegments;
            var span = duration * 0.8;      // skip logos and end cards
            var head = duration * 0.1;
            var step = span / segments;
            for (var i = 0; i < segments; i++) starts.Add(head + step * i);
        }

        var segmentLength = Math.Min(PreviewSegmentSeconds, Math.Max(0.6, duration / Math.Max(segments, 1)));
        var profile = ActiveProfile();
        var width = TargetWidth(sourceWidth, profile.Width);

        var args = new List<string> { "-y" };
        foreach (var start in starts)
        {
            args.Add("-ss");
            args.Add(start.ToString("0.00", CultureInfo.InvariantCulture));
            args.Add("-t");
            args.Add(segmentLength.ToString("0.00", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(source);
        }

        var chains = new List<string>();
        var concatInputs = new StringBuilder();
        for (var i = 0; i < starts.Count; i++)
        {
            chains.Add(
                $"[{i}:v]scale={width}:-2:force_original_aspect_ratio=decrease:flags=lanczos," +
                $"pad={width}:ceil(ih/2)*2:(ow-iw)/2:(oh-ih)/2," +
                $"setsar=1,fps={profile.Fps},format=yuv420p[v{i}]");
            concatInputs.Append($"[v{i}]");
        }

        var filter = string.Join(";", chains) +
                     $";{concatInputs}concat=n={starts.Count}:v=1:a=0[out]";

        args.AddRange(new[]
        {
            "-filter_complex", filter, "-map", "[out]", "-an",
            "-c:v", "libx264", "-preset", profile.Preset, "-crf", profile.Crf.ToString(),
            "-tune", "film", "-movflags", "+faststart", "-pix_fmt", "yuv420p", destination
        });

        var (code, _) = Run(AppPaths.FFmpeg, args, 900_000);
        if (code == 0 && File.Exists(destination) && new FileInfo(destination).Length > 0) return name;

        try { File.Delete(destination); } catch { }
        return null;
    }

    public static int PreviewWidthFor(int sourceWidth) => TargetWidth(sourceWidth, ActiveProfile().Width);

    public static string ThumbFromTimestamp(string source, double start)
    {
        var name = KeyFor(source) + ".jpg";
        var destination = Path.Combine(AppPaths.ThumbDir, name);
        var (code, _) = Run(AppPaths.FFmpeg, new[]
        {
            "-y", "-ss", start.ToString("0.00", CultureInfo.InvariantCulture), "-i", source,
            "-frames:v", "1", "-vf", $"scale={ThumbWidth}:-2", "-q:v", "2", destination
        }, 120_000);
        return code == 0 && File.Exists(destination) ? name : null;
    }

    // -------------------------------------------------------- sized copies

    /// <summary>
    /// A cached copy of a thumbnail resampled to the width the page will use.
    /// Cards are about 320px wide while the poster is 1920, and letting the
    /// browser bridge that in one step aliases fine detail into a shimmer.
    /// Returns null when the source is already small enough.
    /// </summary>
    public static string SizedThumb(string name, int width)
    {
        if (string.IsNullOrEmpty(name) || Array.IndexOf(AllowedWidths, width) < 0) return null;

        var source = Path.Combine(AppPaths.ThumbDir, name);
        if (!File.Exists(source)) return null;

        var outName = $"{Path.GetFileNameWithoutExtension(name)}_{width}.jpg";
        var outPath = Path.Combine(AppPaths.SizedDir, outName);

        try
        {
            if (File.Exists(outPath) &&
                File.GetLastWriteTimeUtc(outPath) >= File.GetLastWriteTimeUtc(source))
                return outName;
        }
        catch { }

        try
        {
            // Fully qualified: WinForms imports System.Drawing, which has its
            // own Image type, so the bare name is ambiguous.
            using var image = SixLabors.ImageSharp.Image.Load(source);
            if (image.Width <= width * 1.15) return null;

            var height = (int)Math.Round(image.Height * (double)width / image.Width);
            image.Mutate(context => context.Resize(width, height, KnownResamplers.Lanczos3));
            image.SaveAsJpeg(outPath);
            return outName;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"[sized] {name}: {ex.Message}");
            return null;
        }
    }
}
