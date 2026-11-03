using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Noctis.Services;

/// <summary>
/// "About the artist" facts for the artist page, from two open, maintained sources
/// (no API key, no scraping):
/// <list type="bullet">
/// <item><b>MusicBrainz</b> (artist search + lookup with url-rels/genres): type, origin,
/// born/formed, active years, genres, official site — the community database every
/// tagger (Picard, beets) writes from, and the one that carries the Wikidata link.</item>
/// <item><b>Wikipedia</b> REST summary, reached through that Wikidata link (so the article
/// is the artist's, never a same-name page): the lead paragraph as the bio.</item>
/// </list>
/// <b>Wikidata</b> also stands in for both when MusicBrainz sheds load: its name search
/// (accepted only for an item with a MusicBrainz id) identifies the artist, and its claims
/// (birthplace / formation, dates, genres, country) fill the facts.
/// Results are cached per artist id under <c>artist_info/</c> — 7 days for a hit, 1 day
/// for a miss or a partial hit — and every MusicBrainz call is paced to its 1 request/second rule.
/// </summary>
public sealed class ArtistInfoService
{
    private const string MusicBrainzBase = "https://musicbrainz.org/ws/2/artist/";
    private const string MusicBrainzUrlLookup = "https://musicbrainz.org/ws/2/url";
    private const string WikidataApi = "https://www.wikidata.org/w/api.php";
    private const string WikipediaSummary = "https://en.wikipedia.org/api/rest_v1/page/summary/";
    /// <summary>A hit refreshes weekly; a confirmed "no such artist" is retried daily.
    /// Transient failures are never cached at all (see <see cref="GetTextAsync"/>).</summary>
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(7);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(1);
    /// <summary>MusicBrainz's search endpoint load-sheds with 503 "server is currently
    /// busy" (X-RateLimit-Zone: search-shed) for seconds at a time — measured 09-13: one
    /// 200 then two 503s three seconds apart. One retry each: past that, Wikidata's name
    /// search identifies the artist and its claims stand in for the lookup.</summary>
    private const int MusicBrainzSearchAttempts = 2;
    private const int MusicBrainzLookupAttempts = 2;
    private static readonly TimeSpan MusicBrainzRetryDelay = TimeSpan.FromSeconds(2);
    /// <summary>Bumped when the cached shape gains a field older entries lack (2: Links) or
    /// the pipeline learns a new way to find an artist (3: Wikidata lookup by MusicBrainz
    /// id, transient failures no longer cached as misses), so older entries refetch.</summary>
    internal const int CurrentSchema = 3;

    private static readonly string UserAgent =
        $"Noctis/{Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0"} (https://github.com/heartached/Noctis)";

    private readonly HttpClient _http;
    private readonly string _dir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTime _lastMusicBrainzCall = DateTime.MinValue;

    public ArtistInfoService(HttpClient http, IPersistenceService persistence)
    {
        _http = http;
        _dir = Path.Combine(persistence.DataDirectory, "artist_info");
        try { Directory.CreateDirectory(_dir); } catch { }
    }

    /// <summary>Whatever the cache holds for the artist, stale or not — shown at once while
    /// <see cref="GetAsync"/> refreshes, so a revisit never waits on the network. Null when
    /// the cache has nothing usable (no entry, or a miss).</summary>
    public ArtistInfo? TryGetCached(Guid artistId)
    {
        var cached = ReadCache(Path.Combine(_dir, $"{artistId}.json"));
        return cached is { Found: true } ? cached : null;
    }

    /// <summary>Cached or freshly fetched facts; null when nothing reliable was found.</summary>
    public async Task<ArtistInfo?> GetAsync(Guid artistId, string artistName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || artistName == "Unknown Artist") return null;

        var path = Path.Combine(_dir, $"{artistId}.json");
        var cached = ReadCache(path);
        if (cached != null && !IsStale(cached))
            return cached.Found ? cached : null;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var info = await FetchAsync(artistName, ct, GetPinnedMusicBrainzId(artistId)).ConfigureAwait(false)
                       ?? new ArtistInfo { Name = artistName, Found = false };
            info.FetchedAtUtc = DateTime.UtcNow;
            info.Schema = CurrentSchema;
            WriteCache(path, info);
            return info.Found ? info : null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // Offline or a source hiccup: keep serving a stale hit rather than nothing.
            DebugLogger.Warn(DebugLogger.Category.Error, "ArtistInfo.Fetch", $"{artistName}: {ex.GetType().Name}: {ex.Message}");
            return cached is { Found: true } ? cached : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    // ── Manual pick (GitHub #141: "Search Artist Info" on the artist page) ──

    private string PinPath(Guid artistId) => Path.Combine(_dir, $"{artistId}.mbid");

    /// <summary>The MusicBrainz artist the user's pick resolved to, or null (automatic: found by name).</summary>
    public string? GetPinnedMusicBrainzId(Guid artistId)
    {
        try
        {
            var path = PinPath(artistId);
            if (!File.Exists(path)) return null;
            var mbid = File.ReadAllText(path).Trim();
            return Guid.TryParse(mbid, out _) ? mbid : null;
        }
        catch { return null; }
    }

    /// <summary>Pins the artist's facts to a MusicBrainz id (null: back to the name search) and
    /// drops the cached entry, so the next <see cref="GetAsync"/> fetches the chosen artist.</summary>
    public void SetPinnedMusicBrainzId(Guid artistId, string? mbid)
    {
        try
        {
            if (mbid != null && Guid.TryParse(mbid, out _)) File.WriteAllText(PinPath(artistId), mbid);
            else if (File.Exists(PinPath(artistId))) File.Delete(PinPath(artistId));
            var cache = Path.Combine(_dir, $"{artistId}.json");
            if (File.Exists(cache)) File.Delete(cache);
        }
        catch { }
    }

    /// <summary>
    /// The MusicBrainz artist behind a Deezer account: MusicBrainz's own link to the Deezer
    /// page, else the Wikidata item carrying that Deezer id (P2722) and a MusicBrainz id
    /// (P434). Null when neither knows it. Network failures propagate.
    /// </summary>
    public async Task<string?> FindMusicBrainzIdForDeezerAsync(long deezerId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var id = deezerId.ToString(CultureInfo.InvariantCulture);
            var urlJson = await GetMusicBrainzAsync(
                $"{MusicBrainzUrlLookup}?resource={Uri.EscapeDataString("https://www.deezer.com/artist/" + id)}&inc=artist-rels&fmt=json",
                MusicBrainzLookupAttempts, ct).ConfigureAwait(false);
            if (urlJson != null)
            {
                using var doc = JsonDocument.Parse(urlJson);
                if (ParseUrlArtist(doc.RootElement) is { } mbid) return mbid;
            }

            var searchJson = await GetTextAsync(
                $"{WikidataApi}?action=query&list=search&srsearch={Uri.EscapeDataString("haswbstatement:P2722=" + id)}&srlimit=1&format=json", ct)
                .ConfigureAwait(false);
            if (searchJson == null) return null;
            string? qid;
            using (var doc = JsonDocument.Parse(searchJson))
                qid = ParseWikidataSearch(doc.RootElement);
            if (qid == null) return null;
            var entityJson = await GetTextAsync(
                $"{WikidataApi}?action=wbgetentities&ids={qid}&props=claims&format=json", ct).ConfigureAwait(false);
            if (entityJson == null) return null;
            using var entity = JsonDocument.Parse(entityJson);
            return ParseMusicBrainzClaim(entity.RootElement, qid);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>The artist a MusicBrainz <c>url</c> lookup (inc=artist-rels) links to, or null.
    /// Several artists sharing one page (a duo's account) is no answer.</summary>
    internal static string? ParseUrlArtist(JsonElement root)
    {
        if (!root.TryGetProperty("relations", out var rels) || rels.ValueKind != JsonValueKind.Array) return null;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var r in rels.EnumerateArray())
        {
            if (r.ValueKind == JsonValueKind.Object && r.TryGetProperty("artist", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                && Guid.TryParse(idEl.GetString(), out _))
                ids.Add(idEl.GetString()!);
        }
        return ids.Count == 1 ? ids.First() : null;
    }

    internal static bool IsStale(ArtistInfo info)
        => info.Schema < CurrentSchema
           || DateTime.UtcNow - info.FetchedAtUtc > (info.Found && !info.Partial ? HitTtl : MissTtl);

    private static ArtistInfo? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<ArtistInfo>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteCache(string path, ArtistInfo info)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(info));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private async Task<ArtistInfo?> FetchAsync(string artistName, CancellationToken ct, string? pinnedMbid = null)
    {
        // 1. Identify: MusicBrainz search (one retry through its load-shedding), else
        //    Wikidata's name search accepted only for an item that carries a MusicBrainz
        //    artist id (P434) — both give the id; the second also gives the article.
        //    An artist the user picked (GitHub #141) skips the search.
        string? mbid = pinnedMbid, qid = null;
        if (mbid == null)
        try
        {
            var searchJson = await GetMusicBrainzAsync(
                $"{MusicBrainzBase}?query={Uri.EscapeDataString("artist:\"" + artistName.Replace("\"", "") + "\"")}&fmt=json&limit=5",
                MusicBrainzSearchAttempts, ct);
            if (searchJson != null)
                using (var searchDoc = JsonDocument.Parse(searchJson))
                    mbid = PickBestMatch(searchDoc.RootElement, artistName);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            mbid = null; // search shed: try the other door
        }
        if (mbid == null)
        {
            (mbid, qid) = await FindViaWikidataAsync(artistName, ct);
            if (mbid == null) return null;
        }

        // 2. Two lanes at once. MusicBrainz lane: the lookup (relations, genres, dates),
        //    paced and retried. Wikidata lane: the item for that MusicBrainz id, then its
        //    sitelinks + claims, then the Wikipedia lead — never waits on MusicBrainz, so a
        //    shed lookup costs the link row, not the biography, and the page shows in
        //    max(lanes) rather than their sum.
        var lookupTask = LookupMusicBrainzAsync(mbid, artistName, ct);
        var wiki = new ArtistInfo { Name = artistName, MusicBrainzId = mbid, MusicBrainzUrl = "https://musicbrainz.org/artist/" + mbid, WikidataId = qid ?? "" };
        var wikiTask = ResolveWikipediaLaneAsync(wiki, ct);
        await Task.WhenAll(lookupTask, wikiTask).ConfigureAwait(false);
        var facts = wikiTask.Result;

        var info = lookupTask.Result;
        if (info != null)
        {
            if (string.IsNullOrEmpty(info.WikidataId)) info.WikidataId = wiki.WikidataId;
            info.Bio = wiki.Bio; info.BioSource = wiki.BioSource; info.ShortDescription = wiki.ShortDescription;
            if (wiki.WikipediaUrl.Length > 0) info.WikipediaUrl = wiki.WikipediaUrl;
            // Rare: Wikidata has no P434 index entry for the id but MusicBrainz carries the
            // Wikidata / Wikipedia relation — take the article through that instead.
            if (!info.HasBio && wiki.WikidataId.Length == 0 && (info.WikidataId.Length > 0 || info.WikipediaUrl.Length > 0))
                await ResolveWikipediaLaneAsync(info, ct).ConfigureAwait(false);
        }
        else
        {
            // Shed: Wikidata's claims stand in for FROM / BORN / GENRE (labelled in one
            // more call); the entry is partial and retried on the miss clock for the links.
            info = wiki;
            info.Partial = true;
            if (facts != null)
            {
                var labels = new Dictionary<string, string>();
                if (facts.ItemIds.Count > 0)
                {
                    var labelsJson = await GetTextAsync(
                        $"{WikidataApi}?action=wbgetentities&ids={string.Join("|", facts.ItemIds)}&props=labels&languages=en&format=json", ct);
                    if (labelsJson != null)
                    {
                        using var labelsDoc = JsonDocument.Parse(labelsJson);
                        labels = ParseWikidataLabels(labelsDoc.RootElement);
                    }
                }
                ApplyWikidataFacts(info, facts, labels);
            }
        }

        info.Found = true;
        return info;
    }

    /// <summary>The Wikidata lane: item (by id, or by MusicBrainz id), then sitelinks +
    /// claims in one call, then the English Wikipedia lead into <paramref name="info"/>.
    /// Returns the parsed claims (for a shed lookup), null when there was no item.</summary>
    private async Task<WikidataFacts?> ResolveWikipediaLaneAsync(ArtistInfo info, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(info.WikidataId) && info.MusicBrainzId.Length > 0)
            info.WikidataId = await FindWikidataByMusicBrainzIdAsync(info.MusicBrainzId, ct).ConfigureAwait(false) ?? "";
        WikidataFacts? facts = null;
        string? title = null;
        if (!string.IsNullOrEmpty(info.WikidataId))
        {
            var json = await GetTextAsync(
                $"{WikidataApi}?action=wbgetentities&ids={info.WikidataId}&props=sitelinks|claims&sitefilter=enwiki&format=json", ct);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                title = ParseWikidataTitle(doc.RootElement, info.WikidataId);
                facts = ParseWikidataFacts(doc.RootElement, info.WikidataId);
            }
        }
        title ??= TitleFromWikipediaUrl(info.WikipediaUrl);
        if (title != null)
        {
            var summaryJson = await GetTextAsync(WikipediaSummary + Uri.EscapeDataString(title.Replace(' ', '_')), ct);
            if (summaryJson != null)
            {
                using var summaryDoc = JsonDocument.Parse(summaryJson);
                ApplyWikipediaSummary(summaryDoc.RootElement, info);
            }
        }
        return facts;
    }

    private static string? TitleFromWikipediaUrl(string url)
    {
        if (string.IsNullOrEmpty(url) || !url.Contains("en.wikipedia.org/wiki/", StringComparison.OrdinalIgnoreCase)) return null;
        var slug = url[(url.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase) + 6)..];
        return Uri.UnescapeDataString(slug).Replace('_', ' ');
    }

    /// <summary>The lookup with relations + genres; null when MusicBrainz shed every attempt.</summary>
    private async Task<ArtistInfo?> LookupMusicBrainzAsync(string mbid, string artistName, CancellationToken ct)
    {
        try
        {
            var lookupJson = await GetMusicBrainzAsync($"{MusicBrainzBase}{mbid}?inc=url-rels+genres+tags&fmt=json", MusicBrainzLookupAttempts, ct);
            if (lookupJson == null) return null;
            using var lookupDoc = JsonDocument.Parse(lookupJson);
            return ParseLookup(lookupDoc.RootElement, artistName);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>Wikidata's item for a MusicBrainz artist id (P434), or null.</summary>
    private async Task<string?> FindWikidataByMusicBrainzIdAsync(string mbid, CancellationToken ct)
    {
        var json = await GetTextAsync(
            $"{WikidataApi}?action=query&list=search&srsearch={Uri.EscapeDataString("haswbstatement:P434=" + mbid)}&srlimit=1&format=json", ct);
        if (json == null) return null;
        using var doc = JsonDocument.Parse(json);
        return ParseWikidataSearch(doc.RootElement);
    }

    /// <summary>Wikidata name search → the first exact-label item with a MusicBrainz
    /// artist id (P434). Returns (mbid, qid), or (null, null).</summary>
    private async Task<(string? Mbid, string? Qid)> FindViaWikidataAsync(string artistName, CancellationToken ct)
    {
        var searchJson = await GetTextAsync(
            $"{WikidataApi}?action=wbsearchentities&search={Uri.EscapeDataString(artistName)}&language=en&type=item&limit=7&format=json", ct);
        if (searchJson == null) return (null, null);
        List<string> candidates;
        using (var doc = JsonDocument.Parse(searchJson))
            candidates = ParseWikidataCandidates(doc.RootElement, artistName);
        if (candidates.Count == 0) return (null, null);

        var entitiesJson = await GetTextAsync(
            $"{WikidataApi}?action=wbgetentities&ids={string.Join("|", candidates)}&props=claims&format=json", ct);
        if (entitiesJson == null) return (null, null);
        using var entities = JsonDocument.Parse(entitiesJson);
        foreach (var qid in candidates)
        {
            var mbid = ParseMusicBrainzClaim(entities.RootElement, qid);
            if (mbid != null) return (mbid, qid);
        }
        return (null, null);
    }

    private async Task<string?> GetMusicBrainzAsync(string url, int attempts, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            // ≤ 1 request/second, measured from the previous call's start.
            var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastMusicBrainzCall);
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastMusicBrainzCall = DateTime.UtcNow;
            try
            {
                return await GetTextAsync(url, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable && attempt < attempts)
            {
                await Task.Delay(MusicBrainzRetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // 404 is an answer ("no such article"); anything else failing (MusicBrainz's 503s,
        // a proxy hiccup) throws so GetAsync keeps the stale hit and caches NO miss —
        // a transient outage used to blank the About card for three days.
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await HttpSafety.ReadStringBoundedAsync(resp.Content, HttpSafety.MaxTextBytes, ct).ConfigureAwait(false);
    }

    // ── Parsers (pure, internal for tests) ──

    /// <summary>The MusicBrainz id of the best search hit: an exact (case-insensitive)
    /// name match scoring ≥ 80, else the top hit when it scores ≥ 95. Anything looser
    /// risks showing another artist's biography, which is worse than showing none.</summary>
    internal static string? PickBestMatch(JsonElement searchRoot, string artistName)
    {
        if (!searchRoot.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array)
            return null;
        string? first = null; var firstScore = 0;
        foreach (var a in artists.EnumerateArray())
        {
            var id = a.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (id == null) continue;
            var score = a.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            var name = a.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (first == null) { first = id; firstScore = score; }
            if (score >= 80 && string.Equals(name?.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase))
                return id;
            if (score >= 80 && a.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array
                && aliases.EnumerateArray().Any(al => al.TryGetProperty("name", out var an)
                    && string.Equals(an.GetString()?.Trim(), artistName.Trim(), StringComparison.OrdinalIgnoreCase)))
                return id;
        }
        return firstScore >= 95 ? first : null;
    }

    internal static ArtistInfo ParseLookup(JsonElement a, string requestedName)
    {
        var info = new ArtistInfo { Name = requestedName };
        // MusicBrainz emits explicit nulls ("begin-area": null for Chase Atlantic); a
        // property read on a Null element throws, so every nested read goes through here.
        static string Str(JsonElement e, string prop)
            => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? ""
                : "";

        info.MusicBrainzId = Str(a, "id");
        var name = Str(a, "name");
        if (name.Length > 0) info.Name = name;
        info.Type = Str(a, "type");
        info.Gender = Str(a, "gender");
        info.Country = Str(a, "country");
        info.Disambiguation = Str(a, "disambiguation");
        if (a.TryGetProperty("area", out var area)) info.Area = Str(area, "name");
        if (a.TryGetProperty("begin-area", out var bArea)) info.BeginArea = Str(bArea, "name");
        if (a.TryGetProperty("life-span", out var ls) && ls.ValueKind == JsonValueKind.Object)
        {
            info.Begin = Str(ls, "begin");
            info.End = Str(ls, "end");
            info.Ended = ls.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True;
        }

        // Genres: MusicBrainz's curated list first (voted counts), tags as a fallback.
        var genres = new List<(string Name, int Count)>();
        foreach (var prop in new[] { "genres", "tags" })
        {
            if (genres.Count > 0) break;
            if (!a.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) continue;
            foreach (var g in arr.EnumerateArray())
            {
                if (g.ValueKind != JsonValueKind.Object) continue;
                var gn = Str(g, "name");
                var count = g.TryGetProperty("count", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
                if (gn.Length > 0 && count > 0) genres.Add((gn, count));
            }
        }
        info.Genres = genres.OrderByDescending(g => g.Count).ThenBy(g => g.Name).Take(4).Select(g => g.Name).ToList();

        if (a.TryGetProperty("relations", out var rels) && rels.ValueKind == JsonValueKind.Array)
        {
            foreach (var r in rels.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var type = Str(r, "type");
                var url = r.TryGetProperty("url", out var u) ? Str(u, "resource") : "";
                if (url.Length == 0) continue;
                // Relations MusicBrainz marks ended (a dead account) are not links to offer.
                if (r.TryGetProperty("ended", out var ended) && ended.ValueKind == JsonValueKind.True) continue;
                switch (type)
                {
                    case "wikidata":
                        info.WikidataId = url[(url.LastIndexOf('/') + 1)..];
                        break;
                    case "wikipedia":
                        if (string.IsNullOrEmpty(info.WikipediaUrl)) info.WikipediaUrl = url;
                        break;
                    case "official homepage":
                        if (string.IsNullOrEmpty(info.WebsiteUrl)) info.WebsiteUrl = url;
                        break;
                }
                var kind = ArtistLink.ClassifyUrl(type, url);
                if (kind != null && !info.Links.Any(l => l.Kind == kind))
                    info.Links.Add(new ArtistLink { Kind = kind, Url = url });
            }
        }
        info.Links = info.Links.OrderBy(l => l.Order).ToList();
        if (info.MusicBrainzId.Length > 0)
            info.MusicBrainzUrl = "https://musicbrainz.org/artist/" + info.MusicBrainzId;
        return info;
    }

    /// <summary>Item ids from a <c>wbsearchentities</c> result whose label (or an alias the
    /// search matched on) equals the name, case-insensitively — "Chase Atlantic" must not
    /// resolve to "Chase Atlantic discography".</summary>
    internal static List<string> ParseWikidataCandidates(JsonElement root, string artistName)
    {
        var ids = new List<string>();
        if (!root.TryGetProperty("search", out var search) || search.ValueKind != JsonValueKind.Array) return ids;
        var wanted = artistName.Trim();
        foreach (var hit in search.EnumerateArray())
        {
            var id = hit.TryGetProperty("id", out var i) ? i.GetString() : null;
            if (id is not { Length: > 1 } || id[0] != 'Q') continue;
            var label = hit.TryGetProperty("label", out var l) ? l.GetString()?.Trim() : null;
            var matched = hit.TryGetProperty("match", out var m) && m.TryGetProperty("text", out var mt) ? mt.GetString()?.Trim() : null;
            if (string.Equals(label, wanted, StringComparison.OrdinalIgnoreCase)
                || string.Equals(matched, wanted, StringComparison.OrdinalIgnoreCase))
                ids.Add(id);
        }
        return ids;
    }

    /// <summary>The MusicBrainz artist id (property P434) claimed by <paramref name="qid"/>
    /// in a <c>wbgetentities</c> result, null when the item is not a MusicBrainz artist.</summary>
    internal static string? ParseMusicBrainzClaim(JsonElement root, string qid)
    {
        if (!root.TryGetProperty("entities", out var entities) || !entities.TryGetProperty(qid, out var entity)) return null;
        if (!entity.TryGetProperty("claims", out var claims) || !claims.TryGetProperty("P434", out var p434)
            || p434.ValueKind != JsonValueKind.Array) return null;
        foreach (var claim in p434.EnumerateArray())
        {
            if (claim.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var dv)
                && dv.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
            {
                var mbid = v.GetString();
                if (Guid.TryParse(mbid, out _)) return mbid;
            }
        }
        return null;
    }

    /// <summary>FROM / BORN / GENRE straight from a Wikidata item's claims (used when
    /// MusicBrainz was shed): P19 birthplace or P740 place of formation, P569 birth date or
    /// P571 inception, P136 genres, P27 citizenship or P495 country of origin, P31 = Q5 for
    /// a person. Item-valued claims come back as ids to label in a second call.</summary>
    internal static WikidataFacts? ParseWikidataFacts(JsonElement root, string qid)
    {
        if (!root.TryGetProperty("entities", out var entities) || !entities.TryGetProperty(qid, out var entity)
            || !entity.TryGetProperty("claims", out var claims) || claims.ValueKind != JsonValueKind.Object) return null;

        var facts = new WikidataFacts();
        facts.IsPerson = ClaimItems(claims, "P31").Contains("Q5");
        facts.PlaceId = ClaimItems(claims, facts.IsPerson ? "P19" : "P740").FirstOrDefault()
                        ?? ClaimItems(claims, facts.IsPerson ? "P740" : "P19").FirstOrDefault();
        facts.CountryId = ClaimItems(claims, facts.IsPerson ? "P27" : "P495").FirstOrDefault()
                          ?? ClaimItems(claims, facts.IsPerson ? "P495" : "P27").FirstOrDefault();
        facts.GenreIds = ClaimItems(claims, "P136").Take(4).ToList();
        facts.Begin = ClaimTime(claims, facts.IsPerson ? "P569" : "P571") ?? ClaimTime(claims, facts.IsPerson ? "P571" : "P569") ?? "";
        return facts;
    }

    private static IEnumerable<string> ClaimItems(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) yield break;
        foreach (var claim in arr.EnumerateArray())
        {
            if (claim.TryGetProperty("mainsnak", out var snak) && snak.TryGetProperty("datavalue", out var dv)
                && dv.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.Object
                && v.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                yield return id.GetString()!;
        }
    }

    /// <summary>"+1994-03-10T00:00:00Z" at precision 11/10/9 → "1994-03-10" / "1994-03" / "1994".</summary>
    private static string? ClaimTime(JsonElement claims, string property)
    {
        if (!claims.TryGetProperty(property, out var arr) || arr.ValueKind != JsonValueKind.Array) return null;
        foreach (var claim in arr.EnumerateArray())
        {
            if (!claim.TryGetProperty("mainsnak", out var snak) || !snak.TryGetProperty("datavalue", out var dv)
                || !dv.TryGetProperty("value", out var v) || v.ValueKind != JsonValueKind.Object
                || !v.TryGetProperty("time", out var t) || t.ValueKind != JsonValueKind.String) continue;
            var time = t.GetString() ?? "";
            var precision = v.TryGetProperty("precision", out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 11;
            if (time.Length < 11 || time[0] != '+') continue;
            var iso = time[1..11]; // yyyy-MM-dd
            return precision switch { >= 11 => iso, 10 => iso[..7], _ => iso[..4] };
        }
        return null;
    }

    internal static Dictionary<string, string> ParseWikidataLabels(JsonElement root)
    {
        var labels = new Dictionary<string, string>();
        if (!root.TryGetProperty("entities", out var entities) || entities.ValueKind != JsonValueKind.Object) return labels;
        foreach (var entity in entities.EnumerateObject())
        {
            if (entity.Value.TryGetProperty("labels", out var l) && l.TryGetProperty("en", out var en)
                && en.TryGetProperty("value", out var v) && v.ValueKind == JsonValueKind.String)
                labels[entity.Name] = v.GetString() ?? "";
        }
        return labels;
    }

    /// <summary>Fills only what the MusicBrainz lookup would have: the card reads the same
    /// whichever source answered.</summary>
    internal static void ApplyWikidataFacts(ArtistInfo info, WikidataFacts facts, IReadOnlyDictionary<string, string> labels)
    {
        static string Label(IReadOnlyDictionary<string, string> labels, string? id)
            => id != null && labels.TryGetValue(id, out var v) ? v : "";
        if (info.Type.Length == 0) info.Type = facts.IsPerson ? "Person" : "Group";
        if (info.BeginArea.Length == 0) info.BeginArea = Label(labels, facts.PlaceId);
        // Area, not Country: Country is an ISO code and Area the display name it falls back to.
        if (info.Area.Length == 0 && info.Country.Length == 0) info.Area = Label(labels, facts.CountryId);
        if (info.Begin.Length == 0) info.Begin = facts.Begin;
        if (info.Genres.Count == 0)
            info.Genres = facts.GenreIds.Select(id => Label(labels, id)).Where(g => g.Length > 0).ToList();
    }

    /// <summary>The item id ("Q46537070") from a Wikidata <c>list=search</c> result, null when
    /// nothing carries the statement.</summary>
    internal static string? ParseWikidataSearch(JsonElement root)
    {
        if (!root.TryGetProperty("query", out var query) || !query.TryGetProperty("search", out var search)
            || search.ValueKind != JsonValueKind.Array) return null;
        foreach (var hit in search.EnumerateArray())
        {
            var title = hit.TryGetProperty("title", out var t) ? t.GetString() : null;
            if (title is { Length: > 1 } && title[0] == 'Q') return title;
        }
        return null;
    }

    internal static string? ParseWikidataTitle(JsonElement root, string qid)
    {
        if (!root.TryGetProperty("entities", out var entities) || !entities.TryGetProperty(qid, out var entity)) return null;
        if (!entity.TryGetProperty("sitelinks", out var links) || !links.TryGetProperty("enwiki", out var enwiki)) return null;
        return enwiki.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    internal static void ApplyWikipediaSummary(JsonElement root, ArtistInfo info)
    {
        // Only a real article: disambiguation pages carry type "disambiguation".
        if (root.TryGetProperty("type", out var type) && type.GetString() != "standard") return;
        if (root.TryGetProperty("extract", out var extract) && extract.ValueKind == JsonValueKind.String)
        {
            var text = extract.GetString()?.Trim() ?? "";
            if (text.Length > 0) { info.Bio = text; info.BioSource = "Wikipedia"; }
        }
        if (root.TryGetProperty("description", out var desc) && desc.ValueKind == JsonValueKind.String)
            info.ShortDescription = desc.GetString() ?? "";
        if (root.TryGetProperty("content_urls", out var urls) && urls.TryGetProperty("desktop", out var desktop)
            && desktop.TryGetProperty("page", out var page) && page.ValueKind == JsonValueKind.String)
            info.WikipediaUrl = page.GetString() ?? info.WikipediaUrl;
    }
}

/// <summary>Cached artist facts (round-trips through System.Text.Json).</summary>
public sealed class ArtistInfo
{
    public string Name { get; set; } = "";
    public string MusicBrainzId { get; set; } = "";
    public string MusicBrainzUrl { get; set; } = "";
    /// <summary>MusicBrainz artist type: Person, Group, Orchestra, Choir, Character, Other.</summary>
    public string Type { get; set; } = "";
    public string Gender { get; set; } = "";
    /// <summary>ISO 3166-1 alpha-2.</summary>
    public string Country { get; set; } = "";
    public string Area { get; set; } = "";
    public string BeginArea { get; set; } = "";
    /// <summary>Partial ISO date: "1994-03-10", "1994-03" or "1994".</summary>
    public string Begin { get; set; } = "";
    public string End { get; set; } = "";
    public bool Ended { get; set; }
    public string Disambiguation { get; set; } = "";
    public List<string> Genres { get; set; } = new();
    public string Bio { get; set; } = "";
    public string BioSource { get; set; } = "";
    /// <summary>Wikipedia's one-line description ("Puerto Rican rapper and singer").</summary>
    public string ShortDescription { get; set; } = "";
    public string WikidataId { get; set; } = "";
    public string WikipediaUrl { get; set; } = "";
    public string WebsiteUrl { get; set; } = "";
    /// <summary>Streaming / social / homepage links for the About card's icon row, in
    /// display order (Spotify, Apple Music, YouTube, SoundCloud, Bandcamp, Instagram, X, site).</summary>
    public List<ArtistLink> Links { get; set; } = new();
    public DateTime FetchedAtUtc { get; set; }
    public bool Found { get; set; }
    /// <summary>The MusicBrainz lookup was shed: identity and bio only, no facts/links.
    /// Refreshed on the short (miss) clock.</summary>
    public bool Partial { get; set; }
    /// <summary>Cache shape version; see <see cref="ArtistInfoService.CurrentSchema"/>.</summary>
    public int Schema { get; set; }

    // ── Display helpers ──

    [JsonIgnore] public bool IsGroup => Type is "Group" or "Orchestra" or "Choir";
    [JsonIgnore] public bool HasBio => Bio.Length > 0;
    [JsonIgnore] public bool HasGenres => Genres.Count > 0;
    [JsonIgnore] public bool HasLinks => Links.Count > 0;
    /// <summary>"Alternative · R&amp;B · Pop" for the About card's tag line.</summary>
    [JsonIgnore] public string GenresDotted => string.Join(" · ", Genres.Select(Capitalize));
    /// <summary>Just the year of the begin date ("2011"), for the calendar fact.</summary>
    [JsonIgnore] public string BeginYear => Begin.Length >= 4 ? Begin[..4] : "";
    [JsonIgnore] public bool HasWebsite => WebsiteUrl.Length > 0;
    [JsonIgnore] public bool HasWikipedia => WikipediaUrl.Length > 0;
    [JsonIgnore] public bool HasFrom => FromDisplay.Length > 0;
    [JsonIgnore] public bool HasBegin => Begin.Length > 0;

    /// <summary>"Vega Baja, Puerto Rico" — the birthplace/founding place, then the country
    /// when it adds something.</summary>
    [JsonIgnore]
    public string FromDisplay
    {
        get
        {
            var place = BeginArea.Length > 0 ? BeginArea : Area;
            var country = CountryName;
            if (place.Length == 0) return country;
            if (country.Length == 0 || place.Contains(country, StringComparison.OrdinalIgnoreCase)) return place;
            return $"{place}, {country}";
        }
    }

    [JsonIgnore]
    public string CountryName
    {
        get
        {
            if (Country.Length != 2) return Area;
            try { return new RegionInfo(Country).EnglishName; }
            catch { return Area.Length > 0 ? Area : Country; }
        }
    }

    [JsonIgnore] public string BeginLabel => IsGroup ? "FORMED" : "BORN";
    [JsonIgnore] public string BeginDisplay => FormatPartialDate(Begin);
    [JsonIgnore] public string EndLabel => IsGroup ? "DISBANDED" : "DIED";
    [JsonIgnore] public bool HasEnd => Ended && End.Length > 0;
    [JsonIgnore] public string EndDisplay => FormatPartialDate(End);

    /// <summary>"Active since 2016" / "Active 1962 – 1970".</summary>
    [JsonIgnore]
    public string ActiveDisplay
    {
        get
        {
            var start = Begin.Length >= 4 ? Begin[..4] : "";
            if (start.Length == 0) return "";
            if (Ended) return End.Length >= 4 ? $"{start} – {End[..4]}" : $"{start} – (ended)";
            return $"{start} – present";
        }
    }
    [JsonIgnore] public bool HasActive => ActiveDisplay.Length > 0;

    [JsonIgnore]
    public string TypeDisplay => Type switch
    {
        "Person" => Gender.Length > 0 ? $"Solo artist · {Capitalize(Gender)}" : "Solo artist",
        "Group" => "Band / group",
        "Orchestra" => "Orchestra",
        "Choir" => "Choir",
        "Character" => "Character",
        _ => "",
    };
    [JsonIgnore] public bool HasType => TypeDisplay.Length > 0;

    [JsonIgnore] public string GenresDisplay => string.Join(", ", Genres.Select(Capitalize));

    internal static string Capitalize(string s)
        => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>"March 10, 1994" for a full date, "March 1994" for a month, "1994" for a year.</summary>
    internal static string FormatPartialDate(string iso)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        var parts = iso.Split('-');
        try
        {
            if (parts.Length >= 3 && int.TryParse(parts[0], out var y) && int.TryParse(parts[1], out var m) && int.TryParse(parts[2], out var d))
                return new DateTime(y, m, d).ToString("MMMM d, yyyy", CultureInfo.InvariantCulture);
            if (parts.Length == 2 && int.TryParse(parts[0], out y) && int.TryParse(parts[1], out m))
                return new DateTime(y, m, 1).ToString("MMMM yyyy", CultureInfo.InvariantCulture);
        }
        catch { }
        return parts[0];
    }
}

/// <summary>What a Wikidata item says about an artist, before its item ids are labelled.</summary>
public sealed class WikidataFacts
{
    public bool IsPerson { get; set; }
    public string? PlaceId { get; set; }
    public string? CountryId { get; set; }
    public List<string> GenreIds { get; set; } = new();
    /// <summary>Partial ISO date, like <see cref="ArtistInfo.Begin"/>.</summary>
    public string Begin { get; set; } = "";
    /// <summary>Every item id that needs an English label.</summary>
    public List<string> ItemIds
        => new[] { PlaceId, CountryId }.Concat(GenreIds).Where(id => id != null).Distinct().Select(id => id!).ToList();
}

/// <summary>One link on the About card: a known service (drawn with its brand glyph) or
/// the official site. Classified from the URL host rather than MusicBrainz's relation
/// type, which lumps Spotify, Apple Music, Deezer and Tidal together as "streaming".</summary>
public sealed class ArtistLink
{
    public string Kind { get; set; } = "";
    public string Url { get; set; } = "";

    private static readonly string[] KindOrder =
        { "spotify", "applemusic", "youtube", "soundcloud", "bandcamp", "instagram", "x", "website" };

    [JsonIgnore] public int Order => Math.Max(0, Array.IndexOf(KindOrder, Kind));

    [JsonIgnore]
    public string IconKey => Kind switch
    {
        "spotify" => "SpotifyIcon",
        "applemusic" => "AppleMusicIcon",
        "youtube" => "YouTubeIcon",
        "soundcloud" => "SoundCloudIcon",
        "bandcamp" => "BandcampIcon",
        "instagram" => "InstagramIcon",
        "x" => "XIcon",
        _ => "GlobeIcon",
    };

    [JsonIgnore]
    public string Label => Kind switch
    {
        "spotify" => "Spotify",
        "applemusic" => "Apple Music",
        "youtube" => "YouTube",
        "soundcloud" => "SoundCloud",
        "bandcamp" => "Bandcamp",
        "instagram" => "Instagram",
        "x" => "X",
        _ => "Website",
    };

    /// <summary>The kind for a MusicBrainz url relation, or null when it is not one the
    /// card shows (Facebook, Discogs, Deezer, lyrics sites …).</summary>
    internal static string? ClassifyUrl(string relationType, string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return null;
        var host = uri.Host.ToLowerInvariant();
        if (host.StartsWith("www.")) host = host[4..];
        if (host is "open.spotify.com" or "spotify.com" || host.EndsWith(".spotify.com")) return "spotify";
        if (host is "music.apple.com" or "itunes.apple.com") return "applemusic";
        if (host is "youtube.com" or "music.youtube.com" or "youtu.be" || host.EndsWith(".youtube.com")) return "youtube";
        if (host == "soundcloud.com") return "soundcloud";
        if (host == "bandcamp.com" || host.EndsWith(".bandcamp.com")) return "bandcamp";
        if (host == "instagram.com") return "instagram";
        if (host is "x.com" or "twitter.com") return "x";
        if (relationType == "official homepage") return "website";
        return null;
    }
}
