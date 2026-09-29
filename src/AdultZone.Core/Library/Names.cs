using System.Globalization;
using System.Text.RegularExpressions;

namespace AdultZone.Core.Library;

public sealed class Parsed
{
    public string Title = "";
    public string Studio = "";
    public List<string> Actors = new();
    public string ReleaseDate = "";
}

/// <summary>
/// Best-effort details from a file name — studio, cast, date, title. The
/// patterns are 1.x's, so a library scans the same. Everything stays editable.
///
///   Studio - Actor Name.mp4
///   Studio - Actor One, Actor Two - 27.07.2021.mp4
///   Studio - Actor Name - Title.mp4
///   Actor Name - Title - [Studio].mp4
///   Studio.24.03.15.Actor.Name.Title.mp4
/// </summary>
public static class Names
{
    const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    static readonly (Regex Pattern, string Kind)[] DatePatterns =
    {
        (new Regex(@"(?<!\d)(20\d{2}|19\d{2})[-._/](\d{1,2})[-._/](\d{1,2})(?!\d)"), "ymd"),
        // 27.07.2021 -- day first, the usual European order
        (new Regex(@"(?<!\d)(\d{1,2})[-._/](\d{1,2})[-._/](20\d{2}|19\d{2})(?!\d)"), "dmy"),
        (new Regex(@"(?<!\d)(\d{2})[-._](\d{2})[-._](\d{2})(?!\d)"), "yymmdd"),
        (new Regex(@"[(\[]?(20\d{2}|19\d{2})[)\]]?"), "year"),
    };

    static readonly Regex Junk = new(
        @"\b(1080p|720p|480p|2160p|4k|uhd|hdrip|webrip|web-dl|bluray|x264|x265|h264|h265" +
        @"|hevc|aac|mp4|xxx|hd|sd|hq|rq|sample|part\d+|cd\d+)\b", I);

    static readonly Regex SplitActors = new(@"\s*(?:,|&|\+|\band\b)\s*", I);
    static readonly Regex Bracketed = new(@"[\[{]([^\]}]+)[\]}]");
    static readonly Regex BracketChars = new(@"[\[\](){}]");
    static readonly Regex Spaces = new(@"\s{2,}");
    static readonly Regex Dashes = new(@"\s+-\s+|\s+-\s*|\s*-\s+");

    static int N(string s) => int.Parse(s, CultureInfo.InvariantCulture);

    public static string Clean(string text)
    {
        text = text.Replace('_', ' ').Replace('.', ' ');
        text = Junk.Replace(text, " ");
        text = BracketChars.Replace(text, " ");
        text = Spaces.Replace(text, " ");
        return text.Trim(' ', '-', '.');
    }

    static bool IsDigits(string s) => s.Length > 0 && s.All(char.IsDigit);

    /// <summary>
    /// A performer's name rather than a scene title: short, no digits, one to
    /// four words — "Aaliyah Hadid" passes, "Aaliyah Gets What She Wants" not.
    /// </summary>
    static bool LooksLikeName(string text)
    {
        text = text.Trim();
        if (text.Length == 0 || text.Length > 40 || text.Any(char.IsDigit)) return false;
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        return words is >= 1 and <= 4;
    }

    static bool AllCase(string text, bool upper)
    {
        var cased = text.Where(char.IsLetter).ToList();
        return cased.Count > 0 && cased.All(c => upper ? char.IsUpper(c) : char.IsLower(c));
    }

    /// <summary>ALL CAPS or all lower case becomes Title Case; mixed case is left as typed.</summary>
    public static string TitleCase(string text)
    {
        if (!AllCase(text, true) && !AllCase(text, false)) return text;
        return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w =>
            w.All(char.IsLetter) ? char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant() : w));
    }

    static List<string> Cast(string text) =>
        SplitActors.Split(text).Where(a => a.Length > 0).Select(TitleCase).ToList();

    public static Parsed Parse(string path, string? root, bool folderAsStudio = true, bool twoPartActor = true)
    {
        var stem = Path.GetFileNameWithoutExtension(path);
        var output = new Parsed();
        var work = stem;

        // Studio in brackets, "Actor Name - Title - [Studio]": taken first,
        // since cleaning strips brackets. Release tags and years are not studios.
        var bracketStudio = "";
        foreach (Match m in Bracketed.Matches(stem))
        {
            var candidate = Clean(m.Groups[1].Value);
            if (candidate.Length < 2 || IsDigits(candidate)) continue;
            bracketStudio = TitleCase(candidate);
            var at = work.IndexOf(m.Value, StringComparison.Ordinal);
            if (at >= 0) work = work[..at] + " - " + work[(at + m.Value.Length)..];
            break;
        }

        foreach (var (pattern, kind) in DatePatterns)
        {
            var m = pattern.Match(work);
            if (!m.Success) continue;
            string date;
            if (kind == "ymd")
            {
                int mo = N(m.Groups[2].Value), d = N(m.Groups[3].Value);
                if (mo is < 1 or > 12 || d is < 1 or > 31) continue;
                date = $"{m.Groups[1].Value}-{mo:00}-{d:00}";
            }
            else if (kind == "dmy")
            {
                int d = N(m.Groups[1].Value), mo = N(m.Groups[2].Value);
                // Unless the first number cannot be a day, "27.07.2021" is day first.
                if (d > 12 && mo <= 12) { }
                else if (mo > 12 && d <= 12) (d, mo) = (mo, d);
                if (mo is < 1 or > 12 || d is < 1 or > 31) continue;
                date = $"{m.Groups[3].Value}-{mo:00}-{d:00}";
            }
            else if (kind == "yymmdd")
            {
                var yy = N(m.Groups[1].Value);
                var year = yy < 70 ? 2000 + yy : 1900 + yy;
                string mo = m.Groups[2].Value, d = m.Groups[3].Value;
                if (N(mo) is < 1 or > 12 || N(d) is < 1 or > 31) continue;
                date = $"{year}-{mo}-{d}";
            }
            else date = $"{m.Groups[1].Value}-01-01";
            output.ReleaseDate = date;
            work = work[..m.Index] + " - " + work[(m.Index + m.Length)..];
            break;
        }

        var parts = Dashes.Split(work).Select(Clean).Where(p => p.Length > 0).ToList();

        if (bracketStudio.Length > 0)
        {
            // The studio is known, so what is left reads "cast - title".
            output.Studio = bracketStudio;
            if (parts.Count >= 2)
            {
                output.Actors = Cast(parts[0]);
                output.Title = TitleCase(string.Join(" - ", parts.Skip(1)));
            }
            else if (parts.Count == 1) output.Title = TitleCase(parts[0]);
        }
        else if (parts.Count >= 3)
        {
            output.Studio = TitleCase(parts[0]);
            output.Actors = Cast(parts[1]);
            output.Title = TitleCase(string.Join(" - ", parts.Skip(2)));
        }
        else if (parts.Count == 2)
        {
            output.Studio = TitleCase(parts[0]);
            var second = parts[1];
            var names = SplitActors.Split(second).Where(n => n.Length > 0).ToList();
            // Only when the name really had a dash: dotted names can look
            // two-part just because a date was taken out of the middle.
            var trustTwoPart = twoPartActor && stem.Contains('-');
            if ((names.Count > 1 || trustTwoPart) && names.All(LooksLikeName))
                output.Actors = names.Select(TitleCase).ToList();
            output.Title = TitleCase(second);
        }
        else if (parts.Count == 1) output.Title = TitleCase(parts[0]);

        if (output.Title.Length == 0)
        {
            var cleaned = TitleCase(Clean(stem));
            output.Title = cleaned.Length > 0 ? cleaned : stem;
        }

        // Otherwise the folder the file sits in names the studio.
        if (folderAsStudio && output.Studio.Length == 0)
        {
            var parent = Path.GetDirectoryName(path) ?? "";
            if (root == null || !SamePath(parent, root))
            {
                var candidate = Clean(Path.GetFileName(parent));
                if (candidate.Length > 0 && candidate.Length < 60 && !IsDigits(candidate))
                    output.Studio = TitleCase(candidate);
            }
        }
        return output;
    }

    static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd('\\', '/'), b.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    public static string Initials(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
    }
}
