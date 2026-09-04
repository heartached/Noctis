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
/// Results are cached per artist id under <c>artist_info/</c> — 30 days for a hit,
/// 3 days for a miss — and every lookup is paced to MusicBrainz's 1 request/second rule.
/// </summary>
public sealed class ArtistInfoService
{
    private const string MusicBrainzBase = "https://musicbrainz.org/ws/2/artist/";
    private const string WikidataApi = "https://www.wikidata.org/w/api.php";
    private const string WikipediaSummary = "https://en.wikipedia.org/api/rest_v1/page/summary/";
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(3);

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
            var info = await FetchAsync(artistName, ct).ConfigureAwait(false)
                       ?? new ArtistInfo { Name = artistName, Found = false };
            info.FetchedAtUtc = DateTime.UtcNow;
            WriteCache(path, info);
            return info.Found ? info : null;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Offline or a source hiccup: keep serving a stale hit rather than nothing.
            return cached is { Found: true } ? cached : null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool IsStale(ArtistInfo info)
        => DateTime.UtcNow - info.FetchedAtUtc > (info.Found ? HitTtl : MissTtl);

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

    private async Task<ArtistInfo?> FetchAsync(string artistName, CancellationToken ct)
    {
        // 1. Search MusicBrainz for the id.
        var searchJson = await GetMusicBrainzAsync(
            $"{MusicBrainzBase}?query={Uri.EscapeDataString("artist:\"" + artistName.Replace("\"", "") + "\"")}&fmt=json&limit=5", ct);
        if (searchJson == null) return null;
        string? mbid;
        using (var searchDoc = JsonDocument.Parse(searchJson))
            mbid = PickBestMatch(searchDoc.RootElement, artistName);
        if (mbid == null) return null;

        // 2. Lookup with relations + genres.
        var lookupJson = await GetMusicBrainzAsync($"{MusicBrainzBase}{mbid}?inc=url-rels+genres+tags&fmt=json", ct);
        if (lookupJson == null) return null;
        ArtistInfo info;
        using (var lookupDoc = JsonDocument.Parse(lookupJson))
            info = ParseLookup(lookupDoc.RootElement, artistName);

        // 3. Bio: Wikidata → English Wikipedia title → REST summary.
        var title = await ResolveWikipediaTitleAsync(info, ct);
        if (title != null)
        {
            var summaryJson = await GetTextAsync(WikipediaSummary + Uri.EscapeDataString(title.Replace(' ', '_')), ct);
            if (summaryJson != null)
            {
                using var summaryDoc = JsonDocument.Parse(summaryJson);
                ApplyWikipediaSummary(summaryDoc.RootElement, info);
            }
        }

        info.Found = true;
        return info;
    }

    private async Task<string?> ResolveWikipediaTitleAsync(ArtistInfo info, CancellationToken ct)
    {
        // Prefer the Wikidata item MusicBrainz links (authoritative identity).
        if (!string.IsNullOrEmpty(info.WikidataId))
        {
            var json = await GetTextAsync(
                $"{WikidataApi}?action=wbgetentities&ids={info.WikidataId}&props=sitelinks&sitefilter=enwiki&format=json", ct);
            if (json != null)
            {
                using var doc = JsonDocument.Parse(json);
                var title = ParseWikidataTitle(doc.RootElement, info.WikidataId);
                if (title != null) return title;
            }
        }
        // A direct English Wikipedia relation is the next best thing.
        if (!string.IsNullOrEmpty(info.WikipediaUrl) && info.WikipediaUrl.Contains("en.wikipedia.org/wiki/", StringComparison.OrdinalIgnoreCase))
        {
            var slug = info.WikipediaUrl[(info.WikipediaUrl.IndexOf("/wiki/", StringComparison.OrdinalIgnoreCase) + 6)..];
            return Uri.UnescapeDataString(slug).Replace('_', ' ');
        }
        return null;
    }

    private async Task<string?> GetMusicBrainzAsync(string url, CancellationToken ct)
    {
        // ≤ 1 request/second, measured from the previous call's start.
        var wait = TimeSpan.FromMilliseconds(1100) - (DateTime.UtcNow - _lastMusicBrainzCall);
        if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
        _lastMusicBrainzCall = DateTime.UtcNow;
        return await GetTextAsync(url, ct).ConfigureAwait(false);
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.UserAgent.Clear();
        req.Headers.UserAgent.ParseAdd(UserAgent);
        req.Headers.Accept.ParseAdd("application/json");
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
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
        static string Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        info.MusicBrainzId = Str(a, "id");
        var name = Str(a, "name");
        if (name.Length > 0) info.Name = name;
        info.Type = Str(a, "type");
        info.Gender = Str(a, "gender");
        info.Country = Str(a, "country");
        info.Disambiguation = Str(a, "disambiguation");
        if (a.TryGetProperty("area", out var area)) info.Area = Str(area, "name");
        if (a.TryGetProperty("begin-area", out var bArea)) info.BeginArea = Str(bArea, "name");
        if (a.TryGetProperty("life-span", out var ls))
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
                var type = Str(r, "type");
                var url = r.TryGetProperty("url", out var u) ? Str(u, "resource") : "";
                if (url.Length == 0) continue;
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
            }
        }
        if (info.MusicBrainzId.Length > 0)
            info.MusicBrainzUrl = "https://musicbrainz.org/artist/" + info.MusicBrainzId;
        return info;
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
    public DateTime FetchedAtUtc { get; set; }
    public bool Found { get; set; }

    // ── Display helpers ──

    [JsonIgnore] public bool IsGroup => Type is "Group" or "Orchestra" or "Choir";
    [JsonIgnore] public bool HasBio => Bio.Length > 0;
    [JsonIgnore] public bool HasGenres => Genres.Count > 0;
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

    private static string Capitalize(string s)
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
