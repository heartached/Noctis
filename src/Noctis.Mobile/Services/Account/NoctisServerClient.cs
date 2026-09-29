using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Noctis.Services;

namespace Noctis.Mobile.Services.Account;

/// <summary>One song from the desktop's search3 catalog, already validated and capped.</summary>
internal sealed record RemoteSong(
    Guid Id, Guid? AlbumId, string Title, string Album, string Artist, int DurationSeconds, long Size,
    string Suffix, int Track, int Disc, int Year, string Genre, int BitRate, int SamplingRate,
    int UserRating, bool Starred, int PlayCount, DateTime? Created);

internal sealed record SignInResult(string ApiKey, string? User, string? Server, bool SyncEnabled);

/// <summary>One ledger item as pulled. The payload stays raw JSON: callers validate it field by field.</summary>
internal sealed record PulledItem(string Kind, string Id, JsonElement Payload, DateTime UpdatedUtc, string Device, long Seq);

internal sealed record SyncPage(long Seq, bool More, IReadOnlyList<PulledItem> Items);

internal sealed record PushItem(string Kind, string Id, object Payload, DateTime UpdatedUtc);

/// <summary>
/// The phone's HTTP link to one desktop Noctis Server. Every request goes through a handler
/// from the platform's <see cref="NoctisHandlerFactory"/> whose certificate check accepts only
/// the pinned leaf (SHA-256 of its DER bytes, compared in constant time; chain and name errors
/// are irrelevant — the pin is the trust). The device key travels in the
/// <c>X-Noctis-Key</c> header, never in a URL. Server errors come back as
/// <see cref="NoctisServerException"/> kinds; nothing here logs a URL with a query, a key, a
/// password or an HttpClient exception message.
/// </summary>
internal sealed class NoctisServerClient : IDisposable
{
    public const string ClientName = "NoctisAndroid";
    public const string ApiVersion = "1.16.1";
    public const string KeyHeader = "X-Noctis-Key";
    public const int CoverSize = 512;
    private const int PageSize = 500;
    private const int MaxPages = 2000;
    private const int MaxStringLength = 500;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);

    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string? _key;
    private int _pinRejections;

    /// <param name="baseUrl">Already normalised by <see cref="NormalizeServerUrl"/>.</param>
    /// <param name="fingerprint">The pinned "AB:CD:…" fingerprint; ignored for plain-http loopback.</param>
    /// <param name="deviceKey">Null until signed in.</param>
    public NoctisServerClient(NoctisHandlerFactory handlerFactory, string baseUrl, string fingerprint, string? deviceKey)
    {
        _baseUrl = baseUrl;
        _key = deviceKey;
        var pin = baseUrl.StartsWith("https://", StringComparison.Ordinal) ? ParseFingerprint(fingerprint) : null;
        var handler = handlerFactory(cert =>
        {
            if (pin is not null && cert is not null && PinMatches(cert, pin)) return true;
            Interlocked.Increment(ref _pinRejections);
            return false;
        });
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string BaseUrl => _baseUrl;

    public void Dispose() => _http.Dispose();

    // ── Addresses and fingerprints ────────────────────────────────────────

    /// <summary>
    /// "host:port", "https://host:port/" → "https://host:port". https only, with a host and an
    /// optional port and nothing else (no path, query, fragment or user info); plain http is
    /// accepted for loopback hosts only (tests against a local Kestrel).
    /// </summary>
    public static string NormalizeServerUrl(string? input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0 || text.Length > 300) throw InvalidAddress();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) throw InvalidAddress();
        if (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw InvalidAddress();
        if (uri.AbsolutePath != "/" || text.EndsWith('?') || text.EndsWith('#')) throw InvalidAddress();
        if (string.IsNullOrWhiteSpace(uri.Host)) throw InvalidAddress();
        var https = uri.Scheme == Uri.UriSchemeHttps;
        if (!https && !(uri.Scheme == Uri.UriSchemeHttp && IsLoopback(uri))) throw InvalidAddress();
        return uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback || (IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));

    private static NoctisServerException InvalidAddress() =>
        new(NoctisErrorKind.InvalidAddress, "Enter the address shown in the desktop's Settings, like https://192.168.1.20:5443.");

    /// <summary>"AB:CD:…" upper-case SHA-256 of the certificate's DER bytes (same format as the desktop shows).</summary>
    public static string Fingerprint(X509Certificate2 cert)
        => string.Join(':', Convert.ToHexString(SHA256.HashData(cert.RawData)).Chunk(2).Select(c => new string(c)));

    /// <summary>The 32 bytes of an "AB:CD:…" fingerprint (separators and case are ignored).</summary>
    public static byte[] ParseFingerprint(string? fingerprint)
    {
        var hex = new string((fingerprint ?? string.Empty).Where(char.IsAsciiHexDigit).ToArray());
        var stripped = (fingerprint ?? string.Empty).Replace(":", "").Replace(" ", "");
        if (hex.Length != 64 || stripped.Length != 64)
            throw new NoctisServerException(NoctisErrorKind.CertificateChanged, "The certificate fingerprint is not valid.");
        return Convert.FromHexString(hex);
    }

    /// <summary>Canonical "AB:CD:…" form of a fingerprint the user confirmed.</summary>
    public static string NormalizeFingerprint(string? fingerprint)
        => string.Join(':', Convert.ToHexString(ParseFingerprint(fingerprint)).Chunk(2).Select(c => new string(c)));

    private static bool PinMatches(X509Certificate2 cert, byte[] pin)
    {
        try { return CryptographicOperations.FixedTimeEquals(SHA256.HashData(cert.RawData), pin); }
        catch { return false; }
    }

    /// <summary>
    /// First contact. Connects without credentials, accepting whatever certificate the server
    /// presents for this one request, and returns its fingerprint for the user to compare.
    /// The handler is private to this call, so the accept-any check never reaches a pinned
    /// connection. Plain-http loopback (tests) returns "".
    /// </summary>
    public static async Task<string> ProbeFingerprintAsync(NoctisHandlerFactory handlerFactory, string baseUrl, CancellationToken ct)
    {
        string? seen = null;
        var handler = handlerFactory(cert =>
        {
            if (cert is not null) seen = Fingerprint(cert);
            return true;
        });
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await http.GetAsync($"{baseUrl}/rest/ping.view?f=json&c={ClientName}&v={ApiVersion}",
                HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Log(new NoctisServerException(NoctisErrorKind.Server, "The server did not answer like a Noctis server."), "probe", (int)response.StatusCode);
            ParseEnvelope(body, signingIn: false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Log(Unreachable(), "probe", 0);
        }
        catch (HttpRequestException)
        {
            throw Log(Unreachable(), "probe", 0);
        }
        if (!baseUrl.StartsWith("https://", StringComparison.Ordinal)) return string.Empty;
        return seen ?? throw Log(new NoctisServerException(NoctisErrorKind.Server, "The server presented no certificate."), "probe", 0);
    }

    // ── Account ──────────────────────────────────────────────────────────

    /// <summary>noctisSignIn (form POST): password auth that returns this device's own key.</summary>
    public async Task<SignInResult> SignInAsync(string user, string password, string deviceId, string deviceName, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("u", user),
            new("p", "enc:" + Convert.ToHexString(Encoding.UTF8.GetBytes(password))),
            new("deviceId", deviceId),
            new("deviceName", deviceName),
        };
        var r = await PostFormAsync("noctisSignIn", form, signingIn: true, withKey: false, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("noctisSignIn", out var s) || s.ValueKind != JsonValueKind.Object)
            throw new NoctisServerException(NoctisErrorKind.Server, "This desktop's Noctis is too old to sign in phones. Update it.");
        var key = Str(s, "apiKey");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(c => c < 0x21 || c > 0x7E))
            throw new NoctisServerException(NoctisErrorKind.Server, "The server sent an unusable device key.");
        return new SignInResult(key, Cap(Str(s, "user"), 64), Cap(Str(s, "server"), 100), Bool(s, "syncEnabled"));
    }

    /// <summary>noctisSignOut: revokes this device's key.</summary>
    public Task SignOutAsync(CancellationToken ct) => PostFormAsync("noctisSignOut", new(), signingIn: false, withKey: true, ct);

    // ── Catalog ──────────────────────────────────────────────────────────

    /// <summary>Song count the server reports (getScanStatus), or -1 when it has none.</summary>
    public async Task<int> GetSongCountAsync(CancellationToken ct)
    {
        var r = await GetJsonAsync("getScanStatus", Array.Empty<KeyValuePair<string, string>>(), ct).ConfigureAwait(false);
        return r.TryGetProperty("scanStatus", out var s) && s.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : -1;
    }

    /// <summary>Every album's artist by album id (search3 with an empty query, paged).</summary>
    public async Task<Dictionary<Guid, string>> GetAlbumArtistsAsync(CancellationToken ct)
    {
        var result = new Dictionary<Guid, string>();
        for (var page = 0; page < MaxPages; page++)
        {
            var r = await GetJsonAsync("search3", new KeyValuePair<string, string>[]
            {
                new("query", ""), new("artistCount", "0"), new("songCount", "0"),
                new("albumCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                new("albumOffset", (page * PageSize).ToString(CultureInfo.InvariantCulture)),
            }, ct).ConfigureAwait(false);
            var albums = Items(r, "searchResult3", "album");
            foreach (var a in albums)
            {
                if (NoctisRemoteIds.TryParseServerId(Str(a, "id"), "al-", out var id))
                    result.TryAdd(id, Cap(Str(a, "artist"), MaxStringLength) ?? string.Empty);
            }
            if (albums.Count < PageSize) break;
        }
        return result;
    }

    /// <summary>Every song (search3 with an empty query, 500 per page). Invalid entries are skipped;
    /// a song listed twice (library changed between pages) is kept once.</summary>
    public async Task<List<RemoteSong>> GetAllSongsAsync(IProgress<int>? progress, CancellationToken ct)
    {
        var songs = new List<RemoteSong>();
        var seen = new HashSet<Guid>();
        for (var page = 0; page < MaxPages; page++)
        {
            var r = await GetJsonAsync("search3", new KeyValuePair<string, string>[]
            {
                new("query", ""), new("artistCount", "0"), new("albumCount", "0"),
                new("songCount", PageSize.ToString(CultureInfo.InvariantCulture)),
                new("songOffset", (page * PageSize).ToString(CultureInfo.InvariantCulture)),
            }, ct).ConfigureAwait(false);
            var entries = Items(r, "searchResult3", "song");
            foreach (var e in entries)
            {
                var song = ParseSong(e);
                if (song is not null && seen.Add(song.Id)) songs.Add(song);
            }
            progress?.Report(songs.Count);
            if (entries.Count < PageSize) break;
        }
        return songs;
    }

    internal static RemoteSong? ParseSong(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (!NoctisRemoteIds.TryParseServerId(Str(e, "id"), "tr-", out var id)) return null;
        Guid? albumId = NoctisRemoteIds.TryParseServerId(Str(e, "albumId") ?? Str(e, "parent"), "al-", out var al) ? al : null;
        var suffix = (Str(e, "suffix") ?? string.Empty).Trim().ToLowerInvariant();
        if (suffix.Length > 8 || !suffix.All(char.IsAsciiLetterOrDigit)) suffix = string.Empty;
        return new RemoteSong(
            id, albumId,
            Cap(Str(e, "title"), MaxStringLength) ?? string.Empty,
            Cap(Str(e, "album"), MaxStringLength) ?? string.Empty,
            Cap(Str(e, "artist"), MaxStringLength) ?? string.Empty,
            Math.Clamp(Int(e, "duration"), 0, 60 * 60 * 24),
            Math.Max(0, Long(e, "size")),
            suffix,
            Math.Clamp(Int(e, "track"), 0, 9999),
            Math.Clamp(Int(e, "discNumber"), 0, 999),
            Math.Clamp(Int(e, "year"), 0, 9999),
            Cap(Str(e, "genre"), MaxStringLength) ?? string.Empty,
            Math.Clamp(Int(e, "bitRate"), 0, 100_000),
            Math.Clamp(Int(e, "samplingRate"), 0, 10_000_000),
            Math.Clamp(Int(e, "userRating"), 0, 5),
            e.TryGetProperty("starred", out var st) && st.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined),
            Math.Max(0, Int(e, "playCount")),
            Date(e, "created"));
    }

    // ── Binary: covers and downloads ─────────────────────────────────────

    /// <summary>
    /// getCoverArt at <see cref="CoverSize"/> into <paramref name="destination"/> (written beside
    /// it, then moved over it). False when the server has no cover for the album.
    /// </summary>
    public async Task<bool> DownloadCoverAsync(Guid albumId, string destination, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = Request(HttpMethod.Get, "getCoverArt", new KeyValuePair<string, string>[]
        {
            new("id", NoctisRemoteIds.ToServerAlbumId(albumId)),
            new("size", CoverSize.ToString(CultureInfo.InvariantCulture)),
        });
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, "cover", timeout.Token, ct).ConfigureAwait(false);
        try
        {
            await ThrowIfEnvelopeAsync(response, "cover", timeout.Token, ct).ConfigureAwait(false);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Server)
        {
            return false; // 70 not found: the album has no art
        }
        var dir = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(dir);
        var tmp = destination + ".part";
        try
        {
            await using (var body = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false))
            await using (var file = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                await CopyCappedAsync(body, file, 32L * 1024 * 1024, timeout.Token).ConfigureAwait(false);
            if (new FileInfo(tmp).Length == 0) { File.Delete(tmp); return false; }
            File.Move(tmp, destination, overwrite: true);
            return true;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryDelete(tmp);
            throw Log(Unreachable(), "cover", 0);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            TryDelete(tmp);
            throw Log(Unreachable(), "cover", 0);
        }
    }

    /// <summary>
    /// Downloads a song's original file to <paramref name="partPath"/> (no timeout — the caller's
    /// token cancels). Returns the response content type so the caller can pick an extension.
    /// </summary>
    public async Task<string?> DownloadTrackAsync(Guid trackId, string partPath, CancellationToken ct)
    {
        using var request = Request(HttpMethod.Get, "download", new KeyValuePair<string, string>[]
        {
            new("id", NoctisRemoteIds.ToServerTrackId(trackId)),
        });
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, "download", ct, ct).ConfigureAwait(false);
        await ThrowIfEnvelopeAsync(response, "download", ct, ct).ConfigureAwait(false);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await body.CopyToAsync(file, 81920, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException && !ct.IsCancellationRequested)
        {
            throw Log(Unreachable(), "download", 0);
        }
        return response.Content.Headers.ContentType?.MediaType;
    }

    // ── Sync ledger and plays ────────────────────────────────────────────

    public async Task<SyncPage> GetSyncChangesAsync(long since, string deviceId, string deviceName, CancellationToken ct)
    {
        var r = await GetJsonAsync("getNoctisSyncChanges", new KeyValuePair<string, string>[]
        {
            new("since", Math.Max(0, since).ToString(CultureInfo.InvariantCulture)),
            new("device", deviceId),
            new("name", deviceName),
        }, ct).ConfigureAwait(false);
        if (!r.TryGetProperty("noctisSync", out var s) || s.ValueKind != JsonValueKind.Object)
            throw new NoctisServerException(NoctisErrorKind.Server, "The server sent an unreadable sync page.");
        var items = new List<PulledItem>();
        if (s.TryGetProperty("items", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var i in arr.EnumerateArray())
            {
                if (i.ValueKind != JsonValueKind.Object) continue;
                var kind = Str(i, "kind");
                var id = Str(i, "id");
                if (kind is null || id is null || !i.TryGetProperty("payload", out var payload)) continue;
                var updated = Date(i, "updatedUtc") ?? DateTime.MinValue;
                items.Add(new PulledItem(kind, id, payload.Clone(), updated, Str(i, "device") ?? string.Empty, Long(i, "seq")));
            }
        }
        // "more" arrives with servers that page the ledger; absent means false.
        return new SyncPage(Long(s, "seq"), Bool(s, "more"), items);
    }

    /// <summary>pushNoctisSyncChanges (JSON body). Returns how many items the server accepted as newer.</summary>
    public async Task<int> PushSyncChangesAsync(string deviceId, string deviceName, IReadOnlyList<PushItem> items, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            device = deviceId,
            name = deviceName,
            items = items.Select(i => new
            {
                kind = i.Kind,
                id = i.Id,
                payload = i.Payload,
                updatedUtc = i.UpdatedUtc.ToString("O", CultureInfo.InvariantCulture),
            }),
        }, Noctis.Services.Sync.SyncJson.Options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = Request(HttpMethod.Post, "pushNoctisSyncChanges", Array.Empty<KeyValuePair<string, string>>());
        request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, "push", timeout.Token, ct).ConfigureAwait(false);
        var r = await ReadEnvelopeAsync(response, "push", signingIn: false, timeout.Token, ct).ConfigureAwait(false);
        return r.TryGetProperty("noctisSync", out var s) ? Int(s, "applied") : 0;
    }

    /// <summary>scrobble (form POST, submission=true): each id paired with its play time in ms since the epoch.</summary>
    public Task ScrobbleAsync(IReadOnlyList<(Guid Id, long TimeMs)> plays, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("submission", "true") };
        foreach (var (id, time) in plays)
        {
            form.Add(new("id", NoctisRemoteIds.ToServerTrackId(id)));
            form.Add(new("time", time.ToString(CultureInfo.InvariantCulture)));
        }
        return PostFormAsync("scrobble", form, signingIn: false, withKey: true, ct);
    }

    // ── Plumbing ─────────────────────────────────────────────────────────

    private HttpRequestMessage Request(HttpMethod method, string endpoint, IEnumerable<KeyValuePair<string, string>> query, bool withKey = true)
    {
        var sb = new StringBuilder(_baseUrl).Append("/rest/").Append(endpoint).Append(".view?f=json&c=").Append(ClientName)
            .Append("&v=").Append(ApiVersion);
        foreach (var (k, v) in query)
            sb.Append('&').Append(Uri.EscapeDataString(k)).Append('=').Append(Uri.EscapeDataString(v));
        var request = new HttpRequestMessage(method, sb.ToString());
        // The key goes in a header only, never in a URL (URLs end up in logs and caches).
        if (withKey && _key is not null)
            request.Headers.TryAddWithoutValidation(KeyHeader, _key);
        return request;
    }

    private async Task<JsonElement> GetJsonAsync(string endpoint, IEnumerable<KeyValuePair<string, string>> query, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = Request(HttpMethod.Get, endpoint, query);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, endpoint, timeout.Token, ct).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, endpoint, signingIn: false, timeout.Token, ct).ConfigureAwait(false);
    }

    private async Task<JsonElement> PostFormAsync(string endpoint, List<KeyValuePair<string, string>> form, bool signingIn, bool withKey, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = Request(HttpMethod.Post, endpoint, Array.Empty<KeyValuePair<string, string>>(), withKey);
        request.Content = new FormUrlEncodedContent(form);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, endpoint, timeout.Token, ct).ConfigureAwait(false);
        return await ReadEnvelopeAsync(response, endpoint, signingIn, timeout.Token, ct).ConfigureAwait(false);
    }

    /// <summary>Sends, mapping transport failures: a request the pin check refused is
    /// <see cref="NoctisErrorKind.CertificateChanged"/>, any other failure or a timeout is
    /// <see cref="NoctisErrorKind.Unreachable"/>, 429 is <see cref="NoctisErrorKind.LockedOut"/>.</summary>
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, HttpCompletionOption option, string what,
        CancellationToken token, CancellationToken callerToken)
    {
        var rejectionsBefore = Volatile.Read(ref _pinRejections);
        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, option, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw Log(Unreachable(), what, 0);
        }
        catch (HttpRequestException)
        {
            if (Volatile.Read(ref _pinRejections) != rejectionsBefore)
                throw Log(new NoctisServerException(NoctisErrorKind.CertificateChanged,
                    "The desktop's certificate changed. Sign out and sign in again to trust the new one."), what, 0);
            throw Log(Unreachable(), what, 0);
        }
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
        {
            var retry = response.Headers.RetryAfter?.Delta;
            response.Dispose();
            throw Log(new NoctisServerException(NoctisErrorKind.LockedOut, retry is { } d
                ? $"Too many failed sign-ins. Try again in {Math.Max(1, (int)Math.Ceiling(d.TotalMinutes))} min."
                : "Too many failed sign-ins. Try again later."), what, 429);
        }
        return response;
    }

    private static async Task<JsonElement> ReadEnvelopeAsync(HttpResponseMessage response, string what, bool signingIn,
        CancellationToken token, CancellationToken callerToken)
    {
        string body;
        try
        {
            body = await response.Content.ReadAsStringAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !callerToken.IsCancellationRequested))
        {
            throw Log(Unreachable(), what, (int)response.StatusCode);
        }
        if (!response.IsSuccessStatusCode)
            throw Log(new NoctisServerException(NoctisErrorKind.Server, $"The server answered HTTP {(int)response.StatusCode}."), what, (int)response.StatusCode);
        try
        {
            return ParseEnvelope(body, signingIn);
        }
        catch (NoctisServerException ex)
        {
            throw Log(ex, what, (int)response.StatusCode);
        }
    }

    /// <summary>Binary endpoints answer errors as a JSON envelope with status 200: turn those into exceptions.</summary>
    private static async Task ThrowIfEnvelopeAsync(HttpResponseMessage response, string what, CancellationToken token,
        CancellationToken callerToken)
    {
        var type = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        var isEnvelope = type.Contains("json", StringComparison.OrdinalIgnoreCase) || type.Contains("xml", StringComparison.OrdinalIgnoreCase);
        if (response.IsSuccessStatusCode && !isEnvelope) return;
        await ReadEnvelopeAsync(response, what, signingIn: false, token, callerToken).ConfigureAwait(false);
        // An "ok" envelope where a file was expected.
        throw Log(new NoctisServerException(NoctisErrorKind.Server, "The server sent no file."), what, (int)response.StatusCode);
    }

    internal static JsonElement ParseEnvelope(string body, bool signingIn)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(body);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new NoctisServerException(NoctisErrorKind.Server, "The server did not answer like a Noctis server.");
        }
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("subsonic-response", out var r) || r.ValueKind != JsonValueKind.Object)
            throw new NoctisServerException(NoctisErrorKind.Server, "The server did not answer like a Noctis server.");
        if (Str(r, "status") == "ok") return r;
        var code = r.TryGetProperty("error", out var err) ? Int(err, "code") : 0;
        throw MapError(code, signingIn);
    }

    internal static NoctisServerException MapError(int code, bool signingIn) => code switch
    {
        40 or 41 when signingIn => new(NoctisErrorKind.BadCredentials, "Wrong user name or password."),
        40 or 41 => new(NoctisErrorKind.SignedOut, "This phone was signed out on the desktop. Sign in again."),
        50 => new(NoctisErrorKind.SyncDisabled, "Sync is turned off on the desktop (Settings → Account & Devices)."),
        _ => new(NoctisErrorKind.Server, $"The server refused the request (error {code})."),
    };

    private static NoctisServerException Unreachable() =>
        new(NoctisErrorKind.Unreachable, "Can't reach the desktop. Check that it is on, Noctis Server is running, and both are on the same network.");

    /// <summary>Kind and status only: messages from HttpClient can carry the URL.</summary>
    private static NoctisServerException Log(NoctisServerException ex, string what, int status)
    {
        DebugLog.Write("Account", $"{what}: {ex.Kind}{(status > 0 ? $" (HTTP {status})" : "")}");
        return ex;
    }

    private static async Task CopyCappedAsync(Stream source, Stream destination, long cap, CancellationToken ct)
    {
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > cap) throw new IOException("Response too large.");
            await destination.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }

    internal static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* best effort */ }
    }

    // ── JSON field readers (lenient: the server writes numbers, some clients strings) ──

    private static List<JsonElement> Items(JsonElement r, string container, string name)
    {
        if (!r.TryGetProperty(container, out var c) || c.ValueKind != JsonValueKind.Object) return new();
        if (!c.TryGetProperty(name, out var a) || a.ValueKind != JsonValueKind.Array) return new();
        return a.EnumerateArray().ToList();
    }

    internal static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    internal static int Int(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number)
            return v.TryGetInt32(out var i) ? i : v.TryGetDouble(out var d) ? (int)Math.Clamp(d, int.MinValue, int.MaxValue) : 0;
        return v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    internal static long Long(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number) return v.TryGetInt64(out var l) ? l : 0;
        return v.ValueKind == JsonValueKind.String && long.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
    }

    internal static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
        && (v.ValueKind == JsonValueKind.True || (v.ValueKind == JsonValueKind.String && bool.TryParse(v.GetString(), out var b) && b));

    internal static DateTime? Date(JsonElement e, string name)
    {
        var s = Str(e, name);
        if (s is null || !DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var d)) return null;
        return d.Kind switch
        {
            DateTimeKind.Utc => d,
            DateTimeKind.Local => d.ToUniversalTime(),
            _ => DateTime.SpecifyKind(d, DateTimeKind.Utc),
        };
    }

    internal static string? Cap(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
