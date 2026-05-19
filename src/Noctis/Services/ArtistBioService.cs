using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Noctis.Services;

/// <summary>
/// Fetches biographical metadata for an artist (country, lifespan, genres, biography)
/// from MusicBrainz (no API key) and caches per-artist JSON under
/// <c>%APPDATA%\Noctis\artist_bios\{slug}.json</c>.
/// </summary>
public sealed class ArtistBioService
{
    private const string UserAgent = "Noctis/1.0 (https://github.com/heartached/Noctis)";
    private const string MusicBrainzArtistSearch = "https://musicbrainz.org/ws/2/artist/";

    private readonly HttpClient _http;
    private readonly string _bioDir;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly Regex SlugStrip = new(@"[^a-zA-Z0-9]+", RegexOptions.Compiled);

    public ArtistBioService(HttpClient http, IPersistenceService persistence)
    {
        _http = http;
        _bioDir = Path.Combine(persistence.DataDirectory, "artist_bios");
        Directory.CreateDirectory(_bioDir);
    }

    /// <summary>
    /// Returns biographical data for the artist. First served from cache when present,
    /// otherwise fetched live and persisted. Returns a minimally populated record on failure.
    /// </summary>
    public async Task<ArtistBio> GetAsync(string artistName, CancellationToken ct = default)
    {
        var path = CachePath(artistName);
        if (File.Exists(path))
        {
            try
            {
                var json = await File.ReadAllTextAsync(path, ct);
                var cached = JsonSerializer.Deserialize<ArtistBio>(json);
                if (cached != null) return cached;
            }
            catch { /* fall through to refetch */ }
        }

        // Serialize live lookups to be polite to MusicBrainz's 1 req/sec policy.
        await _gate.WaitAsync(ct);
        try
        {
            var bio = await FetchFromMusicBrainzAsync(artistName, ct) ?? new ArtistBio { Name = artistName };
            try
            {
                await File.WriteAllTextAsync(path,
                    JsonSerializer.Serialize(bio, new JsonSerializerOptions { WriteIndented = false }),
                    ct);
            }
            catch (Exception ex) { Debug.WriteLine($"[ArtistBio] cache write failed: {ex.Message}"); }
            return bio;
        }
        finally
        {
            _gate.Release();
            // MusicBrainz asks for ≤1 req/sec. Pause AFTER the call so the next caller waits.
            try { await Task.Delay(1100, CancellationToken.None); } catch { }
        }
    }

    private async Task<ArtistBio?> FetchFromMusicBrainzAsync(string artistName, CancellationToken ct)
    {
        try
        {
            var url = $"{MusicBrainzArtistSearch}?query={Uri.EscapeDataString("artist:" + artistName)}&fmt=json&limit=1";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(UserAgent);
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return null;
            var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("artists", out var artists) ||
                artists.ValueKind != JsonValueKind.Array ||
                artists.GetArrayLength() == 0)
                return new ArtistBio { Name = artistName };

            var a = artists[0];
            var bio = new ArtistBio { Name = artistName };

            if (a.TryGetProperty("name", out var nm) && nm.ValueKind == JsonValueKind.String)
                bio.Name = nm.GetString() ?? artistName;

            if (a.TryGetProperty("country", out var cc) && cc.ValueKind == JsonValueKind.String)
                bio.Country = cc.GetString() ?? string.Empty;

            if (a.TryGetProperty("area", out var area) &&
                area.TryGetProperty("name", out var areaName))
                bio.Area = areaName.GetString() ?? string.Empty;

            if (a.TryGetProperty("begin-area", out var bArea) &&
                bArea.TryGetProperty("name", out var bAreaName))
                bio.BeginArea = bAreaName.GetString() ?? string.Empty;

            if (a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String)
                bio.Type = t.GetString() ?? string.Empty;

            if (a.TryGetProperty("gender", out var g) && g.ValueKind == JsonValueKind.String)
                bio.Gender = g.GetString() ?? string.Empty;

            if (a.TryGetProperty("life-span", out var ls))
            {
                if (ls.TryGetProperty("begin", out var beg) && beg.ValueKind == JsonValueKind.String)
                    bio.LifeSpanBegin = beg.GetString() ?? string.Empty;
                if (ls.TryGetProperty("end", out var end) && end.ValueKind == JsonValueKind.String)
                    bio.LifeSpanEnd = end.GetString() ?? string.Empty;
            }

            if (a.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
            {
                var top = new List<string>();
                foreach (var tag in tags.EnumerateArray())
                {
                    if (tag.TryGetProperty("name", out var tn) && tn.ValueKind == JsonValueKind.String)
                    {
                        var name = tn.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) top.Add(name);
                    }
                    if (top.Count >= 5) break;
                }
                bio.Tags = top;
            }

            if (a.TryGetProperty("disambiguation", out var dis) && dis.ValueKind == JsonValueKind.String)
                bio.Disambiguation = dis.GetString() ?? string.Empty;

            return bio;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ArtistBio] MusicBrainz lookup failed: {ex.Message}");
            return null;
        }
    }

    private string CachePath(string artistName)
    {
        var slug = SlugStrip.Replace(artistName.Trim().ToLowerInvariant(), "-");
        if (slug.Length == 0) slug = "unknown";
        if (slug.Length > 80) slug = slug[..80];
        return Path.Combine(_bioDir, slug + ".json");
    }
}

/// <summary>
/// Cached biographical data for an artist. Designed to round-trip through System.Text.Json.
/// </summary>
public sealed class ArtistBio
{
    [JsonPropertyName("name")]            public string Name { get; set; } = string.Empty;
    [JsonPropertyName("country")]         public string Country { get; set; } = string.Empty;
    [JsonPropertyName("area")]            public string Area { get; set; } = string.Empty;
    [JsonPropertyName("beginArea")]       public string BeginArea { get; set; } = string.Empty;
    [JsonPropertyName("type")]            public string Type { get; set; } = string.Empty;
    [JsonPropertyName("gender")]          public string Gender { get; set; } = string.Empty;
    [JsonPropertyName("lifeSpanBegin")]   public string LifeSpanBegin { get; set; } = string.Empty;
    [JsonPropertyName("lifeSpanEnd")]     public string LifeSpanEnd { get; set; } = string.Empty;
    [JsonPropertyName("tags")]            public List<string> Tags { get; set; } = new();
    [JsonPropertyName("disambiguation")]  public string Disambiguation { get; set; } = string.Empty;

    /// <summary>Best-effort "Origin" display: BeginArea, else Area, else Country.</summary>
    public string FromDisplay =>
        !string.IsNullOrWhiteSpace(BeginArea) ? BeginArea :
        !string.IsNullOrWhiteSpace(Area) ? Area :
        Country;

    /// <summary>"Born" or "Founded" label depending on artist type.</summary>
    public string BornLabel =>
        string.Equals(Type, "Group", StringComparison.OrdinalIgnoreCase) ? "FOUNDED" : "BORN";

    public string TagsDisplay => Tags is { Count: > 0 } ? string.Join(", ", Tags) : string.Empty;
}
