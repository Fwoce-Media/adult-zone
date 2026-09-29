using System.Globalization;
using System.Text;
using AdultZone.Core.Data;
using AdultZone.Core.Library;

namespace AdultZone.Core.Providers;

/// <summary>A target with more than one likely match, left for a person to pick.</summary>
public sealed class BatchChoice
{
    public long Id;
    public string Label = "";
    public List<Found> Options = new();
}

/// <summary>Two profiles whose names look like the same performer.</summary>
public sealed class BatchMerge
{
    public long FromId, IntoId;
    public string FromName = "", IntoName = "";
}

public sealed class BatchState
{
    public volatile bool Running;
    public int Total, Done, Succeeded, Skipped, Failed;
    public string Current = "";
    public readonly List<BatchChoice> Choices = new();
    public readonly List<BatchMerge> Merges = new();
    public readonly List<string> Errors = new();
    public readonly List<long> FailedIds = new();
}

public sealed record BatchOptions(string Kind, string Provider, HashSet<string> Fields, bool AutoMerge, bool SkipDone, bool Replace);

/// <summary>
/// Scrapes a whole library in one go. A clear match is applied; several close
/// matches wait for a pick; a name that is another profile's spelled
/// differently is merged, or held for a yes.
/// </summary>
public static class Batch
{
    public static readonly (string Key, string Label)[] ActorFields =
    {
        ("name", "Name"), ("description", "Biography"), ("birthdate", "Date of birth"), ("gender", "Gender"),
        ("country", "Nationality"), ("status", "Career status"), ("facts", "Details"), ("image", "Portrait"), ("banner", "Wide photo"),
    };

    public static readonly (string Key, string Label)[] VideoFields =
    {
        ("name", "Title"), ("description", "Description"), ("date", "Release date"), ("performers", "Cast"),
        ("site", "Sub-site"), ("tags", "Tags"), ("studio", "Studio"), ("image", "Thumbnail"),
    };

    sealed record Target(long Id, string Label, string Title, string Studio, string Date, List<string> Cast, string Kind = "scene");

    /// <summary>Letters and digits only, lower case, accents dropped: "Adriana Chechik" = "adriana-chéchik".</summary>
    public static string Norm(string text)
    {
        var sb = new StringBuilder();
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }

    public static int Count(string kind, bool skipDone) => Targets(kind, skipDone).Count;

    static List<Target> Targets(string kind, bool skipDone)
    {
        var output = new List<Target>();
        if (kind == "actor")
        {
            var done = skipDone ? "WHERE COALESCE(scraped, 0) = 0 AND COALESCE(source, '') = ''" : "";
            foreach (var r in Db.Query($"SELECT id, name FROM actors {done} ORDER BY name COLLATE NOCASE"))
                output.Add(new Target(r.Long("id") ?? 0, r.Str("name"), r.Str("name"), "", "", new()));
            return output;
        }
        var skip = skipDone ? "AND COALESCE(v.scraped, 0) = 0" : "";
        var rows = Db.Query($"""
            SELECT v.id, v.title, v.release_date, COALESCE(s.name, '') AS studio, COALESCE(v.kind, 'scene') AS kind,
                   (SELECT GROUP_CONCAT(a.name, '|') FROM video_actors va JOIN actors a ON a.id = va.actor_id WHERE va.video_id = v.id) AS cast_names
            FROM videos v LEFT JOIN studios s ON s.id = v.studio_id
            WHERE v.missing = 0 {skip} ORDER BY v.title COLLATE NOCASE
            """);
        foreach (var r in rows)
        {
            var cast = r.Str("cast_names").Split('|', StringSplitOptions.RemoveEmptyEntries).ToList();
            output.Add(new Target(r.Long("id") ?? 0, r.Str("title"), r.Str("title"), r.Str("studio"), r.Str("release_date"), cast, r.Str("kind")));
        }
        return output;
    }

    // ------------------------------------------------------------------ run
    /// <summary>Runs over every target, or only the ones listed (a retry of the failed).</summary>
    public static void Run(BatchOptions o, BatchState state, CancellationToken stop, Action tick, IReadOnlyCollection<long>? only = null)
    {
        var targets = only == null ? Targets(o.Kind, o.SkipDone) : Targets(o.Kind, false).Where(t => only.Contains(t.Id)).ToList();
        if (only == null) state.Total = targets.Count;
        state.Running = true;
        tick();
        try
        {
            foreach (var t in targets)
            {
                if (stop.IsCancellationRequested) break;
                state.Current = t.Label;
                tick();
                try
                {
                    // A dropped connection or a busy server gets two more tries before it counts as failed.
                    (Found? Pick, List<Found> All) found = default;
                    for (var attempt = 1; ; attempt++)
                    {
                        try { found = Find(o, t); break; }
                        catch (SourceError ex) when (attempt < 3 && (ex.Status == 0 || ex.Status == 429 || ex.Status >= 500) && !stop.IsCancellationRequested)
                        {
                            stop.WaitHandle.WaitOne(2000 * attempt);
                        }
                    }
                    var (pick, all) = found;
                    if (pick != null)
                    {
                        var applied = Apply(o, t.Id, pick, state);
                        if (applied.Count > 0) state.Succeeded++;
                        else state.Skipped++;
                    }
                    else if (all.Count == 0) state.Skipped++;
                    else lock (state) state.Choices.Add(new BatchChoice { Id = t.Id, Label = t.Label, Options = all.Take(10).ToList() });
                }
                catch (Exception ex)
                {
                    state.Failed++;
                    lock (state)
                    {
                        state.Errors.Add($"{t.Label}: {ex.Message}");
                        state.FailedIds.Add(t.Id);
                    }
                }
                if (only == null) state.Done++;
                tick();
                stop.WaitHandle.WaitOne(150);
            }
        }
        finally
        {
            state.Running = false;
            state.Current = "";
            Catalog.Touch();
            tick();
        }
    }

    static (Found? Pick, List<Found> All) Find(BatchOptions o, Target t)
    {
        var kind = o.Kind == "actor" ? "performer" : t.Kind == "movie" ? "movie" : "scene";
        var all = new List<Found>();
        foreach (var query in Queries(o.Kind, t))
        {
            var hits = Scrape.Search(o.Provider, kind, query);
            foreach (var h in hits)
                if (!all.Any(a => a.Name == h.Name && a.Url == h.Url && a.Id == h.Id)) all.Add(h);
            if (Pick(o.Kind, t, hits) is { } pick) return (pick, all);
        }
        return (null, all);
    }

    static IEnumerable<string> Queries(string kind, Target t)
    {
        if (kind == "actor")
        {
            yield return t.Title;
            yield break;
        }
        var castNames = t.Cast.Take(2).ToList();
        var titleIsCast = castNames.Count > 0 && Norm(t.Title) == Norm(string.Join("", castNames));
        var seen = new HashSet<string>();
        foreach (var q in new[]
        {
            titleIsCast ? "" : t.Title,
            titleIsCast || t.Studio.Length == 0 ? "" : $"{t.Studio} {t.Title}",
            castNames.Count > 0 && t.Studio.Length > 0 ? $"{t.Studio} {string.Join(" ", castNames)}" : "",
            castNames.Count > 0 ? string.Join(" ", castNames) : "",
        })
        {
            var clean = q.Trim();
            if (clean.Length > 0 && seen.Add(clean.ToLowerInvariant())) yield return clean;
        }
    }

    /// <summary>The one clear match, or null when it is not clear.</summary>
    static Found? Pick(string kind, Target t, List<Found> hits)
    {
        if (hits.Count == 0) return null;
        if (kind == "actor")
        {
            var want = Norm(t.Title);
            var exact = hits.Where(h => Norm(h.Name) == want || h.Aliases.Any(a => Norm(a) == want)).ToList();
            // Only a name or alias that matches is applied on its own; anything else waits for a pick.
            return exact.Count == 1 ? exact[0] : null;
        }
        int Score(Found h)
        {
            var score = 0;
            if (Norm(h.Name) == Norm(t.Title)) score += 2;
            if (t.Cast.Count > 0 && t.Cast.All(c => h.Performers.Any(p => Norm(p) == Norm(c)))) score += 2;
            if (t.Studio.Length > 0 && (Norm(h.Studio) == Norm(t.Studio) || Norm(h.Site) == Norm(t.Studio))) score += 1;
            if (t.Date.Length >= 10 && h.Date == t.Date[..10]) score += 2;
            return score;
        }
        var ranked = hits.Select(h => (Hit: h, Score: Score(h))).OrderByDescending(x => x.Score).ToList();
        if (ranked.Count == 1) return ranked[0].Score >= 2 ? ranked[0].Hit : null;
        if (ranked[0].Score >= 3 && ranked[1].Score < ranked[0].Score) return ranked[0].Hit;
        return null;
    }

    // ---------------------------------------------------------------- apply
    static Row? ActorNamed(string name, long notId)
    {
        var want = Norm(name);
        if (want.Length == 0) return null;
        var exact = Db.QueryOne("SELECT id, name FROM actors WHERE name = ? COLLATE NOCASE AND id != ?", name, notId);
        if (exact != null) return exact;
        return Db.Query("SELECT id, name FROM actors WHERE id != ?", notId).FirstOrDefault(r => Norm(r.Str("name")) == want);
    }

    /// <summary>Writes a match, auto-picked or chosen. Returns what was applied.</summary>
    public static List<string> Apply(BatchOptions o, long id, Found hit, BatchState state)
    {
        var f = hit.Copy();
        var picks = new HashSet<string>(o.Fields);
        if (o.Kind == "actor")
        {
            if (f.Source == "tpdb") f = Scrape.Complete(f);
            var current = Db.QueryOne("SELECT name FROM actors WHERE id = ?", id)?.Str("name") ?? "";
            var other = ActorNamed(f.Name, id);
            if (other == null)
            {
                // Another profile under one of this performer's other names folds into this one.
                Row? alias = null;
                foreach (var name in f.Aliases)
                    if ((alias = ActorNamed(name, id)) != null) break;
                if (alias == null) return Scrape.ApplyActor(id, f, picks);
                var aliasId = alias.Long("id") ?? 0;
                if (o.AutoMerge)
                {
                    Catalog.MergeActors(aliasId, id);
                    var merged = Scrape.ApplyActor(id, f, picks);
                    merged.Add("merged");
                    return merged;
                }
                var kept = Scrape.ApplyActor(id, f, picks);
                Ask(state, aliasId, alias.Str("name"), id, Db.QueryOne("SELECT name FROM actors WHERE id = ?", id)?.Str("name") ?? f.Name);
                return kept;
            }

            picks.Remove("name");
            var otherId = other.Long("id") ?? 0;
            if (o.AutoMerge)
            {
                Catalog.MergeActors(id, otherId);
                var applied = Scrape.ApplyActor(otherId, f, picks);
                applied.Add("merged");
                return applied;
            }
            var result = Scrape.ApplyActor(id, f, picks);
            Ask(state, id, current, otherId, other.Str("name"));
            if (result.Count == 0) result.Add("held for merge");
            return result;
        }

        var asks = new List<(string Scraped, long IntoId, string IntoName)>();
        if (picks.Contains("performers"))
        {
            var names = new List<string>();
            foreach (var p in f.Performers)
            {
                if (Db.QueryOne("SELECT id FROM actors WHERE name = ? COLLATE NOCASE", p) != null) { names.Add(p); continue; }
                var variant = ActorNamed(p, 0);
                if (variant == null) { names.Add(p); continue; }
                if (o.AutoMerge) names.Add(variant.Str("name"));
                else
                {
                    names.Add(p);
                    asks.Add((p, variant.Long("id") ?? 0, variant.Str("name")));
                }
            }
            f.Performers = names;
        }
        var done = Scrape.ApplyVideo(id, f, picks, o.Replace);
        foreach (var (scraped, intoId, intoName) in asks)
            if (Db.QueryOne("SELECT id FROM actors WHERE name = ? COLLATE NOCASE", scraped)?.Long("id") is long fromId)
                Ask(state, fromId, scraped, intoId, intoName);
        return done;
    }

    static void Ask(BatchState state, long fromId, string fromName, long intoId, string intoName)
    {
        lock (state)
        {
            if (state.Merges.Any(m => (m.FromId == fromId && m.IntoId == intoId) || (m.FromId == intoId && m.IntoId == fromId))) return;
            state.Merges.Add(new BatchMerge { FromId = fromId, FromName = fromName, IntoId = intoId, IntoName = intoName });
        }
    }
}
