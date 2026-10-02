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

    sealed record Target(long Id, string Label, string Title, string Studio, string Date, List<string> Cast, string Kind = "scene",
                         string Site = "", string File = "");

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
                   COALESCE(v.subsite, '') AS site, v.path,
                   (SELECT GROUP_CONCAT(a.name, '|') FROM video_actors va JOIN actors a ON a.id = va.actor_id WHERE va.video_id = v.id) AS cast_names
            FROM videos v LEFT JOIN studios s ON s.id = v.studio_id
            WHERE v.missing = 0 {skip} ORDER BY v.title COLLATE NOCASE
            """);
        foreach (var r in rows)
        {
            // A "name" with digits in it was a title read into the cast by an old scan.
            var cast = r.Str("cast_names").Split('|', StringSplitOptions.RemoveEmptyEntries).Where(n => !n.Any(char.IsDigit)).ToList();
            output.Add(new Target(r.Long("id") ?? 0, r.Str("title"), r.Str("title"), r.Str("studio"), r.Str("release_date"), cast, r.Str("kind"),
                                  r.Str("site"), Path.GetFileNameWithoutExtension(r.Str("path"))));
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
        (Found? Pick, List<Found> All)? Take(List<Found> hits)
        {
            foreach (var h in hits)
                if (!all.Any(a => a.Name == h.Name && a.Url == h.Url && a.Id == h.Id)) all.Add(h);
            return Pick(o.Kind, t, all) is { } pick ? (pick, all) : null;
        }

        if (o.Kind == "video" && o.Provider == "tpdb" && kind == "scene")
        {
            var title = Clean(TitleIsCast(t) ? "" : t.Title);
            var who = string.Join(" ", t.Cast.Take(2));
            // The studio's own scenes first: its site on ThePornDB, searched by title, then by performers.
            foreach (var site in new[] { t.Site, t.Studio }.Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                foreach (var q in new[] { title, who, Words(Clean(t.File)).Count > 0 ? string.Join(" ", TitleWords(t).Take(4)) : "" }
                                  .Where(x => x.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    List<Found> scoped;
                    try { scoped = Scrape.TpdbSiteScenes(site, q); }
                    catch (SourceError ex) when (ex.Status is 400 or 404 or 422) { break; }
                    if (Take(scoped) is { } found) return found;
                }
            // ThePornDB reads the whole line as it would a file name.
            foreach (var line in new[] { Line(t), Clean(t.File) }.Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                List<Found> parsed;
                try { parsed = Scrape.TpdbParse(line); }
                catch (SourceError ex) when (ex.Status is 400 or 404 or 422) { continue; }
                // Only what agrees with something known is kept, in case the line was not understood.
                if (Take(parsed.Where(h => Score(t, h) > 0).ToList()) is { } found) return found;
            }
        }
        foreach (var query in Queries(o.Kind, t))
            if (Take(Scrape.Search(o.Provider, kind, query, o.Kind == "video" ? 20 : 8)) is { } found) return found;
        return (null, Rank(t, all).Select(x => x.Hit).Take(10).ToList());
    }

    /// <summary>
    /// A search by hand for one item in the review: the studio, performers and
    /// title as typed (a name for a performer). Everything found comes back, best first.
    /// </summary>
    public static List<Found> SearchAgain(BatchOptions o, long id, string studio, string performers, string title)
    {
        Target t;
        if (o.Kind == "actor") t = new Target(id, title, title.Trim(), "", "", new());
        else
        {
            var known = Targets("video", false).FirstOrDefault(x => x.Id == id);
            var cast = performers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            t = new Target(id, title, title.Trim(), studio.Trim(), known?.Date ?? "", cast, known?.Kind ?? "scene",
                           "", "");
        }
        var (pick, all) = Find(o, t);
        var ranked = o.Kind == "actor" ? all : Rank(t, all).Select(x => x.Hit).ToList();
        if (pick != null) ranked = new[] { pick }.Concat(ranked.Where(h => !ReferenceEquals(h, pick))).ToList();
        return ranked.Take(12).ToList();
    }

    static readonly System.Text.RegularExpressions.Regex Noise = new(
        @"\b(free\s+at\s+\S+|wow\.xxx|www\.\S+|xxx|1080p|720p|480p|2160p|4k|uhd|hd|sd|mp4|x264|x265|hevc|web-?dl|webrip)\b|[\[\](){}]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>A title or file name without release tags, brackets and the site that shared it.</summary>
    static string Clean(string text)
    {
        text = Noise.Replace(text, " ");
        text = Noise.Replace(text.Replace('_', ' ').Replace('.', ' '), " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\s*-\s*", " ");
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s{2,}", " ").Trim();
    }

    static readonly HashSet<string> Small = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "the", "of", "in", "on", "at", "with", "for", "to", "her", "his", "gets", "sc", "scene", "part", "free",
    };

    /// <summary>Words that mark an extra: behind the scenes, a trailer, an interview.</summary>
    static readonly HashSet<string> Extras = new() { "bts", "trailer", "teaser", "preview", "interview", "bloopers", "photoshoot" };

    /// <summary>The words that say something, lower case, accents dropped.</summary>
    static List<string> Words(string text) =>
        System.Text.RegularExpressions.Regex.Split(text, @"[^\p{L}\p{N}]+")
            .Select(Norm).Where(w => w.Length > 1 && !Small.Contains(w)).Distinct().ToList();

    /// <summary>Everything known about the video in words: its title and its file name, less the studio.</summary>
    static List<string> TitleWords(Target t)
    {
        var studio = Words(t.Studio).Concat(Words(t.Site)).ToHashSet();
        return Words(Clean(t.Title)).Concat(Words(Clean(t.File))).Where(w => !studio.Contains(w)).Distinct().ToList();
    }

    /// <summary>"Site Performer One Performer Two Title", the fullest description of a video.</summary>
    static string Line(Target t)
    {
        var who = string.Join(" ", t.Cast.Take(3));
        var title = TitleIsCast(t) ? "" : Clean(t.Title);
        return string.Join(" ", new[] { t.Site.Length > 0 ? t.Site : t.Studio, who, title }.Where(x => x.Length > 0)).Trim();
    }

    static bool TitleIsCast(Target t) =>
        t.Cast.Count > 0 && Norm(t.Title) == Norm(string.Join("", t.Cast.Take(3)));

    /// <summary>Most specific first: studio with performers and title, then fewer of them.</summary>
    static IEnumerable<string> Queries(string kind, Target t)
    {
        if (kind == "actor")
        {
            yield return t.Title;
            yield break;
        }
        var who = string.Join(" ", t.Cast.Take(2));
        var title = TitleIsCast(t) ? "" : Clean(t.Title);
        var where = t.Site.Length > 0 ? t.Site : t.Studio;
        var seen = new HashSet<string>();
        foreach (var parts in new[]
        {
            new[] { where, who, title },
            new[] { where, title },
            new[] { who, title },
            t.Site.Length > 0 && t.Studio.Length > 0 ? new[] { t.Studio, title } : Array.Empty<string>(),
            new[] { where, who },
            new[] { title },
            new[] { who },
        })
        {
            // A part missing leaves a looser search that a later line already covers.
            if (parts.Length == 0 || parts.Any(p => p.Length == 0) && parts.Length > 1) continue;
            var q = string.Join(" ", parts).Trim();
            if (q.Length > 0 && seen.Add(q.ToLowerInvariant())) yield return q;
        }
    }

    /// <summary>
    /// How well a match agrees with what is known. Titles are compared by the
    /// words they share, not letter for letter; performers count when their
    /// name appears in the title or file name too ("Alex and Maitland").
    /// </summary>
    static int Score(Target t, Found h)
    {
        var score = 0;
        var ours = TitleWords(t);
        var theirs = Words(Clean(h.Name));
        if (ours.Count > 0 && theirs.Count > 0)
        {
            var shared = theirs.Count(ours.Contains);
            var overlap = (double)shared / Math.Min(ours.Count, theirs.Count);
            if (overlap >= 0.8) score += 3;
            else if (overlap >= 0.5) score += 2;
            else if (overlap >= 0.3 && shared >= 2) score += 1;
            // The same words and nothing more: "A And B" is "A & B", and is not "A & B BTS".
            var studio = Words(t.Studio).Concat(Words(t.Site)).ToHashSet();
            var titleOnly = Words(Clean(t.Title)).Where(w => !studio.Contains(w)).ToHashSet();
            var theirSet = theirs.Where(w => !studio.Contains(w)).ToHashSet();
            if (theirSet.Count > 0 && (theirSet.SetEquals(titleOnly) || theirSet.SetEquals(ours))) score += 2;
            // An extra of the scene, not the scene, unless ours is one too.
            if (theirs.Any(w => Extras.Contains(w) && !ours.Contains(w)) || (h.Name.Contains("behind the scenes", StringComparison.OrdinalIgnoreCase) && !ours.Contains("behind")))
                score -= 3;
        }

        if (h.Performers.Count > 0)
        {
            var named = t.Cast.Count(c => h.Performers.Any(p => Norm(p) == Norm(c)));
            // A first or last name of theirs in our title or file name.
            var mentioned = h.Performers.Count(p => Words(p).Any(w => w.Length >= 3 && ours.Contains(w)) && !t.Cast.Any(c => Norm(c) == Norm(p)));
            if (t.Cast.Count > 0 && named == t.Cast.Count) score += 3;
            else if (named > 0) score += 2;
            if (mentioned > 0) score += Math.Min(2, mentioned);
            if (t.Cast.Count > 0 && named == 0 && mentioned == 0) score -= 2;
        }

        var known = new[] { t.Site, t.Studio }.Where(x => x.Length > 0).ToList();
        if (known.Count > 0 && (h.Site.Length > 0 || h.Studio.Length > 0))
        {
            if (known.Any(k => Norm(h.Site) == Norm(k) || Norm(h.Studio) == Norm(k))) score += 3;
            else score -= 1;
        }
        if (t.Date.Length >= 10 && h.Date == t.Date[..10]) score += 2;
        return score;
    }

    /// <summary>The same agreement score, for a video described by hand (used to check the matching).</summary>
    public static int ScoreOf(string title, string file, string studio, string site, string date, IEnumerable<string> cast, Found h) =>
        Score(new Target(0, title, title, studio, date, cast.ToList(), "scene", site, file), h);

    public static bool Picks(string title, string file, string studio, string site, IEnumerable<string> cast, List<Found> hits) =>
        Pick("video", new Target(0, title, title, studio, "", cast.ToList(), "scene", site, file), hits) != null;

    static List<(Found Hit, int Score)> Rank(Target t, List<Found> hits)
    {
        var ranked = hits.Select(h => (Hit: h, Score: Score(t, h))).OrderByDescending(x => x.Score).ToList();
        // Once anything agrees with what is known, the ones that agree with nothing drop out of the choices.
        if (ranked.Count > 0 && ranked[0].Score > 0) ranked = ranked.Where(x => x.Score > 0).ToList();
        return ranked;
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
        var ranked = Rank(t, hits);
        if (ranked.Count == 0) return null;
        var best = ranked[0].Score;
        // Two things agreeing (say title and performers, or performers and studio) and clearly ahead of the rest.
        return best >= 5 && (ranked.Count == 1 || best - ranked[1].Score >= 2) ? ranked[0].Hit : null;
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
        // Long result lists come trimmed; the one chosen is read in full before it is written.
        if (o.Kind == "video" && f.Source == "tpdb" && Db.QueryOne("SELECT kind FROM videos WHERE id = ?", id)?.Str("kind") != "movie")
            f = Scrape.CompleteScene(f);
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

    // ------------------------------------------------------------- sessions
    /// <summary>What a batch left to review, kept on disk so the review can be finished another time.</summary>
    sealed class Saved
    {
        public string Kind = "", Provider = "";
        public List<string> Fields = new();
        public bool AutoMerge, SkipDone, Replace;
        public int Total, Done, Succeeded, Skipped, Failed;
        public List<BatchChoice> Choices = new();
        public List<BatchMerge> Merges = new();
        public List<string> Errors = new();
        public List<long> FailedIds = new();
    }

    static readonly System.Text.Json.JsonSerializerOptions Json = new() { IncludeFields = true };

    static string SessionPath(string kind) => Path.Combine(Config.AppHome, $"batch-{(kind == "actor" ? "actor" : "video")}.json");

    /// <summary>Saves what is left to review, or clears the saved review when nothing is.</summary>
    public static void SaveSession(BatchOptions o, BatchState state)
    {
        var path = SessionPath(o.Kind);
        Saved saved;
        lock (state)
        {
            if (state.Choices.Count + state.Merges.Count + state.FailedIds.Count == 0)
            {
                try { File.Delete(path); } catch { }
                return;
            }
            saved = new Saved
            {
                Kind = o.Kind, Provider = o.Provider, Fields = o.Fields.ToList(), AutoMerge = o.AutoMerge, SkipDone = o.SkipDone, Replace = o.Replace,
                Total = state.Total, Done = state.Done, Succeeded = state.Succeeded, Skipped = state.Skipped, Failed = state.Failed,
                Choices = state.Choices.ToList(), Merges = state.Merges.ToList(), Errors = state.Errors.ToList(), FailedIds = state.FailedIds.ToList(),
            };
        }
        try
        {
            var temp = path + ".tmp";
            File.WriteAllText(temp, System.Text.Json.JsonSerializer.Serialize(saved, Json));
            File.Move(temp, path, true);
        }
        catch { }
    }

    /// <summary>A saved review for this kind of scrape, with the options it ran with; null when there is none.</summary>
    public static (BatchOptions Options, BatchState State)? LoadSession(string kind)
    {
        try
        {
            var path = SessionPath(kind);
            if (!File.Exists(path)) return null;
            var saved = System.Text.Json.JsonSerializer.Deserialize<Saved>(File.ReadAllText(path), Json);
            if (saved == null) return null;
            var state = new BatchState
            {
                Total = saved.Total, Done = saved.Done, Succeeded = saved.Succeeded, Skipped = saved.Skipped, Failed = saved.Failed,
            };
            // Anything reviewed some other way since (edited, deleted, merged) drops out.
            state.Choices.AddRange(saved.Choices.Where(c => kind == "actor"
                ? Db.QueryOne("SELECT id FROM actors WHERE id = ?", c.Id) != null
                : Db.QueryOne("SELECT id FROM videos WHERE id = ? AND missing = 0", c.Id) != null));
            state.Merges.AddRange(saved.Merges.Where(m =>
                Db.QueryOne("SELECT id FROM actors WHERE id = ?", m.FromId) != null && Db.QueryOne("SELECT id FROM actors WHERE id = ?", m.IntoId) != null));
            state.Errors.AddRange(saved.Errors);
            state.FailedIds.AddRange(saved.FailedIds);
            var options = new BatchOptions(saved.Kind, saved.Provider, saved.Fields.ToHashSet(), saved.AutoMerge, saved.SkipDone, saved.Replace);
            return (options, state);
        }
        catch { return null; }
    }

    /// <summary>How many items a saved review still holds, without loading it all.</summary>
    public static int SessionCount(string kind) =>
        LoadSession(kind) is { } s ? s.State.Choices.Count + s.State.Merges.Count + s.State.FailedIds.Count : 0;

    public static void ClearSession(string kind)
    {
        try { File.Delete(SessionPath(kind)); } catch { }
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
