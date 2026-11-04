using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Noctis.Services;

/// <summary>
/// "Similar Artists" for the artist page, from Deezer's public related-artists endpoint
/// (the same keyless API the portrait fetcher uses): search the artist by name, pick the
/// real account the way <see cref="ArtistImageService"/> does (exact name, most fans),
/// then <c>/artist/{id}/related</c>. Portraits are downloaded once into
/// <c>artwork/similar/</c>; the list is cached per artist id under <c>artist_info/</c>
/// for 30 days (3 days for a miss). Never throws: a network failure is an empty list.
/// </summary>
public sealed class SimilarArtistsService
{
    private const string DeezerSearchUrl = "https://api.deezer.com/search/artist";
    private const string DeezerArtistUrl = "https://api.deezer.com/artist/";
    private const int MaxRelated = 12;
    private const long MaxImageBytes = 2 * 1024 * 1024;
    private static readonly TimeSpan HitTtl = TimeSpan.FromDays(30);
    private static readonly TimeSpan MissTtl = TimeSpan.FromDays(3);
    private const int RequestPacingMs = 120;

    private readonly HttpClient _http;
    private readonly string _cacheDir;
    private readonly string _imageDir;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SimilarArtistsService(HttpClient http, IPersistenceService persistence)
    {
        _http = http;
        _cacheDir = Path.Combine(persistence.DataDirectory, "artist_info");
        _imageDir = Path.Combine(persistence.DataDirectory, "artwork", "similar");
        try { Directory.CreateDirectory(_cacheDir); Directory.CreateDirectory(_imageDir); } catch { }
    }

    /// <summary>Related artists, most similar first; empty when unknown or offline.
    /// <paramref name="pinnedDeezerId"/> is the account the user picked with Search Artist
    /// Info (GitHub #141/#170): its related list is asked for directly, never the name
    /// search, and a list cached for another account (or for the name search) is re-fetched.</summary>
    public async Task<IReadOnlyList<SimilarArtist>> GetAsync(Guid artistId, string artistName, long? pinnedDeezerId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(artistName) || artistName == "Unknown Artist") return Array.Empty<SimilarArtist>();

        var path = Path.Combine(_cacheDir, $"{artistId}.similar.json");
        var cached = ReadCache(path);
        var sameAccount = cached != null && cached.PinnedDeezerId == pinnedDeezerId;
        if (cached != null && sameAccount && !IsStale(cached))
            return cached.Artists;

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var artists = await FetchAsync(artistName, pinnedDeezerId, ct).ConfigureAwait(false);
            var entry = new SimilarArtistsCache
            {
                Artists = artists, FetchedAtUtc = DateTime.UtcNow, Picker = PickerVersion, PinnedDeezerId = pinnedDeezerId,
            };
            WriteCache(path, entry);
            return artists;
        }
        catch (OperationCanceledException) { throw; }
        catch
        {
            // Offline: a stale list for the same account beats nothing; another account's never.
            return sameAccount ? cached!.Artists : Array.Empty<SimilarArtist>();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Bumped when the way the Deezer account is picked changes, so lists built
    /// for the wrong same-name account are re-fetched. 1: names folded without diacritics.</summary>
    internal const int PickerVersion = 1;

    private static bool IsStale(SimilarArtistsCache c)
        => c.Picker != PickerVersion
           || DateTime.UtcNow - c.FetchedAtUtc > (c.Artists.Count > 0 ? HitTtl : MissTtl);

    private static SimilarArtistsCache? ReadCache(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<SimilarArtistsCache>(File.ReadAllText(path));
        }
        catch { return null; }
    }

    private static void WriteCache(string path, SimilarArtistsCache entry)
    {
        try
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(entry));
            File.Move(tmp, path, overwrite: true);
        }
        catch { }
    }

    private async Task<List<SimilarArtist>> FetchAsync(string artistName, long? pinnedDeezerId, CancellationToken ct)
    {
        long deezerId;
        if (pinnedDeezerId is > 0)
        {
            deezerId = pinnedDeezerId.Value;
        }
        else
        {
            var searchJson = await GetTextAsync(
                $"{DeezerSearchUrl}?q={Uri.EscapeDataString(artistName.Trim())}&limit=10", ct).ConfigureAwait(false);
            if (searchJson == null) return new List<SimilarArtist>();
            using (var doc = JsonDocument.Parse(searchJson))
                deezerId = PickBestMatch(doc.RootElement, artistName);
            if (deezerId <= 0) return new List<SimilarArtist>();
            await Task.Delay(RequestPacingMs, ct).ConfigureAwait(false);
        }

        var relatedJson = await GetTextAsync($"{DeezerArtistUrl}{deezerId}/related?limit={MaxRelated}", ct).ConfigureAwait(false);
        if (relatedJson == null) return new List<SimilarArtist>();
        List<SimilarArtist> related;
        using (var doc = JsonDocument.Parse(relatedJson))
            related = ParseRelated(doc.RootElement);

        foreach (var a in related)
        {
            ct.ThrowIfCancellationRequested();
            if (a.PictureUrl.Length == 0) continue;
            var local = Path.Combine(_imageDir, $"{a.DeezerId}.jpg");
            if (!File.Exists(local))
            {
                await Task.Delay(RequestPacingMs, ct).ConfigureAwait(false);
                await DownloadAsync(a.PictureUrl, local, ct).ConfigureAwait(false);
            }
            if (File.Exists(local)) a.ImagePath = local;
        }
        return related;
    }

    private async Task DownloadAsync(string url, string local, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return;
            var bytes = await HttpSafety.ReadBytesBoundedAsync(resp.Content, MaxImageBytes, ct).ConfigureAwait(false);
            if (bytes.Length == 0) return;
            var tmp = local + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, ct).ConfigureAwait(false);
            File.Move(tmp, local, overwrite: true);
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    private async Task<string?> GetTextAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        return await HttpSafety.ReadStringBoundedAsync(resp.Content, ct: ct).ConfigureAwait(false);
    }

    // ── Parsers (pure, internal for tests) ──

    /// <summary>The Deezer id of the artist: exact-name matches only (Deezer search is
    /// fuzzy), compared without diacritics (the real "Arcángel" is "Arcangel" there, 09-17),
    /// the one with the most fans when same-name impostors exist; 0 when none.</summary>
    internal static long PickBestMatch(JsonElement searchRoot, string artistName)
    {
        if (!searchRoot.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return 0;
        long bestId = 0, bestFans = -1;
        var query = ArtistImageService.FoldDiacritics(artistName.Trim());
        foreach (var item in data.EnumerateArray())
        {
            var name = item.TryGetProperty("name", out var n) ? n.GetString()?.Trim() : null;
            if (name == null || !string.Equals(ArtistImageService.FoldDiacritics(name), query, StringComparison.OrdinalIgnoreCase)) continue;
            var id = ReadLong(item, "id");
            var fans = ReadLong(item, "nb_fan");
            if (id > 0 && fans > bestFans) { bestId = id; bestFans = fans; }
        }
        return bestId;
    }

    internal static List<SimilarArtist> ParseRelated(JsonElement root)
    {
        var list = new List<SimilarArtist>();
        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return list;
        foreach (var item in data.EnumerateArray())
        {
            var id = ReadLong(item, "id");
            var name = item.TryGetProperty("name", out var n) ? n.GetString()?.Trim() ?? "" : "";
            if (id <= 0 || name.Length == 0) continue;
            var picture = "";
            foreach (var prop in new[] { "picture_big", "picture_medium", "picture_xl" })
            {
                if (item.TryGetProperty(prop, out var p) && p.ValueKind == JsonValueKind.String)
                {
                    var url = p.GetString() ?? "";
                    if (url.Length > 0 && !url.Contains("/artist//", StringComparison.Ordinal)) { picture = url; break; }
                }
            }
            list.Add(new SimilarArtist { DeezerId = id, Name = name, PictureUrl = picture });
            if (list.Count >= MaxRelated) break;
        }
        return list;
    }

    private static long ReadLong(JsonElement item, string property)
        => item.TryGetProperty(property, out var node) && node.ValueKind == JsonValueKind.Number && node.TryGetInt64(out var v) ? v : 0;
}

/// <summary>One related artist (cached; round-trips through System.Text.Json).</summary>
public sealed class SimilarArtist
{
    public long DeezerId { get; set; }
    public string Name { get; set; } = "";
    public string PictureUrl { get; set; } = "";
    /// <summary>Local portrait under artwork/similar/, empty when the download failed.</summary>
    public string ImagePath { get; set; } = "";
}

public sealed class SimilarArtistsCache
{
    public List<SimilarArtist> Artists { get; set; } = new();
    public DateTime FetchedAtUtc { get; set; }
    /// <summary>Picker logic that chose the account (absent in older files = 0 = stale).</summary>
    public int Picker { get; set; }
    /// <summary>The Deezer account the user picked when this list was fetched; null when it
    /// came from the name search. A different pick (or a reset) makes the entry stale.</summary>
    public long? PinnedDeezerId { get; set; }
}
