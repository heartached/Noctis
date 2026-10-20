using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Noctis.Services;

/// <summary>
/// The API keys the logo lookup may use. None is built in: fanart.tv hands out a project key
/// per app (free, https://fanart.tv/get-an-api-key/), and TheAudioDB's free test key may only be
/// used for development, never in an app published to a store (its terms of use, 2025-07-01),
/// so each build brings its own (see the Android project's NoctisFanartApiKey property). Without
/// any key there is no lookup at all. <see cref="ToString"/> never prints a key.
/// </summary>
public sealed record ArtistLogoKeys(string? FanartProjectKey = null, string? FanartClientKey = null, string? TheAudioDbKey = null)
{
    public bool HasFanart => !string.IsNullOrWhiteSpace(FanartProjectKey);
    public bool HasTheAudioDb => !string.IsNullOrWhiteSpace(TheAudioDbKey);
    public bool IsEmpty => !HasFanart && !HasTheAudioDb;

    public override string ToString() =>
        $"ArtistLogoKeys {{ fanart.tv: {(HasFanart ? "set" : "none")}, TheAudioDB: {(HasTheAudioDb ? "set" : "none")} }}";

    /// <summary>The keys an assembly was built with: AssemblyMetadata "NoctisFanartApiKey",
    /// "NoctisFanartClientKey" and "NoctisTheAudioDbApiKey" (the build's MSBuild properties of
    /// those names, or environment variables; nothing in the repository).</summary>
    public static ArtistLogoKeys FromAssembly(Assembly assembly)
    {
        string? Read(string name) => assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == name)?.Value is { Length: > 0 } v ? v.Trim() : null;
        return new ArtistLogoKeys(Read("NoctisFanartApiKey"), Read("NoctisFanartClientKey"), Read("NoctisTheAudioDbApiKey"));
    }
}

/// <summary>
/// Artists' own logos (the transparent wordmark Apple Music's artist pages show instead of the
/// name), looked up by name only and kept on disk, so a logo seen once shows offline.
/// <list type="bullet">
/// <item><b>Which artist.</b> The name becomes a MusicBrainz id: MusicBrainz's artist search,
/// keeping only hits whose name (or an alias) is the asked one, diacritics and case folded. When
/// several artists share the name, the library's own song titles pick the one MusicBrainz credits
/// with them; when nothing tells them apart, no logo (a wrong artist's logo is worse than the
/// name). MusicBrainz asks for one request a second, which every request here keeps.</item>
/// <item><b>Which logo.</b> fanart.tv by that id (HD logo, most liked first, then the older
/// small logo), then TheAudioDB by that id; each only with its key. TheAudioDB's name search
/// stands in for the id when MusicBrainz gives no answer at all (down, rate limited), and is
/// accepted only for the exact name.</item>
/// <item><b>Only real logos.</b> A PNG (transparency), within the image size bound; anything
/// else is no logo. Nothing is ever drawn or generated in its place.</item>
/// </list>
/// Cache: <c>&lt;directory&gt;/&lt;hash of the name&gt;.png</c>; an artist the services answered
/// for without a logo leaves a <c>.miss</c> marker for <see cref="DefaultMissRetryAfter"/>
/// (artists gain logos over time); a lookup that got no answer leaves nothing, so the next
/// visit asks again. One lookup per name at a time, however many ask. Keys travel only in the
/// requests, never in a log line or an exception message from here.
/// </summary>
public sealed class ArtistLogoService
{
    public const string FanartUrl = "https://webservice.fanart.tv/v3/music/";
    public const string TheAudioDbUrl = "https://www.theaudiodb.com/api/v1/json/";
    public const string MusicBrainzUrl = "https://musicbrainz.org/ws/2/";

    /// <summary>How long one lookup (every request and the download) may take.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(15);

    /// <summary>How long an artist without a logo is left alone.</summary>
    public static readonly TimeSpan DefaultMissRetryAfter = TimeSpan.FromDays(14);

    /// <summary>MusicBrainz's rate rule: one request a second (a little over, to be safe).</summary>
    public static readonly TimeSpan MusicBrainzSpacing = TimeSpan.FromMilliseconds(1100);

    /// <summary>The lowest MusicBrainz search score a same-name hit may have.</summary>
    public const int MinMusicBrainzScore = 90;

    /// <summary>How many of the library's titles the same-name check sends.</summary>
    public const int VerifyTitleLimit = 4;

    private static readonly string UserAgent =
        $"Noctis/{typeof(ArtistLogoService).Assembly.GetName().Version?.ToString(3) ?? "1.0"} (https://github.com/heartached/Noctis)";

    private readonly HttpClient _http;
    private readonly string _directory;
    private readonly ArtistLogoKeys _keys;
    private readonly Func<string, IEnumerable<string>>? _libraryTitles;
    private readonly Func<byte[], byte[]?>? _prepare;
    private readonly TimeSpan _timeout, _missRetryAfter, _musicBrainzSpacing;
    private readonly Func<DateTime> _utcNow;
    private readonly SemaphoreSlim _musicBrainzGate = new(1, 1);
    private readonly SemaphoreSlim _lookupGate = new(1, 1);
    private DateTime _lastMusicBrainzCall = DateTime.MinValue;
    private readonly object _lock = new();
    private readonly Dictionary<string, Task<string?>> _pending = new(StringComparer.Ordinal);
    private HashSet<string>? _logos;
    private Dictionary<string, DateTime>? _misses;

    /// <param name="http">Client for every request; each call also takes a token.</param>
    /// <param name="directory">The cache folder (created on the first save).</param>
    /// <param name="keys">The keys; with none, <see cref="IsEnabled"/> is false and nothing is asked.</param>
    /// <param name="libraryTitles">The library's song titles for a name, to tell same-name
    /// artists apart (asked only then).</param>
    /// <param name="prepare">Runs on a downloaded logo before it is saved (the phone trims its
    /// transparent margins); null from it saves the original.</param>
    public ArtistLogoService(HttpClient http, string directory, ArtistLogoKeys keys,
        Func<string, IEnumerable<string>>? libraryTitles = null, Func<byte[], byte[]?>? prepare = null,
        TimeSpan? timeout = null, TimeSpan? missRetryAfter = null, TimeSpan? musicBrainzSpacing = null, Func<DateTime>? utcNow = null)
    {
        _http = http;
        _directory = directory;
        _keys = keys;
        _libraryTitles = libraryTitles;
        _prepare = prepare;
        _timeout = timeout ?? DefaultTimeout;
        _missRetryAfter = missRetryAfter ?? DefaultMissRetryAfter;
        _musicBrainzSpacing = musicBrainzSpacing ?? MusicBrainzSpacing;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>Whether any logo service has a key: without one nothing is ever asked.</summary>
    public bool IsEnabled => !_keys.IsEmpty;

    /// <summary>The cache file name for an artist: a hash of the trimmed, lower-cased name.</summary>
    public static string Key(string artistName)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(artistName.Trim().ToLowerInvariant()));
        return Convert.ToHexString(hash, 0, 16).ToLowerInvariant();
    }

    internal string LogoPath(string key) => Path.Combine(_directory, key + ".png");
    internal string MissPath(string key) => Path.Combine(_directory, key + ".miss");

    private static bool IsAskable(string artistName) =>
        !string.IsNullOrWhiteSpace(artistName)
        && !string.Equals(artistName.Trim(), "Unknown Artist", StringComparison.OrdinalIgnoreCase)
        && !string.Equals(artistName.Trim(), "Various Artists", StringComparison.OrdinalIgnoreCase);

    /// <summary>The logo already on disk for the artist, or null. Never asks the network (and
    /// answers even without keys: a logo fetched once stays).</summary>
    public string? CachedLogo(string artistName)
    {
        if (!IsAskable(artistName)) return null;
        var key = Key(artistName);
        lock (_lock)
        {
            EnsureIndex();
            return _logos!.Contains(key) ? LogoPath(key) : null;
        }
    }

    /// <summary>
    /// The artist's logo file: the cached one, else a lookup. Null when there is none, no key is
    /// set, the artist could not be told apart from a namesake, or the lookup failed or was slow;
    /// never throws for a failed lookup. Cancelling stops waiting (it may throw
    /// <see cref="OperationCanceledException"/>); a lookup already under way finishes and is
    /// cached for the next visit.
    /// </summary>
    public async Task<string?> GetLogoAsync(string artistName, CancellationToken ct = default)
    {
        if (!IsAskable(artistName)) return null;
        ct.ThrowIfCancellationRequested();
        var key = Key(artistName);
        Task<string?> task;
        lock (_lock)
        {
            EnsureIndex();
            if (_logos!.Contains(key)) return LogoPath(key);
            if (!IsEnabled) return null;
            if (_misses!.TryGetValue(key, out var missed) && _utcNow() - missed < _missRetryAfter) return null;
            if (!_pending.TryGetValue(key, out task!))
            {
                task = Task.Run(() => FetchAsync(artistName.Trim(), key));
                _pending[key] = task;
            }
        }
        return await task.WaitAsync(ct).ConfigureAwait(false);
    }

    private async Task<string?> FetchAsync(string artistName, string key)
    {
        try
        {
            // One artist at a time: MusicBrainz's pace is shared anyway, and a page opened twice
            // asks once.
            await _lookupGate.WaitAsync().ConfigureAwait(false);
            try
            {
                using var timeout = new CancellationTokenSource(_timeout);
                var found = await FindAsync(artistName, timeout.Token).ConfigureAwait(false);
                if (found.Bytes is not { } bytes)
                {
                    if (found.Answered) RecordMiss(key);
                    return null;
                }
                return await SaveAsync(key, bytes).ConfigureAwait(false);
            }
            finally
            {
                _lookupGate.Release();
            }
        }
        catch (OperationCanceledException)
        {
            DebugLog.Write("Artist", "Artist logo lookup timed out");
            return null;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Artist logo lookup failed: {Scrub(ex.Message)}");
            return null;
        }
        finally
        {
            lock (_lock) _pending.Remove(key);
        }
    }

    /// <summary>A lookup's outcome: the logo's bytes, or null; <see cref="Answered"/> false when
    /// some service gave no usable answer, so "no logo" is not remembered.</summary>
    internal readonly record struct Found(byte[]? Bytes, bool Answered);

    internal async Task<Found> FindAsync(string artistName, CancellationToken ct)
    {
        var answered = true;
        var mb = await ResolveMbidAsync(artistName, ct).ConfigureAwait(false);
        string? mbid = mb.Mbid;
        if (!mb.Answered) answered = false;

        string? audioDbUrl = null;
        if (mbid == null && !mb.Answered && _keys.HasTheAudioDb)
        {
            // MusicBrainz gave no answer: TheAudioDB's own name search, exact name only.
            var byName = await TheAudioDbByNameAsync(artistName, ct).ConfigureAwait(false);
            if (byName.Answered) answered = true;
            mbid = byName.Mbid;
            audioDbUrl = byName.LogoUrl;
        }
        if (mbid == null && audioDbUrl == null) return new Found(null, answered);

        if (mbid != null && _keys.HasFanart)
        {
            var fanart = await FanartLogoUrlsAsync(mbid, ct).ConfigureAwait(false);
            if (!fanart.Answered) answered = false;
            foreach (var url in fanart.Urls)
                if (await DownloadPngAsync(url, ct).ConfigureAwait(false) is { } bytes) return new Found(bytes, true);
        }
        if (audioDbUrl == null && mbid != null && _keys.HasTheAudioDb)
        {
            var byId = await TheAudioDbByMbidAsync(mbid, ct).ConfigureAwait(false);
            if (!byId.Answered) answered = false;
            audioDbUrl = byId.LogoUrl;
        }
        if (audioDbUrl != null && await DownloadPngAsync(audioDbUrl, ct).ConfigureAwait(false) is { } audioDbBytes)
            return new Found(audioDbBytes, true);
        return new Found(null, answered);
    }

    // ---- MusicBrainz --------------------------------------------------------------------------

    internal readonly record struct MbidResult(string? Mbid, bool Answered);

    /// <summary>The artist's MusicBrainz id: the only same-name hit, or the one MusicBrainz credits
    /// with the library's titles; null when none or when namesakes cannot be told apart.</summary>
    internal async Task<MbidResult> ResolveMbidAsync(string artistName, CancellationToken ct)
    {
        var url = $"{MusicBrainzUrl}artist/?query={Uri.EscapeDataString("artist:" + Quote(artistName))}&fmt=json&limit=10";
        var json = await MusicBrainzGetAsync(url, ct).ConfigureAwait(false);
        if (json == null) return new MbidResult(null, false);
        var hits = SameNameArtists(json, artistName);
        if (hits.Count == 0) return new MbidResult(null, true);
        if (hits.Count == 1) return new MbidResult(hits[0], true);

        var titles = LibraryTitles(artistName);
        if (titles.Count == 0) return new MbidResult(null, true);
        var query = "artist:" + Quote(artistName) + " AND (" + string.Join(" OR ", titles.Select(t => "recording:" + Quote(t))) + ")";
        var recordings = await MusicBrainzGetAsync($"{MusicBrainzUrl}recording/?query={Uri.EscapeDataString(query)}&fmt=json&limit=50", ct).ConfigureAwait(false);
        if (recordings == null) return new MbidResult(null, false);
        return new MbidResult(PickCredited(recordings, hits, titles), true);
    }

    /// <summary>The ids of the search hits named <paramref name="artistName"/> (or with it as an
    /// alias), diacritics and case folded, scored at least <see cref="MinMusicBrainzScore"/>, in
    /// MusicBrainz's order.</summary>
    internal static List<string> SameNameArtists(string json, string artistName)
    {
        var ids = new List<string>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return ids;
        var wanted = NameKey(artistName);
        foreach (var artist in artists.EnumerateArray())
        {
            if (Str(artist, "id") is not { Length: > 0 } id) continue;
            var score = artist.TryGetProperty("score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt32() : 0;
            if (score < MinMusicBrainzScore) continue;
            var named = NameKey(Str(artist, "name")) == wanted;
            if (!named && artist.TryGetProperty("aliases", out var aliases) && aliases.ValueKind == JsonValueKind.Array)
                named = aliases.EnumerateArray().Any(a => NameKey(Str(a, "name")) == wanted);
            if (named && !ids.Contains(id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>The candidate credited with the most of the library's titles; null on a tie or none.</summary>
    internal static string? PickCredited(string recordingsJson, IReadOnlyList<string> candidates, IReadOnlyCollection<string> titles)
    {
        var titleKeys = new HashSet<string>(titles.Select(DeezerArtistPhotos.TitleKey).Where(k => k.Length > 0), StringComparer.Ordinal);
        var counts = candidates.ToDictionary(c => c, _ => new HashSet<string>(StringComparer.Ordinal), StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(recordingsJson);
        if (!doc.RootElement.TryGetProperty("recordings", out var recordings) || recordings.ValueKind != JsonValueKind.Array) return null;
        foreach (var recording in recordings.EnumerateArray())
        {
            var title = DeezerArtistPhotos.TitleKey(Str(recording, "title"));
            if (!titleKeys.Contains(title)) continue;
            if (!recording.TryGetProperty("artist-credit", out var credit) || credit.ValueKind != JsonValueKind.Array) continue;
            foreach (var part in credit.EnumerateArray())
                if (part.TryGetProperty("artist", out var a) && Str(a, "id") is { } id && counts.TryGetValue(id, out var set))
                    set.Add(title);
        }
        var ranked = counts.OrderByDescending(kv => kv.Value.Count).ToList();
        if (ranked[0].Value.Count == 0) return null;
        if (ranked.Count > 1 && ranked[1].Value.Count == ranked[0].Value.Count) return null;
        return ranked[0].Key;
    }

    private IReadOnlyList<string> LibraryTitles(string artistName)
    {
        if (_libraryTitles == null) return Array.Empty<string>();
        try
        {
            return _libraryTitles(artistName)
                .Where(t => DeezerArtistPhotos.TitleKey(t).Length >= 3)
                .Select(t => StripSuffix(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(VerifyTitleLimit)
                .ToList();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Reading the artist's titles failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>"Song (feat. X)" → "Song": MusicBrainz titles the recording without the credit.</summary>
    private static string StripSuffix(string title)
    {
        var cut = title.IndexOfAny(new[] { '(', '[' });
        return (cut > 0 ? title[..cut] : title).Trim();
    }

    /// <summary>A MusicBrainz GET at its pace, with the User-Agent it requires; null for no
    /// usable answer (refused, rate limited, server error).</summary>
    private async Task<string?> MusicBrainzGetAsync(string url, CancellationToken ct)
    {
        await _musicBrainzGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _lastMusicBrainzCall + _musicBrainzSpacing - _utcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, ct).ConfigureAwait(false);
            _lastMusicBrainzCall = _utcNow();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.UserAgent.ParseAdd(UserAgent);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                DebugLog.Write("Artist", $"MusicBrainz answered {(int)response.StatusCode} for an artist logo lookup");
                return null;
            }
            return await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
        }
        finally
        {
            _musicBrainzGate.Release();
        }
    }

    // ---- fanart.tv ----------------------------------------------------------------------------

    internal readonly record struct LogoUrls(IReadOnlyList<string> Urls, bool Answered);

    private async Task<LogoUrls> FanartLogoUrlsAsync(string mbid, CancellationToken ct)
    {
        var url = $"{FanartUrl}{Uri.EscapeDataString(mbid)}?api_key={Uri.EscapeDataString(_keys.FanartProjectKey!)}";
        if (!string.IsNullOrWhiteSpace(_keys.FanartClientKey)) url += $"&client_key={Uri.EscapeDataString(_keys.FanartClientKey)}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        // fanart.tv answers 404 for an artist it has no images of: an answer.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return new LogoUrls(Array.Empty<string>(), true);
        if (!response.IsSuccessStatusCode)
        {
            DebugLog.Write("Artist", $"fanart.tv answered {(int)response.StatusCode} for an artist logo lookup");
            return new LogoUrls(Array.Empty<string>(), false);
        }
        var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
        return new LogoUrls(FanartLogoUrls(json), true);
    }

    /// <summary>fanart.tv's logos for an artist, best first: the HD logos (800×310) most liked
    /// first, then the older small ones. A logo in another language than English (or none) goes
    /// after the others of its kind.</summary>
    internal static IReadOnlyList<string> FanartLogoUrls(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var urls = new List<string>();
        foreach (var kind in new[] { "hdmusiclogo", "musiclogo" })
        {
            if (!doc.RootElement.TryGetProperty(kind, out var list) || list.ValueKind != JsonValueKind.Array) continue;
            urls.AddRange(list.EnumerateArray()
                .Select(e => (Url: Str(e, "url"), Likes: int.TryParse(Str(e, "likes"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l) ? l : 0,
                    Lang: Str(e, "lang")))
                .Where(e => IsHttps(e.Url))
                .OrderBy(e => e.Lang is null or "" or "en" or "00" ? 0 : 1)
                .ThenByDescending(e => e.Likes)
                .Select(e => e.Url!));
        }
        return urls;
    }

    // ---- TheAudioDB ---------------------------------------------------------------------------

    internal readonly record struct AudioDbResult(string? Mbid, string? LogoUrl, bool Answered);

    private Task<AudioDbResult> TheAudioDbByMbidAsync(string mbid, CancellationToken ct) =>
        TheAudioDbAsync($"artist-mb.php?i={Uri.EscapeDataString(mbid)}", null, ct);

    private Task<AudioDbResult> TheAudioDbByNameAsync(string artistName, CancellationToken ct) =>
        TheAudioDbAsync($"search.php?s={Uri.EscapeDataString(artistName)}", artistName, ct);

    private async Task<AudioDbResult> TheAudioDbAsync(string query, string? exactName, CancellationToken ct)
    {
        var url = $"{TheAudioDbUrl}{Uri.EscapeDataString(_keys.TheAudioDbKey!)}/{query}";
        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            DebugLog.Write("Artist", $"TheAudioDB answered {(int)response.StatusCode} for an artist logo lookup");
            return new AudioDbResult(null, null, false);
        }
        var json = await HttpSafety.ReadStringBoundedAsync(response.Content, ct: ct).ConfigureAwait(false);
        var (mbid, logo) = TheAudioDbArtist(json, exactName);
        return new AudioDbResult(mbid, logo, true);
    }

    /// <summary>TheAudioDB's answer: the artist's MusicBrainz id and logo URL. With
    /// <paramref name="exactName"/> (a name search) only an artist of exactly that name (or alternate
    /// name) counts.</summary>
    internal static (string? Mbid, string? LogoUrl) TheAudioDbArtist(string json, string? exactName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("artists", out var artists) || artists.ValueKind != JsonValueKind.Array) return (null, null);
        var wanted = exactName == null ? null : NameKey(exactName);
        foreach (var artist in artists.EnumerateArray())
        {
            if (wanted != null)
            {
                var names = new[] { Str(artist, "strArtist") }
                    .Concat((Str(artist, "strArtistAlternate") ?? "").Split(','));
                if (!names.Any(n => NameKey(n) == wanted)) continue;
            }
            var logo = Str(artist, "strArtistLogo");
            return (Str(artist, "strMusicBrainzID") is { Length: > 0 } id ? id : null, IsHttps(logo) ? logo : null);
        }
        return (null, null);
    }

    // ---- Download and cache -------------------------------------------------------------------

    /// <summary>The logo's bytes when it is a PNG within the image bound; null otherwise (a
    /// logo without transparency would draw a box on the page).</summary>
    private async Task<byte[]?> DownloadPngAsync(string url, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var bytes = await HttpSafety.ReadBytesBoundedAsync(response.Content, HttpSafety.MaxImageBytes, ct).ConfigureAwait(false);
        return IsPng(bytes) ? bytes : null;
    }

    public static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length > 8 && data[0] == 0x89 && data[1] == 0x50 && data[2] == 0x4E && data[3] == 0x47
        && data[4] == 0x0D && data[5] == 0x0A && data[6] == 0x1A && data[7] == 0x0A;

    /// <summary>Written through a temporary file and a rename, so a page decoding the logo never
    /// reads half a PNG.</summary>
    private async Task<string> SaveAsync(string key, byte[] bytes)
    {
        if (_prepare != null)
        {
            try
            {
                if (_prepare(bytes) is { } prepared && IsPng(prepared)) bytes = prepared;
            }
            catch (Exception ex)
            {
                DebugLog.Write("Artist", $"Preparing an artist logo failed: {ex.Message}");
            }
        }
        Directory.CreateDirectory(_directory);
        var path = LogoPath(key);
        var tmp = path + ".tmp";
        await File.WriteAllBytesAsync(tmp, bytes).ConfigureAwait(false);
        File.Move(tmp, path, overwrite: true);
        TryDelete(MissPath(key));
        lock (_lock)
        {
            EnsureIndex();
            _logos!.Add(key);
            _misses!.Remove(key);
        }
        return path;
    }

    private void RecordMiss(string key)
    {
        var now = _utcNow();
        try
        {
            Directory.CreateDirectory(_directory);
            var path = MissPath(key);
            File.WriteAllText(path, string.Empty);
            File.SetLastWriteTimeUtc(path, now);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Recording an artist logo miss failed: {ex.Message}");
        }
        lock (_lock)
        {
            EnsureIndex();
            _misses![key] = now;
        }
    }

    /// <summary>Reads the cache folder once (under <see cref="_lock"/>).</summary>
    private void EnsureIndex()
    {
        if (_logos != null) return;
        var logos = new HashSet<string>(StringComparer.Ordinal);
        var misses = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(_directory))
            {
                foreach (var file in Directory.EnumerateFiles(_directory))
                {
                    var name = Path.GetFileNameWithoutExtension(file);
                    switch (Path.GetExtension(file))
                    {
                        case ".png": logos.Add(name); break;
                        case ".miss": misses[name] = File.GetLastWriteTimeUtc(file); break;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            DebugLog.Write("Artist", $"Reading the artist logo cache failed: {ex.Message}");
        }
        _logos = logos;
        _misses = misses;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // A stale marker only delays a retry.
        }
    }

    // ---- Helpers ------------------------------------------------------------------------------

    /// <summary>A name as compared: diacritics folded, case and spacing ignored, "&amp;" and
    /// "and" the same.</summary>
    internal static string NameKey(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var folded = DeezerArtistPhotos.FoldDiacritics(name.Trim()).ToLowerInvariant().Replace("&", " and ");
        return string.Concat(folded.Where(char.IsLetterOrDigit));
    }

    /// <summary>A Lucene phrase: quotes and backslashes escaped.</summary>
    private static string Quote(string text) => "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool IsHttps(string? url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == Uri.UriSchemeHttps;

    /// <summary>A message with the keys taken out, should a handler ever quote a URL.</summary>
    private string Scrub(string message)
    {
        foreach (var secret in new[] { _keys.FanartProjectKey, _keys.FanartClientKey, _keys.TheAudioDbKey })
            if (!string.IsNullOrWhiteSpace(secret)) message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
        return LogRedaction.Scrub(message);
    }
}
