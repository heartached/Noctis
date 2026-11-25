using System.Net;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.Services;

/// <summary>Which of a Discogs release's two lists becomes the album's genre tag (GitHub #267).</summary>
public enum DiscogsGenreSource
{
    /// <summary>The "Style" list (House, Synth-pop, …); the release's genres when it has no styles.</summary>
    Styles = 0,
    /// <summary>The broad "Genre" list (Electronic, Rock, …).</summary>
    Genres = 1,
    /// <summary>Styles first, then genres.</summary>
    StylesAndGenres = 2,
}

/// <summary>GitHub #267: how found Discogs genres turn into the album's genre tag.</summary>
/// <param name="MaxGenres">At most this many genres from Discogs; 0 = all of them.</param>
/// <param name="UseRelease">Look up the closest release version instead of the master release.</param>
/// <param name="AddToExisting">Keep the album's current genres and add the new ones after them.</param>
public sealed record DiscogsGenreOptions(
    DiscogsGenreSource Source = DiscogsGenreSource.Styles,
    int MaxGenres = 0,
    bool UseRelease = false,
    bool AddToExisting = false);

/// <summary>What one album is searched as: its album artist, title and (for ranking only) year.</summary>
public sealed record DiscogsAlbumQuery(string Artist, string Title, int Year = 0);

/// <summary>One Discogs search result (a master or a release).</summary>
public sealed record DiscogsResult(
    long Id, string Kind, string Artist, string Title, int Year,
    IReadOnlyList<string> Genres, IReadOnlyList<string> Styles, string? Uri);

/// <summary>The Discogs master / release an album was matched to.</summary>
public sealed record DiscogsAlbumMatch(
    long Id, string Kind, string Artist, string Title, int Year,
    IReadOnlyList<string> Genres, IReadOnlyList<string> Styles, string Url);

/// <summary>Discogs kept answering "too many requests"; the rest of the run is left alone.</summary>
public sealed class DiscogsBusyException : Exception
{
    public DiscogsBusyException() : base("Discogs is busy") { }
}

/// <summary>
/// GitHub #267 "Discogs Genre Support": the styles (or genres) of an album's Discogs master
/// release, for tagging many albums' genres at once. One search request per album (Discogs'
/// search results carry both lists), unauthenticated, paced under Discogs' 25 requests a minute
/// (<see cref="PoliteGate"/>; 429 backs off and retries, and a used-up rate-limit window is
/// waited out). Only a clear match counts: the title has to be the album's (edition words like
/// "(Deluxe Edition)" ignored on our side) and the artist the album artist; anything weaker is
/// "not found" rather than another record's genres. Nothing is written here except by
/// <see cref="ApplyAsync"/>, after the user reviewed the finds.
/// </summary>
public sealed class DiscogsGenreService
{
    public const string LogSource = "Discogs";
    private const string Api = "https://api.discogs.com/database/search";
    private const string Site = "https://www.discogs.com";

    /// <summary>Lowest title / artist similarity a result needs (see <see cref="AnimatedArtworkMatcher"/>).</summary>
    internal const double MinTitle = AnimatedArtworkMatcher.SameTitle;
    internal const double MinArtist = AnimatedArtworkMatcher.SameArtist;

    private readonly HttpClient _http;
    private readonly PoliteGate _gate;
    private readonly Func<DateTime> _now;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly LruCache<string, IReadOnlyList<DiscogsResult>> _cache = new(500, TimeSpan.FromHours(6), StringComparer.Ordinal);
    private DateTime _windowResetsAt = DateTime.MinValue;
    private readonly object _lock = new();

    /// <summary>Discogs allows 25 unauthenticated requests a minute: one every 2.5 s stays under it.</summary>
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromSeconds(2.5);

    /// <summary>How long a used-up rate-limit window is waited out (Discogs' window is a moving minute).</summary>
    internal static readonly TimeSpan WindowWait = TimeSpan.FromSeconds(60);

    public DiscogsGenreService(HttpClient http, TimeSpan? interval = null,
        Func<DateTime>? clock = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _http = http;
        _now = clock ?? (() => DateTime.UtcNow);
        _delay = delay ?? Task.Delay;
        var spacing = interval ?? DefaultInterval;
        _gate = new PoliteGate(spacing, clock, delay)
        {
            MaxInterval = spacing > TimeSpan.FromSeconds(6) ? spacing : TimeSpan.FromSeconds(6),
        };
    }

    /// <summary>A new run: a source left alone by the last one is asked again.</summary>
    public void BeginRun() => _gate.Reset();

    /// <summary>The best clear Discogs match for the album, or null when there is none. Masters
    /// first (unless <paramref name="useRelease"/>), then releases: not every release has a master.
    /// Throws <see cref="DiscogsBusyException"/> or <see cref="HttpRequestException"/> on failure.</summary>
    public async Task<DiscogsAlbumMatch?> FindAsync(DiscogsAlbumQuery query, bool useRelease, CancellationToken ct)
    {
        var title = SearchTitle(query.Title);
        var artist = SearchArtist(query.Artist);
        if (title.Length == 0 || artist.Length == 0) return null;
        var kinds = useRelease ? new[] { "release" } : new[] { "master", "release" };
        foreach (var kind in kinds)
        {
            var results = await SearchAsync(artist, title, kind, ct).ConfigureAwait(false);
            if (PickBest(results, query, kind) is { } best) return best;
        }
        return null;
    }

    private async Task<IReadOnlyList<DiscogsResult>> SearchAsync(string artist, string title, string kind, CancellationToken ct)
    {
        var url = $"{Api}?artist={Uri.EscapeDataString(artist)}&release_title={Uri.EscapeDataString(title)}&type={kind}&per_page=25";
        if (_cache.TryGet(url, out var cached)) return cached;
        var json = await GetAsync(url, ct).ConfigureAwait(false);
        var results = json is null ? Array.Empty<DiscogsResult>() : ParseSearch(json, kind);
        _cache.Set(url, results);
        return results;
    }

    private async Task<string?> GetAsync(string url, CancellationToken ct)
    {
        while (true)
        {
            if (_gate.GaveUp) throw new DiscogsBusyException();
            TimeSpan windowWait;
            lock (_lock) windowWait = _windowResetsAt - _now();
            if (windowWait > TimeSpan.Zero) await _delay(windowWait, ct).ConfigureAwait(false);
            await _gate.WaitTurnAsync(ct).ConfigureAwait(false);

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            // Discogs requires a descriptive User-Agent.
            req.Headers.UserAgent.ParseAdd(ProviderHttp.UserAgent);
            req.Headers.Accept.ParseAdd("application/json");
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            NoteRateLimit(resp);

            if (resp.StatusCode == HttpStatusCode.TooManyRequests || resp.StatusCode == HttpStatusCode.ServiceUnavailable)
            {
                var retryAfter = resp.Headers.RetryAfter?.Delta
                                 ?? (resp.Headers.RetryAfter?.Date is { } at ? at - DateTimeOffset.UtcNow : null);
                DebugLog.Write(LogSource, $"busy (HTTP {(int)resp.StatusCode}), backing off");
                _gate.ReportBusy(retryAfter);
                continue;
            }
            _gate.ReportOk();
            if (resp.StatusCode == HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
            return await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
        }
    }

    /// <summary>x-discogs-ratelimit-remaining 0: wait the window out before the next request, so
    /// the run doesn't walk into a 429 (other apps on the same network count too).</summary>
    private void NoteRateLimit(HttpResponseMessage resp)
    {
        if (!resp.Headers.TryGetValues("x-discogs-ratelimit-remaining", out var values)) return;
        if (!int.TryParse(values.FirstOrDefault(), out var remaining) || remaining > 0) return;
        lock (_lock) _windowResetsAt = _now() + WindowWait;
    }

    // ── Parsing and matching ──

    /// <summary>The results of a /database/search answer.</summary>
    internal static IReadOnlyList<DiscogsResult> ParseSearch(string json, string kind)
    {
        var list = new List<DiscogsResult>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                return list;
            foreach (var r in results.EnumerateArray())
            {
                if (r.ValueKind != JsonValueKind.Object) continue;
                var id = r.TryGetProperty("id", out var idEl) && idEl.TryGetInt64(out var n) ? n : 0;
                var full = Str(r, "title");
                if (id <= 0 || full.Length == 0) continue;
                var (artist, title) = SplitTitle(full);
                var resultKind = Str(r, "type") is { Length: > 0 } t ? t : kind;
                list.Add(new DiscogsResult(id, resultKind, artist, title, Year(r), Strings(r, "genre"), Strings(r, "style"),
                    Str(r, "uri") is { Length: > 0 } uri ? uri : null));
            }
        }
        catch (JsonException ex)
        {
            DebugLog.Write(LogSource, $"unreadable search answer: {ex.Message}");
        }
        return list;
    }

    private static string Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() ?? string.Empty : string.Empty;

    // "year" is a string in search results ("1993") and a number on /masters and /releases.
    private static int Year(JsonElement e)
    {
        if (!e.TryGetProperty("year", out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s) ? s : 0;
    }

    private static IReadOnlyList<string> Strings(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return Array.Empty<string>();
        return v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString()?.Trim() ?? string.Empty)
            .Where(x => x.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>"Björk - Debut" → ("Björk", "Debut"). Discogs joins the credit and the title with " - ".</summary>
    internal static (string Artist, string Title) SplitTitle(string full)
    {
        var i = full.IndexOf(" - ", StringComparison.Ordinal);
        return i < 0 ? (string.Empty, full.Trim()) : (CleanArtist(full[..i]), full[(i + 3)..].Trim());
    }

    /// <summary>Discogs' artist disambiguation and name-variation marks off: "Nirvana (2)" → "Nirvana", "Björk*" → "Björk".</summary>
    internal static string CleanArtist(string artist)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(artist, @"\s*\(\d+\)", string.Empty);
        return s.Replace("*", string.Empty).Trim();
    }

    /// <summary>The title searched for: edition words off ("Debut (Deluxe Edition)" → "Debut").</summary>
    internal static string SearchTitle(string? title) => AlbumTitleNormalizer.Normalize(title);

    /// <summary>The artist searched for: the primary credit; Discogs credits compilations to "Various".</summary>
    internal static string SearchArtist(string? artist)
    {
        if (string.IsNullOrWhiteSpace(artist)) return string.Empty;
        if (IsVarious(artist)) return "Various";
        return Track.GetPrimaryArtist(artist).Trim();
    }

    private static bool IsVarious(string? artist)
        => artist is not null && (artist.Trim().Equals(Track.VariousArtists, StringComparison.OrdinalIgnoreCase)
                                  || artist.Trim().Equals("Various", StringComparison.OrdinalIgnoreCase));

    /// <summary>How well a result's title names the album, 0..1: edition words are ignored on both sides.</summary>
    internal static double TitleScore(string resultTitle, string albumTitle)
        => Math.Max(AnimatedArtworkMatcher.TitleSimilarity(resultTitle, albumTitle),
                    AnimatedArtworkMatcher.TitleSimilarity(SearchTitle(resultTitle), SearchTitle(albumTitle)) >= 1 ? 0.95 : 0);

    internal static double ArtistScore(string resultArtist, string albumArtist)
    {
        if (IsVarious(resultArtist) && IsVarious(albumArtist)) return 1;
        return AnimatedArtworkMatcher.ArtistSimilarity(resultArtist, albumArtist);
    }

    /// <summary>
    /// The clear match among <paramref name="results"/>, or null. Title and artist must both be
    /// close (<see cref="MinTitle"/>, <see cref="MinArtist"/>); among those the exact title wins,
    /// then the year nearest the album's, then Discogs' own order.
    /// </summary>
    internal static DiscogsAlbumMatch? PickBest(IReadOnlyList<DiscogsResult> results, DiscogsAlbumQuery query, string kind)
    {
        DiscogsResult? best = null;
        double bestScore = double.MinValue;
        for (var i = 0; i < results.Count; i++)
        {
            var r = results[i];
            var title = TitleScore(r.Title, query.Title);
            var artist = ArtistScore(r.Artist, query.Artist);
            if (title < MinTitle || artist < MinArtist) continue;
            if (r.Genres.Count == 0 && r.Styles.Count == 0) continue;
            var score = title * 100 + artist * 20 - i * 0.01;
            // A result without a year ranks as if 6 years off: behind a near one, ahead of a far one.
            if (query.Year > 0) score -= Math.Min(r.Year > 0 ? Math.Abs(r.Year - query.Year) : 6, 30) * 0.5;
            if (score > bestScore)
            {
                bestScore = score;
                best = r;
            }
        }
        if (best is null) return null;
        var url = best.Uri is { Length: > 0 } u
            ? (u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : Site + (u.StartsWith('/') ? u : "/" + u))
            : $"{Site}/{(string.Equals(best.Kind, "master", StringComparison.OrdinalIgnoreCase) ? "master" : "release")}/{best.Id}";
        return new DiscogsAlbumMatch(best.Id, best.Kind.Length > 0 ? best.Kind : kind, best.Artist, best.Title, best.Year,
            best.Genres, best.Styles, url);
    }

    // ── Options ──

    /// <summary>The Discogs genres the options take from a match, in order, at most MaxGenres.</summary>
    public static IReadOnlyList<string> PickGenres(DiscogsAlbumMatch match, DiscogsGenreOptions options)
    {
        IEnumerable<string> list = options.Source switch
        {
            DiscogsGenreSource.Genres => match.Genres,
            DiscogsGenreSource.StylesAndGenres => match.Styles.Concat(match.Genres),
            _ => match.Styles.Count > 0 ? match.Styles : match.Genres,
        };
        var picked = list.Select(g => g.Trim()).Where(g => g.Length > 0 && !g.Contains(';'))
            .Distinct(StringComparer.OrdinalIgnoreCase);
        if (options.MaxGenres > 0) picked = picked.Take(options.MaxGenres);
        return picked.ToList();
    }

    /// <summary>The genre value an album with <paramref name="current"/> gets: the picked Discogs
    /// genres, after the current ones when adding (case-insensitive duplicates dropped, the
    /// current spelling kept), joined as <see cref="Track.Genre"/> stores them.</summary>
    public static string ComposeGenre(string? current, DiscogsAlbumMatch match, DiscogsGenreOptions options)
    {
        var found = PickGenres(match, options);
        var values = options.AddToExisting ? Track.SplitStoredGenres(current).Concat(found) : found;
        return Track.JoinGenres(values);
    }

    // ── Writing ──

    public sealed record ApplyResult(int Written, IReadOnlyList<Track> Failed);

    /// <summary>
    /// Writes each track's new genre: the in-memory track first (views update), then the file
    /// through the metadata editor's writer with the genre marked as edited (so a shorter list is
    /// written as given), a few files at a time off the calling thread. A track whose file could not
    /// be written gets its old genre back. Then the library is saved and told about the change.
    /// </summary>
    public static async Task<ApplyResult> ApplyAsync(
        IReadOnlyList<(Track Track, string Genre)> changes, IMetadataService metadata, ILibraryService? library)
    {
        var previous = new Dictionary<Track, string>(ReferenceEqualityComparer.Instance);
        foreach (var (track, genre) in changes)
        {
            if (previous.ContainsKey(track) || string.Equals(track.Genre, genre, StringComparison.Ordinal)) continue;
            previous[track] = track.Genre;
            track.Genre = genre;
        }
        var targets = previous.Keys.ToList();
        var failed = new List<Track>();
        await Task.Run(() => Parallel.ForEach(
            targets.GroupBy(t => t.FilePath, StringComparer.OrdinalIgnoreCase),
            new ParallelOptions { MaxDegreeOfParallelism = 4 },
            group =>
            {
                foreach (var t in group)
                {
                    bool ok;
                    try { ok = t.HasFilesystemPath && !t.IsRemoteStream && metadata.WriteTrackMetadata(t, genreEdited: true); }
                    catch (Exception ex)
                    {
                        DebugLog.Write(LogSource, $"genre write failed for '{t.FilePath}': {ex.Message}");
                        ok = false;
                    }
                    if (!ok) lock (failed) failed.Add(t);
                }
            }));
        foreach (var t in failed) t.Genre = previous[t];
        foreach (var t in targets) t.NotifyMetadataUpdated();
        if (library is not null && targets.Count > failed.Count)
        {
            await library.SaveAsync();
            library.NotifyMetadataChanged();
        }
        return new ApplyResult(targets.Count - failed.Count, failed);
    }
}
