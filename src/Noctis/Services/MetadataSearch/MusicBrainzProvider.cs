using System.Globalization;
using System.Text;
using System.Text.Json;
using Noctis.Models;

namespace Noctis.Services.MetadataSearch;

/// <summary>
/// MusicBrainz WS/2 + Cover Art Archive (keyless, open data). The authority for release
/// structure (label, barcode, disc/track counts, original release date) and the only keyless
/// source here for composers (work relationships). ISRC lookups via <c>query=isrc:</c>.
/// Strictly paced at ≤ 1 request/second (<see cref="RequestPacer.MusicBrainz"/>) with a
/// User-Agent carrying the project URL, as MusicBrainz requires — so it only fetches details
/// for its best hit or two.
/// </summary>
public sealed class MusicBrainzProvider : IMetadataProvider
{
    private const string Ws = "https://musicbrainz.org/ws/2";
    private const string Caa = "https://coverartarchive.org";
    private const int AlbumLookupTop = 2;

    private readonly ProviderHttp _http;

    public MusicBrainzProvider(HttpClient http, LruCache<string, string>? cache = null, RequestPacer? pacer = null)
        => _http = new ProviderHttp(http, pacer ?? RequestPacer.MusicBrainz, cache);

    public string Name => ProviderNames.MusicBrainz;
    // Up to four paced requests (≈ 4.4 s) plus network; the slowest provider by design.
    public TimeSpan Timeout => TimeSpan.FromSeconds(20);
    public bool IsEnabled(AppSettings settings) => settings.MusicBrainzEnabled;

    public Task<IReadOnlyList<MetadataCandidate>> SearchAsync(MetadataQuery query, CancellationToken ct)
        => query.AlbumScope ? SearchReleasesAsync(query, ct) : SearchRecordingsAsync(query, ct);

    // ── Track scope ──

    private async Task<IReadOnlyList<MetadataCandidate>> SearchRecordingsAsync(MetadataQuery q, CancellationToken ct)
    {
        var found = new List<MetadataCandidate>();
        var releaseIds = new Dictionary<string, string>();
        void AddAll(string? json)
        {
            foreach (var (c, releaseId) in ParseRecordings(json, q.Album, q.Isrc))
            {
                if (found.Any(f => f.ProviderId == c.ProviderId)) continue;
                found.Add(c);
                if (releaseId.Length > 0) releaseIds[c.ProviderId] = releaseId;
            }
        }

        if (q.Isrc.Length > 0)
        {
            var json = await _http.GetStringAsync(RecordingSearchUrl("isrc:" + MatchText.NormalizeCode(q.Isrc), 5), ct).ConfigureAwait(false);
            AddAll(json);
        }

        // An ISRC hit is exact; spend the next second on details instead of a text search.
        if (found.Count == 0 && q.Title.Length > 0)
        {
            var title = MatchText.StripFeaturing(q.Title);
            var artist = q.Artist.Length > 0 ? Track.GetPrimaryArtist(q.Artist) : Track.GetPrimaryArtist(q.AlbumArtist);
            var album = AlbumTitleNormalizer.Normalize(q.Album);
            var json = await _http.GetStringAsync(RecordingSearchUrl(BuildRecordingQuery(title, artist, album), 15), ct).ConfigureAwait(false);
            AddAll(json);
            if (found.Count == 0 && album.Length > 0)
            {
                json = await _http.GetStringAsync(RecordingSearchUrl(BuildRecordingQuery(title, artist, string.Empty), 15), ct).ConfigureAwait(false);
                AddAll(json);
            }
        }

        var best = found.Select(c => CandidateScorer.ScoreTrack(q, c)).OrderByDescending(c => c.Confidence).FirstOrDefault();
        if (best is null) return found;
        var i = found.FindIndex(c => c.ProviderId == best.ProviderId);
        found[i] = await TryEnrichRecordingAsync(found[i], releaseIds.GetValueOrDefault(best.ProviderId), ct).ConfigureAwait(false);
        return found;
    }

    private async Task<MetadataCandidate> TryEnrichRecordingAsync(MetadataCandidate c, string? releaseId, CancellationToken ct)
    {
        try
        {
            if (!string.IsNullOrEmpty(releaseId))
            {
                var rel = await _http.GetStringAsync($"{Ws}/release/{releaseId}?fmt=json&inc=labels+recordings+artist-credits+isrcs+release-groups+genres", ct).ConfigureAwait(false);
                if (rel is not null) c = ApplyRelease(c, rel);
            }
            var rec = await _http.GetStringAsync($"{Ws}/recording/{c.ProviderId}?fmt=json&inc=artist-credits+isrcs+genres+work-rels+work-level-rels+artist-rels", ct).ConfigureAwait(false);
            if (rec is not null) c = ApplyRecordingDetails(c, rec);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { /* details are a bonus; keep the search hit */ }
        return c;
    }

    internal static string BuildRecordingQuery(string title, string artist, string album)
    {
        var clauses = new List<string>();
        if (title.Length > 0) clauses.Add($"recording:\"{Escape(title)}\"");
        if (artist.Length > 0) clauses.Add($"artist:\"{Escape(artist)}\"");
        if (album.Length > 0) clauses.Add($"release:\"{Escape(album)}\"");
        return string.Join(" AND ", clauses);
    }

    private static string RecordingSearchUrl(string query, int limit)
        => $"{Ws}/recording?query={Uri.EscapeDataString(query)}&fmt=json&limit={limit}";

    // Inside a quoted Lucene phrase only the quote and the backslash need escaping.
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // ── Album scope ──

    private async Task<IReadOnlyList<MetadataCandidate>> SearchReleasesAsync(MetadataQuery q, CancellationToken ct)
    {
        if (q.Album.Length == 0) return Array.Empty<MetadataCandidate>();
        var artist = Track.GetPrimaryArtist(q.AlbumArtist.Length > 0 ? q.AlbumArtist : q.Artist);
        var hits = ParseReleaseSearch(await _http.GetStringAsync(ReleaseSearchUrl(q.Album.Trim(), artist), ct).ConfigureAwait(false));
        var normalized = AlbumTitleNormalizer.Normalize(q.Album);
        if (hits.Count == 0 && !string.Equals(normalized, q.Album.Trim(), StringComparison.OrdinalIgnoreCase))
            hits = ParseReleaseSearch(await _http.GetStringAsync(ReleaseSearchUrl(normalized, artist), ct).ConfigureAwait(false));

        var top = hits.Select(c => CandidateScorer.ScoreAlbum(q, c))
            .OrderByDescending(c => c.Confidence)
            .ThenBy(c => c.Year ?? int.MaxValue)
            .Take(AlbumLookupTop)
            .Select(c => c.ProviderId)
            .ToList();
        var result = new List<MetadataCandidate>(hits.Count);
        foreach (var hit in hits)
        {
            if (!top.Contains(hit.ProviderId)) { result.Add(hit); continue; }
            try
            {
                var json = await _http.GetStringAsync($"{Ws}/release/{hit.ProviderId}?fmt=json&inc=labels+recordings+artist-credits+isrcs+release-groups+genres", ct).ConfigureAwait(false);
                result.Add(json is null ? hit : ParseRelease(json) ?? hit);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { result.Add(hit); }
        }
        return result;
    }

    private static string ReleaseSearchUrl(string album, string artist)
    {
        var query = $"release:\"{Escape(album)}\"" + (artist.Length > 0 ? $" AND artist:\"{Escape(artist)}\"" : string.Empty);
        return $"{Ws}/release?query={Uri.EscapeDataString(query)}&fmt=json&limit=15";
    }

    // ── Parsing (pure; tested against recorded responses) ──

    /// <summary>
    /// Parses a recording search into one candidate per recording, each placed on its most
    /// plausible release: the one matching the query's album, official, a plain album (not a
    /// compilation/bootleg), earliest. Returns the chosen release id alongside.
    /// </summary>
    internal static IReadOnlyList<(MetadataCandidate Candidate, string ReleaseId)> ParseRecordings(string? json, string queryAlbum, string queryIsrc)
    {
        var list = new List<(MetadataCandidate, string)>();
        if (string.IsNullOrWhiteSpace(json)) return list;
        using var doc = JsonDocument.Parse(json);
        foreach (var rec in Json.Arr(doc.RootElement, "recordings"))
        {
            var id = Json.Str(rec, "id");
            var title = Json.Str(rec, "title");
            if (id.Length == 0 || title.Length == 0) continue;

            var isrcs = Json.Arr(rec, "isrcs").Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString() ?? "").ToList();
            var isrc = isrcs.FirstOrDefault(i => MatchText.SameIsrc(i, queryIsrc)) ?? isrcs.FirstOrDefault() ?? string.Empty;
            var (date, year) = MatchText.ParseDate(Json.Str(rec, "first-release-date"));
            var c = new MetadataCandidate
            {
                Provider = ProviderNames.MusicBrainz,
                ProviderId = id,
                WebUrl = Json.Url("https://musicbrainz.org/recording/" + id),
                Title = title,
                Artist = Credit(rec),
                Isrc = isrc,
                Duration = Json.Millis(Json.Long(rec, "length")),
                ReleaseDate = date,
                Year = year,
            };

            var release = ChooseRelease(Json.Arr(rec, "releases"), queryAlbum);
            var releaseId = string.Empty;
            if (release.ValueKind == JsonValueKind.Object)
            {
                releaseId = Json.Str(release, "id");
                var medium = Json.Arr(release, "media").FirstOrDefault();
                var track = Json.Arr(medium, "track").Concat(Json.Arr(medium, "tracks")).FirstOrDefault();
                var (rDate, rYear) = MatchText.ParseDate(Json.Str(release, "date"));
                var albumArtist = Credit(release);
                c = c with
                {
                    Album = Json.Str(release, "title"),
                    AlbumArtist = albumArtist.Length > 0 ? albumArtist : c.Artist,
                    // The recording's first-release-date is the song's debut (often a single);
                    // the release's own date belongs with the album it is placed on.
                    ReleaseDate = rDate.Length > 0 ? rDate : c.ReleaseDate,
                    Year = rYear ?? c.Year,
                    TrackNumber = ParseTrackNumber(track),
                    DiscNumber = Json.Int(medium, "position") is > 0 and var dn ? dn : null,
                    TrackCount = Json.Int(medium, "track-count") is > 0 and var tc ? tc : null,
                };
            }
            list.Add((c, releaseId));
        }
        return list;
    }

    private static JsonElement ChooseRelease(IEnumerable<JsonElement> releases, string queryAlbum)
    {
        JsonElement best = default;
        var bestScore = double.MinValue;
        var bestDate = string.Empty;
        foreach (var r in releases)
        {
            var score = 0.0;
            if (queryAlbum.Length > 0) score += 3 * MatchText.AlbumSimilarity(queryAlbum, Json.Str(r, "title"));
            score += Json.Str(r, "status") switch
            {
                "Official" => 1.0,
                "Promotion" => -0.5,
                "Bootleg" or "Pseudo-Release" => -2.0,
                _ => 0.0,
            };
            var rg = Json.Obj(r, "release-group");
            score += Json.Str(rg, "primary-type") switch { "Album" => 0.5, "Single" or "EP" => 0.2, _ => 0.0 };
            if (!Json.Arr(rg, "secondary-types").Any()) score += 0.5; // not a compilation/live/soundtrack
            var date = Json.Str(r, "date");
            var earlier = date.Length > 0 && (bestDate.Length == 0 || string.CompareOrdinal(date, bestDate) < 0);
            if (score > bestScore + 1e-9 || (Math.Abs(score - bestScore) < 1e-9 && earlier))
            {
                best = r;
                bestScore = score;
                bestDate = date;
            }
        }
        return best;
    }

    private static int? ParseTrackNumber(JsonElement track)
    {
        // "number" is the printed label ("A1" on vinyl); "position" is the ordinal on the medium.
        var number = Json.Str(track, "number");
        if (int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0) return n;
        return Json.Int(track, "position") is > 0 and var p ? p : null;
    }

    /// <summary>Adds release details (label, barcode, counts, original date, genre, CAA cover and
    /// the recording's real position) from a <c>/release/{id}</c> lookup to a track candidate.</summary>
    public static MetadataCandidate ApplyRelease(MetadataCandidate c, string releaseJson)
    {
        var full = ParseRelease(releaseJson);
        if (full is null) return c;
        using var doc = JsonDocument.Parse(releaseJson);
        int? trackNo = c.TrackNumber, discNo = c.DiscNumber, trackCount = c.TrackCount;
        foreach (var medium in Json.Arr(doc.RootElement, "media"))
            foreach (var t in Json.Arr(medium, "tracks"))
                if (Json.Str(Json.Obj(t, "recording"), "id") == c.ProviderId)
                {
                    trackNo = ParseTrackNumber(t);
                    discNo = Json.Int(medium, "position") is > 0 and var d ? d : discNo;
                    trackCount = Json.Int(medium, "track-count") is > 0 and var n ? n : trackCount;
                }
        return c with
        {
            Album = full.Album.Length > 0 ? full.Album : c.Album,
            AlbumArtist = full.AlbumArtist.Length > 0 ? full.AlbumArtist : c.AlbumArtist,
            ReleaseDate = full.ReleaseDate.Length > 0 ? full.ReleaseDate : c.ReleaseDate,
            Year = full.Year ?? c.Year,
            Genre = c.Genre.Length > 0 ? c.Genre : full.Genre,
            Label = full.Label,
            Barcode = full.Barcode,
            TrackNumber = trackNo,
            DiscNumber = discNo,
            TrackCount = trackCount,
            DiscCount = full.DiscCount,
            ArtworkUrl = full.ArtworkUrl,
            ArtworkThumbUrl = full.ArtworkThumbUrl,
        };
    }

    /// <summary>Adds songwriters (composer/writer/lyricist of the performed work), ISRC and genre from a
    /// <c>/recording/{id}?inc=…work-rels+work-level-rels</c> lookup.</summary>
    public static MetadataCandidate ApplyRecordingDetails(MetadataCandidate c, string recordingJson)
    {
        using var doc = JsonDocument.Parse(recordingJson);
        var rec = doc.RootElement;
        // One entry per songwriter (by MusicBrainz artist id, else name), in credit order.
        var people = new List<(string Key, string Name)>();
        foreach (var rel in Json.Arr(rec, "relations"))
        {
            // Only the work this recording performs: "samples material" relations point at OTHER
            // songs, whose writers did not write this one.
            if (Json.Str(rel, "target-type") != "work" || Json.Str(rel, "type") != "performance") continue;
            foreach (var wr in Json.Arr(Json.Obj(rel, "work"), "relations"))
            {
                // The Composer tag conventionally holds every songwriter: MusicBrainz splits the
                // credit into composer/writer/lyricist ("Lucid Dreams": composer Sting via the
                // interpolation, writers Juice WRLD, Nick Mira, Dominic Miller).
                if (Json.Str(wr, "type") is not ("composer" or "writer" or "lyricist")) continue;
                var name = SongwriterName(wr);
                if (name.Length == 0) continue;
                var artist = Json.Obj(wr, "artist");
                var key = Json.Str(artist, "id") is { Length: > 0 } id ? id : name;
                var at = people.FindIndex(p => p.Key == key);
                if (at < 0) people.Add((key, name));
                // The same person credited twice (composer + writer): a credited name beats the
                // plain artist name on the other relation.
                else if (Json.Str(wr, "target-credit").Length > 0 && people[at].Name == Json.Str(artist, "name"))
                    people[at] = (key, name);
            }
        }
        var isrc = c.Isrc.Length > 0 ? c.Isrc
            : Json.Arr(rec, "isrcs").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").FirstOrDefault(s => s.Length > 0) ?? "";
        return c with
        {
            Composer = people.Count > 0 ? string.Join(ArtistCredit.JoinText, people.Select(p => p.Name)) : c.Composer,
            Isrc = isrc,
            Genre = c.Genre.Length > 0 ? c.Genre : TopGenre(rec),
        };
    }

    /// <summary>
    /// A songwriter as credited on this songwriting relationship ("target-credit"), else the
    /// artist's MusicBrainz name. Owner 10-08: "Talk of the Town" listed producer alias
    /// "2forwOyNE" among the writers; MusicBrainz's writer relation credits him as "Dawoyne
    /// Lawson" (likewise "Dougie F" → "Douglas Ford"), the name printed in songwriting credits.
    /// The work relation carries no legal-name alias, and the sort-name is just the alias
    /// again ("2forwOyNE"), so the relationship credit is the one consistent source.
    /// </summary>
    private static string SongwriterName(JsonElement workRelation)
    {
        var credited = Json.Str(workRelation, "target-credit");
        return credited.Length > 0 ? credited : Json.Str(Json.Obj(workRelation, "artist"), "name");
    }

    /// <summary>Parses a release search into light release candidates (no track list). Bootlegs
    /// are dropped when an official release exists.</summary>
    public static IReadOnlyList<MetadataCandidate> ParseReleaseSearch(string? json)
    {
        var list = new List<(MetadataCandidate C, string Status)>();
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<MetadataCandidate>();
        using var doc = JsonDocument.Parse(json);
        foreach (var r in Json.Arr(doc.RootElement, "releases"))
        {
            var id = Json.Str(r, "id");
            var title = Json.Str(r, "title");
            if (id.Length == 0 || title.Length == 0) continue;
            var (date, year) = MatchText.ParseDate(Json.Str(r, "date"));
            var credit = Credit(r);
            var media = Json.Arr(r, "media").ToList();
            list.Add((new MetadataCandidate
            {
                Provider = ProviderNames.MusicBrainz,
                ProviderId = id,
                WebUrl = Json.Url("https://musicbrainz.org/release/" + id),
                Album = title,
                Artist = credit,
                AlbumArtist = credit,
                ReleaseDate = date,
                Year = year,
                Barcode = Json.Str(r, "barcode"),
                Label = FirstLabel(r),
                TrackCount = Json.Int(r, "track-count") is > 0 and var tc ? tc : null,
                // Number of media = discs. (The search's "disc-count" is the number of disc IDs.)
                DiscCount = media.Count > 0 ? media.Count : null,
            }, Json.Str(r, "status")));
        }
        var hasOfficial = list.Any(x => x.Status == "Official");
        return list.Where(x => !hasOfficial || x.Status is not ("Bootleg" or "Pseudo-Release")).Select(x => x.C).ToList();
    }

    /// <summary>Parses a <c>/release/{id}?inc=recordings+…</c> lookup into a full release candidate.</summary>
    public static MetadataCandidate? ParseRelease(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        var id = Json.Str(r, "id");
        var title = Json.Str(r, "title");
        if (id.Length == 0 || title.Length == 0) return null;

        var tracks = new List<CandidateTrack>();
        var media = Json.Arr(r, "media").ToList();
        foreach (var medium in media)
        {
            var disc = Json.Int(medium, "position") is > 0 and var d ? d : (int?)null;
            foreach (var t in Json.Arr(medium, "tracks"))
            {
                var recording = Json.Obj(t, "recording");
                var credit = Credit(t);
                tracks.Add(new CandidateTrack
                {
                    Title = Json.Str(t, "title"),
                    Artist = credit.Length > 0 ? credit : Credit(recording),
                    TrackNumber = ParseTrackNumber(t),
                    DiscNumber = disc,
                    Duration = Json.Millis(Json.Long(t, "length")) ?? Json.Millis(Json.Long(recording, "length")),
                    Isrc = Json.Arr(recording, "isrcs").Select(e => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : "").FirstOrDefault() ?? "",
                });
            }
        }

        // The release group's first release date is the album's original date; a 2005 reissue
        // of a 2001 album should still tag as 2001.
        var rg = Json.Obj(r, "release-group");
        var (date, year) = MatchText.ParseDate(Json.Str(rg, "first-release-date"));
        if (date.Length == 0) (date, year) = MatchText.ParseDate(Json.Str(r, "date"));
        var credit2 = Credit(r);
        var caa = Json.Obj(r, "cover-art-archive");
        var hasFront = Json.Bool(caa, "front") == true;
        var genre = TopGenre(rg);
        return new MetadataCandidate
        {
            Provider = ProviderNames.MusicBrainz,
            ProviderId = id,
            WebUrl = Json.Url("https://musicbrainz.org/release/" + id),
            Album = title,
            Artist = credit2,
            AlbumArtist = credit2,
            ReleaseDate = date,
            Year = year,
            Genre = genre.Length > 0 ? genre : TopGenre(r),
            Label = FirstLabel(r),
            Barcode = Json.Str(r, "barcode"),
            TrackCount = tracks.Count > 0 ? tracks.Count : null,
            DiscCount = media.Count > 0 ? media.Count : null,
            // Cover Art Archive: /front-1200 redirects to a ≤1200 px rendition, -250 to a
            // thumbnail. Not the bare /front: originals are raw uploads (a 12 MB scan came back
            // in testing) — DownloadArtworkAsync falls back to it when no 1200 exists.
            // Only offered when the release says it has a front image.
            ArtworkUrl = hasFront ? Json.Url($"{Caa}/release/{id}/front-1200") : null,
            ArtworkThumbUrl = hasFront ? Json.Url($"{Caa}/release/{id}/front-250") : null,
            Tracks = tracks,
        };
    }

    /// <summary>The artist credit as printed: names joined by their join phrases
    /// ("Daft Punk feat. Pharrell Williams").</summary>
    private static string Credit(JsonElement el)
    {
        var sb = new StringBuilder();
        foreach (var part in Json.Arr(el, "artist-credit"))
        {
            var name = Json.Str(part, "name");
            if (name.Length == 0) name = Json.Str(Json.Obj(part, "artist"), "name");
            sb.Append(name);
            if (part.TryGetProperty("joinphrase", out var jp) && jp.ValueKind == JsonValueKind.String)
                sb.Append(jp.GetString());
        }
        return sb.ToString().Trim();
    }

    private static string FirstLabel(JsonElement release)
    {
        // Distributors are listed alongside the label; prefer a real label/imprint.
        var labels = Json.Arr(release, "label-info").Select(li => Json.Obj(li, "label")).Where(l => l.ValueKind == JsonValueKind.Object).ToList();
        var pick = labels.FirstOrDefault(l => Json.Str(l, "type") is not ("Distributor" or "Holding" or "Rights Society"));
        if (pick.ValueKind != JsonValueKind.Object) pick = labels.FirstOrDefault();
        return pick.ValueKind == JsonValueKind.Object ? Json.Str(pick, "name") : string.Empty;
    }

    // MusicBrainz genres are lower-case folksonomy tags with vote counts; take the most-voted.
    private static string TopGenre(JsonElement el)
    {
        var top = Json.Arr(el, "genres")
            .Select(g => (Name: Json.Str(g, "name"), Count: Json.Int(g, "count") ?? 0))
            .Where(g => g.Name.Length > 0)
            .OrderByDescending(g => g.Count)
            .FirstOrDefault();
        return top.Name is { Length: > 0 } n ? CultureInfo.InvariantCulture.TextInfo.ToTitleCase(n) : string.Empty;
    }
}
