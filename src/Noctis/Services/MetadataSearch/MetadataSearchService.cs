using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// The metadata editor's search engine (owner 10-08: Search metadata revamp — the old path made
/// one silent best guess from Deezer's first hit). Asks every enabled provider at once, each
/// paced and time-boxed on its own, scores every candidate against the query with
/// <see cref="CandidateScorer"/>, folds cross-provider duplicates with
/// <see cref="CandidateMerger"/>, and reports per-provider outcomes so "offline" never reads as
/// "no match". Results are cached for the session (10 min) per provider and query; detail
/// payloads per URL (30 min).
/// </summary>
public sealed class MetadataSearchService : IMetadataSearchService
{
    public const int MaxCandidates = 25;
    private const double AgreementBonus = 0.03;

    private readonly HttpClient _http;
    private readonly Func<AppSettings> _settings;
    private readonly IReadOnlyList<IMetadataProvider> _providers;
    private readonly LruCache<string, IReadOnlyList<MetadataCandidate>> _results = new(64, TimeSpan.FromMinutes(10));

    public MetadataSearchService(HttpClient http, Func<AppSettings> settings)
        : this(http, settings, DefaultProviders(http)) { }

    public MetadataSearchService(HttpClient http, Func<AppSettings> settings, IReadOnlyList<IMetadataProvider> providers)
    {
        _http = http;
        _settings = settings;
        _providers = providers;
    }

    /// <summary>Deezer and Apple Music answer in well under a second; MusicBrainz is paced at
    /// 1 req/s, so it is listed last (it is also the last to arrive).</summary>
    public static IReadOnlyList<IMetadataProvider> DefaultProviders(HttpClient http)
    {
        var details = new LruCache<string, string>(128, TimeSpan.FromMinutes(30));
        return new IMetadataProvider[]
        {
            new DeezerProvider(http, details),
            new AppleMusicProvider(http, details),
            new MusicBrainzProvider(http, details),
        };
    }

    public IReadOnlyList<string> Providers => _providers.Select(p => p.Name).ToList();

    public async Task<MetadataSearchResult> SearchAsync(MetadataQuery query, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var requested = query.Providers.Count == 0
            ? _providers
            : _providers.Where(p => query.Providers.Contains(p.Name, StringComparer.OrdinalIgnoreCase)).ToList();

        if (!HasSearchText(query))
            return new MetadataSearchResult
            {
                Providers = requested.Select(p => new ProviderStatus(p.Name, ProviderOutcome.NoResults, 0, "Nothing to search for")).ToList(),
            };

        AppSettings settings;
        try { settings = _settings(); }
        catch (Exception) { settings = new AppSettings(); }

        var runs = requested.Select(p => RunProviderAsync(p, query, settings, ct)).ToArray();
        var outcomes = await Task.WhenAll(runs).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        // A provider can surface the same entity twice (an ISRC hit that its text search also
        // found, two lookups landing on one release); keep the first of each.
        var scored = outcomes.SelectMany(o => o.Candidates)
            .DistinctBy(c => (c.Provider, c.ProviderId))
            .Select(c => CandidateScorer.Score(query, c))
            .OrderByDescending(c => c.Confidence)
            .ToList();
        // Ties (several sources saturate at 1.0) go to the result more sources agree on, then to
        // the one with more fields filled in.
        var candidates = CandidateMerger.Group(scored, query.AlbumScope)
            .Select(g => (Candidate: Finish(query, g), Sources: g.Count))
            .OrderByDescending(x => x.Candidate.Confidence)
            .ThenByDescending(x => x.Sources)
            .ThenByDescending(x => Completeness(x.Candidate))
            .Select(x => x.Candidate)
            .Take(MaxCandidates)
            .ToList();

        return new MetadataSearchResult
        {
            Candidates = candidates,
            Providers = outcomes.Select(o => o.Status).ToList(),
        };
    }

    private static int Completeness(MetadataCandidate c)
    {
        var n = 0;
        foreach (var s in new[] { c.Album, c.AlbumArtist, c.ReleaseDate, c.Genre, c.Composer, c.Label, c.Copyright, c.Isrc, c.Barcode })
            if (s.Length > 0) n++;
        foreach (var v in new object?[] { c.TrackNumber, c.TrackCount, c.DiscNumber, c.DiscCount, c.Explicit, c.Bpm, c.Duration, c.ArtworkUrl })
            if (v is not null) n++;
        return n + c.Tracks.Count;
    }

    private static bool HasSearchText(MetadataQuery q)
        => q.AlbumScope
            ? q.Album.Trim().Length > 0
            : q.Title.Trim().Length > 0 || q.Isrc.Trim().Length > 0;

    // A merged candidate is re-scored on its combined fields (an ISRC from one source can match
    // the query) and gets a small bonus per agreeing source.
    private static MetadataCandidate Finish(MetadataQuery q, IReadOnlyList<MetadataCandidate> group)
    {
        if (group.Count == 1) return group[0];
        var rescored = CandidateScorer.Score(q, CandidateMerger.Merge(group));
        var confidence = Math.Max(rescored.Confidence, group.Max(m => m.Confidence));
        confidence = Math.Min(1.0, confidence + AgreementBonus * (group.Count - 1));
        return rescored with
        {
            Confidence = confidence,
            MatchNotes = rescored.MatchNotes.Append($"Found on {group.Count} sources").ToList(),
        };
    }

    private sealed record ProviderRun(ProviderStatus Status, IReadOnlyList<MetadataCandidate> Candidates);

    private async Task<ProviderRun> RunProviderAsync(IMetadataProvider p, MetadataQuery q, AppSettings settings, CancellationToken ct)
    {
        if (!p.IsEnabled(settings))
            return new ProviderRun(new ProviderStatus(p.Name, ProviderOutcome.Disabled, 0, "Turned off in Settings"), Array.Empty<MetadataCandidate>());

        var key = p.Name + "\u0001" + Fingerprint(q);
        if (_results.TryGet(key, out var cached))
            return new ProviderRun(StatusFor(p, cached), cached);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(p.Timeout);
        try
        {
            // Off the caller's context: providers parse JSON synchronously between awaits.
            var list = await Task.Run(() => p.SearchAsync(q, timeout.Token), timeout.Token).ConfigureAwait(false);
            _results.Set(key, list);
            return new ProviderRun(StatusFor(p, list), list);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            // Our per-provider budget, or HttpClient's own timeout.
            return Failed(p, ProviderOutcome.TimedOut, "No answer in time");
        }
        catch (HttpRequestException ex)
        {
            return Failed(p, ProviderOutcome.Failed, Describe(ex));
        }
        catch (JsonException)
        {
            return Failed(p, ProviderOutcome.Failed, "Unexpected response");
        }
        catch (Exception ex)
        {
            return Failed(p, ProviderOutcome.Failed, ex.Message);
        }
    }

    private static ProviderStatus StatusFor(IMetadataProvider p, IReadOnlyList<MetadataCandidate> list)
        => new(p.Name, list.Count > 0 ? ProviderOutcome.Ok : ProviderOutcome.NoResults, list.Count);

    private static ProviderRun Failed(IMetadataProvider p, ProviderOutcome outcome, string message)
        => new(new ProviderStatus(p.Name, outcome, 0, message), Array.Empty<MetadataCandidate>());

    private static string Describe(HttpRequestException ex) => ex.StatusCode switch
    {
        HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable => "Busy — try again shortly",
        { } code => $"HTTP {(int)code}",
        null when ex.InnerException is SocketException || ex.HttpRequestError is HttpRequestError.NameResolutionError
                  or HttpRequestError.ConnectionError => "Offline or unreachable",
        _ => ex.Message,
    };

    /// <summary>Cache key: every query field a provider's request or its "which hit to fetch
    /// details for" choice depends on.</summary>
    internal static string Fingerprint(MetadataQuery q)
    {
        var sb = new StringBuilder();
        sb.Append(q.AlbumScope ? 'A' : 'T').Append('|')
          .Append(q.Title.Trim().ToLowerInvariant()).Append('|')
          .Append(q.Artist.Trim().ToLowerInvariant()).Append('|')
          .Append(q.Album.Trim().ToLowerInvariant()).Append('|')
          .Append(q.AlbumArtist.Trim().ToLowerInvariant()).Append('|')
          .Append(MatchText.NormalizeCode(q.Isrc)).Append('|')
          .Append(q.Duration is { } d ? ((int)d.TotalSeconds).ToString(CultureInfo.InvariantCulture) : "").Append('|')
          .Append(q.Year).Append('|').Append(q.TrackNumber).Append('|').Append(q.DiscNumber).Append('|');
        foreach (var t in q.AlbumTracks)
            sb.Append(t.DiscNumber).Append('.').Append(t.TrackNumber).Append(':')
              .Append((int)t.Duration.TotalSeconds).Append(';');
        return sb.ToString();
    }

    // ── Artwork ──

    public async Task<byte[]?> DownloadArtworkAsync(MetadataCandidate candidate, CancellationToken ct = default)
    {
        foreach (var url in ArtworkUrls(candidate))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.UserAgent.ParseAdd(ProviderHttp.UserAgent);
                req.Headers.Accept.ParseAdd("image/*");
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;
                var type = resp.Content.Headers.ContentType?.MediaType;
                if (type is not null && !type.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
                    && type != "application/octet-stream" && type != "binary/octet-stream")
                    continue;
                var bytes = await HttpSafety.ReadBytesBoundedAsync(resp.Content, HttpSafety.MaxImageBytes, ct).ConfigureAwait(false);
                if (HttpSafety.LooksLikeImage(bytes)) return bytes;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception) { /* next size down */ }
        }
        return null;
    }

    /// <summary>Largest first, then smaller fallbacks (an original over the size cap, or a CDN
    /// hiccup), then the thumbnail.</summary>
    internal static IEnumerable<Uri> ArtworkUrls(MetadataCandidate c)
    {
        var seen = new HashSet<string>();
        IEnumerable<Uri?> Raw()
        {
            if (c.ArtworkUrl is { } full)
            {
                yield return full;
                var s = full.AbsoluteUri;
                // Cover Art Archive: no 1200 px rendition for small uploads → the original.
                if (full.Host.EndsWith("coverartarchive.org", StringComparison.OrdinalIgnoreCase) && s.EndsWith("/front-1200", StringComparison.Ordinal))
                    yield return Json.Url(s[..^"-1200".Length]);
                if (full.Host.EndsWith("mzstatic.com", StringComparison.OrdinalIgnoreCase))
                    yield return AppleMusicProvider.ResizeArtwork(s, 1200);
            }
            yield return c.ArtworkThumbUrl;
        }
        foreach (var u in Raw())
            if (u is not null && seen.Add(u.AbsoluteUri)) yield return u;
    }
}
