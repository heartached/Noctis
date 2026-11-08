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

internal sealed record SignInResult(string ApiKey, string? User, string? Server, bool SyncEnabled, string? DisplayName = null);

/// <summary>What noctisUploadTrack did with a song: added it, or found the same file already there.</summary>
internal enum UploadOutcome { Added, AlreadyThere }

/// <summary>A local song as noctisHasTracks compares it with the desktop's library.</summary>
internal sealed record SongIdentity(string Title, string Artist, int DurationSeconds);

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
internal sealed partial class NoctisServerClient : IDisposable
{
    public const string ClientName = "NoctisAndroid";
    public const string ApiVersion = "1.16.1";
    public const string KeyHeader = "X-Noctis-Key";
    public const int CoverSize = 512;
    private const int PageSize = 500;
    private const int MaxPages = 2000;
    private const int MaxStringLength = 500;
    private static readonly TimeSpan ApiTimeout = TimeSpan.FromSeconds(15);
    // A catalog page is ~0.3 MB and a sync page at most 4 MB; parsing builds several copies,
    // so the cap bounds what a misbehaving desktop can make the phone allocate.
    private const long MaxEnvelopeBytes = 16L * 1024 * 1024;
    private const long MaxProbeBytes = 64L * 1024;
    private const long MaxErrorEnvelopeBytes = 1024L * 1024;
    /// <summary>A download re-checks the phone's free space after this many bytes.</summary>
    private const long RoomCheckBytes = 8L * 1024 * 1024;

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
        // Buffered reads (the JSON envelopes) are capped: a catalog page is well under 1 MB, and a
        // hostile answer must not buffer gigabytes. Streamed bodies (songs, covers) cap themselves.
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxEnvelopeBytes };
    }

    public string BaseUrl => _baseUrl;

    /// <summary>A download that receives nothing for this long has lost its link (a dropped Wi-Fi
    /// delivers nothing and raises nothing): it fails as Unreachable instead of waiting forever.</summary>
    public TimeSpan StallTimeout { get; init; } = DefaultStallTimeout;
    public static readonly TimeSpan DefaultStallTimeout = TimeSpan.FromSeconds(30);

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
        // The probe trusts any certificate, so whoever answers is unvetted: a ping answer is tiny.
        using var http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan, MaxResponseContentBufferSize = MaxProbeBytes };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        try
        {
            using var response = await http.GetAsync($"{baseUrl}/rest/ping.view?f=json&c={ClientName}&v={ApiVersion}",
                HttpCompletionOption.ResponseContentRead, timeout.Token).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Log(NotNoctis(), "probe", (int)response.StatusCode);
            ParseEnvelope(body, signingIn: false);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Server)
        {
            // Whatever answered is not a Noctis server: the page says check the address and
            // port, not "try again in a moment".
            throw Log(NotNoctis(), "probe", 0);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Log(Unreachable(), "probe", 0);
        }
        catch (HttpRequestException ex)
        {
            throw Log(ConnectFailure(ex), "probe", 0);
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
        return ReadSignIn(r);
    }

    /// <summary>noctisPairSignIn (form POST): the QR code's one-time secret, alone, for this
    /// device's own key. Only ever called on a client pinned to the code's fingerprint.</summary>
    public async Task<SignInResult> PairSignInAsync(string secret, string deviceId, string deviceName, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("code", secret),
            new("deviceId", deviceId),
            new("deviceName", deviceName),
        };
        var r = await PostFormAsync(PairSignInMethod, form, signingIn: true, withKey: false, ct).ConfigureAwait(false);
        return ReadSignIn(r);
    }

    /// <summary>The QR sign-in endpoint (the desktop's NoctisServer.PairSignInMethod).</summary>
    internal const string PairSignInMethod = "noctisPairSignIn";

    private static SignInResult ReadSignIn(JsonElement r)
    {
        if (!r.TryGetProperty("noctisSignIn", out var s) || s.ValueKind != JsonValueKind.Object)
            throw new NoctisServerException(NoctisErrorKind.Outdated, "This desktop's Noctis is too old to sign in phones. Update it.");
        var key = Str(s, "apiKey");
        if (string.IsNullOrWhiteSpace(key) || key.Length > 256 || key.Any(c => c < 0x21 || c > 0x7E))
            throw new NoctisServerException(NoctisErrorKind.Server, "The server sent an unusable device key.");
        return new SignInResult(key, Cap(Str(s, "user"), 64), Cap(Str(s, "server"), 100), Bool(s, "syncEnabled"),
            CleanDisplayName(Str(s, "displayName")));
    }

    /// <summary>getNoctisSyncStatus: the desktop's profile name (null when it has none) and the
    /// computer's name as sign-in gave it (null when it gives none). Answers with sync on or off;
    /// an older desktop without the profile name also gives null for it. Also where the computer
    /// sends the phone's plays on to (null: a computer that does not forward them).</summary>
    public async Task<(string? DisplayName, string? ServerName, ComputerScrobbling? Scrobbling)> GetDisplayNameAsync(CancellationToken ct)
    {
        var r = await GetJsonAsync("getNoctisSyncStatus", Array.Empty<KeyValuePair<string, string>>(), ct).ConfigureAwait(false);
        if (!r.TryGetProperty("noctisSync", out var s) || s.ValueKind != JsonValueKind.Object) return (null, null, null);
        var server = Cap(Str(s, "name"), 100);
        var scrobbling = s.TryGetProperty("scrobbling", out var sc) && sc.ValueKind == JsonValueKind.Object
            ? new ComputerScrobbling(Bool(sc, "lastFm"), Bool(sc, "listenBrainz"))
            : null;
        return (CleanDisplayName(Str(s, "displayName")), string.IsNullOrWhiteSpace(server) ? null : server, scrobbling);
    }

    /// <summary>A name from the desktop as shown in the header: control and format characters
    /// (bidi overrides, zero-width) dropped, trimmed, at most 64; null when nothing is left.</summary>
    internal static string? CleanDisplayName(string? raw)
    {
        if (raw is null) return null;
        var clean = new string(raw.Where(c => !char.IsControl(c)
            && char.GetUnicodeCategory(c) != UnicodeCategory.Format).ToArray()).Trim();
        if (clean.Length > 64) clean = clean[..64].TrimEnd();
        return clean.Length == 0 ? null : clean;
    }

    /// <summary>noctisSignOut: revokes this device's key.</summary>
    public Task SignOutAsync(CancellationToken ct) => PostFormAsync("noctisSignOut", new(), signingIn: false, withKey: true, ct);

    // ── Catalog ──────────────────────────────────────────────────────────

    /// <summary>Song count the server reports (getScanStatus), or -1 when it has none.</summary>
    public async Task<int> GetSongCountAsync(CancellationToken ct) => (await GetScanStatusAsync(ct).ConfigureAwait(false)).Count;

    /// <summary>getScanStatus: the song count (-1 when none) and whether a library scan is running
    /// (the catalog is then partial). Desktops before the flag always say false.</summary>
    public async Task<(int Count, bool Scanning)> GetScanStatusAsync(CancellationToken ct)
    {
        var r = await GetJsonAsync("getScanStatus", Array.Empty<KeyValuePair<string, string>>(), ct).ConfigureAwait(false);
        if (!r.TryGetProperty("scanStatus", out var s) || s.ValueKind != JsonValueKind.Object) return (-1, false);
        var count = s.TryGetProperty("count", out var c) && c.TryGetInt32(out var n) ? n : -1;
        return (count, Bool(s, "scanning"));
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
    public Task<bool> DownloadCoverAsync(Guid albumId, string destination, CancellationToken ct) =>
        DownloadImageAsync("getCoverArt", new KeyValuePair<string, string>[]
        {
            new("id", NoctisRemoteIds.ToServerAlbumId(albumId)),
            new("size", CoverSize.ToString(CultureInfo.InvariantCulture)),
        }, destination, "cover", ct);

    /// <summary>
    /// getAvatar: <paramref name="userName"/>'s profile picture (the desktop's) into
    /// <paramref name="destination"/>, written beside it then moved over it. False when the
    /// desktop has none, or is older than getAvatar (its "unknown method" is a server error too).
    /// </summary>
    public Task<bool> GetAvatarAsync(string userName, string destination, CancellationToken ct) =>
        DownloadImageAsync("getAvatar", new KeyValuePair<string, string>[] { new("username", userName) }, destination, "avatar", ct);

    /// <summary>
    /// getNoctisPlaylistCover: the computer's custom cover of a playlist into
    /// <paramref name="destination"/>. False when it has none, or is older than the call.
    /// </summary>
    public Task<bool> GetPlaylistCoverAsync(Guid playlistId, string destination, CancellationToken ct) =>
        DownloadImageAsync("getNoctisPlaylistCover", new KeyValuePair<string, string>[] { new("id", playlistId.ToString("N")) },
            destination, "playlist cover", ct);

    /// <summary>
    /// noctisUploadPlaylistCover: sends a playlist cover (a picture of at most
    /// <see cref="Noctis.Services.Sync.PlaylistCovers.MaxBytes"/>) for the playlist push that names its hash. Returns the
    /// hash the computer keeps it under. Throws <see cref="NoctisServerException"/> (Server) when the
    /// computer refuses it or is older than the call.
    /// </summary>
    public async Task<string> UploadPlaylistCoverAsync(string path, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        await using var source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var request = Request(HttpMethod.Post, "noctisUploadPlaylistCover", Array.Empty<KeyValuePair<string, string>>());
        request.Content = new StreamContent(source, 81920);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = source.Length;
        using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, "playlist cover upload", timeout.Token, ct).ConfigureAwait(false);
        var r = await ReadEnvelopeAsync(response, "playlist cover upload", signingIn: false, timeout.Token, ct).ConfigureAwait(false);
        var cover = r.TryGetProperty("noctisPlaylistCover", out var c) ? Str(c, "cover") : null;
        return Noctis.Services.Sync.PlaylistCovers.IsHash(cover)
            ? cover!
            : throw Log(new NoctisServerException(NoctisErrorKind.Server, "The computer did not take the cover."), "playlist cover upload", 200);
    }

    /// <summary>One picture endpoint into a file; false when the server answers with an error envelope.</summary>
    private async Task<bool> DownloadImageAsync(string endpoint, IEnumerable<KeyValuePair<string, string>> query, string destination,
        string what, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ApiTimeout);
        using var request = Request(HttpMethod.Get, endpoint, query);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, what, timeout.Token, ct).ConfigureAwait(false);
        try
        {
            await ThrowIfEnvelopeAsync(response, what, timeout.Token, ct).ConfigureAwait(false);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Server)
        {
            return false; // 70 not found: no picture
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
            throw Log(Unreachable(), what, 0);
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException)
        {
            TryDelete(tmp);
            throw Log(Unreachable(), what, 0);
        }
    }

    /// <summary>The <c>format</c> value that asks the desktop for a FLAC copy of an ALAC song.</summary>
    public const string FlacFormat = "flac";

    /// <summary>Until the answer starts: the desktop may first make a FLAC copy of an ALAC song.</summary>
    private static readonly TimeSpan DownloadStartTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Downloads a song's original file to <paramref name="partPath"/>. No deadline for the whole
    /// file (a big FLAC over slow Wi-Fi takes minutes), but a transfer that receives nothing for
    /// <see cref="StallTimeout"/> fails as Unreachable. Returns the response content type so the
    /// caller can pick an extension. <paramref name="flac"/> asks for <c>format=flac</c>: an ALAC
    /// song then arrives as FLAC (audio/flac); anything else, or a desktop without ffmpeg, still
    /// sends the original. <paramref name="hasRoom"/> is asked before the first write and every
    /// <see cref="RoomCheckBytes"/> after: false fails the download as StorageFull (the size the
    /// catalog promised may be missing or wrong, so the answer is what counts).
    /// </summary>
    public async Task<string?> DownloadTrackAsync(Guid trackId, string partPath, CancellationToken ct, bool flac = false,
        Func<bool>? hasRoom = null)
    {
        var query = new List<KeyValuePair<string, string>> { new("id", NoctisRemoteIds.ToServerTrackId(trackId)) };
        if (flac) query.Add(new("format", FlacFormat));
        using var request = Request(HttpMethod.Get, "download", query);
        // The watchdog: cancels unless it is re-armed, which every read that brings bytes does.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(DownloadStartTimeout);
        using var response = await SendAsync(request, HttpCompletionOption.ResponseHeadersRead, "download", stall.Token, ct).ConfigureAwait(false);
        await ThrowIfEnvelopeAsync(response, "download", stall.Token, ct).ConfigureAwait(false);
        try
        {
            await using var body = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
            await using var file = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            var sinceRoomCheck = RoomCheckBytes;
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                var read = await body.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
                if (read == 0) break;
                if (hasRoom is not null && (sinceRoomCheck += read) >= RoomCheckBytes)
                {
                    sinceRoomCheck = 0;
                    if (!hasRoom()) throw new NoctisServerException(NoctisErrorKind.StorageFull, "The phone is too full to download more.");
                }
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw Log(Unreachable(), "download", 0);
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
        => ScrobbleAsync(plays.Select(p => (p.Id, p.TimeMs, (string?)null)).ToList(), ct);

    /// <summary>As above, each with its scrobble verdict (noctisScrobble=, <see cref="PlayScrobbleWire"/>)
    /// when any has one: paired by position, so then every id carries one ("no" for a play recorded
    /// without). A computer that predates it ignores the field and only counts the plays.</summary>
    public Task ScrobbleAsync(IReadOnlyList<(Guid Id, long TimeMs, string? Scrobble)> plays, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>> { new("submission", "true") };
        var verdicts = plays.Any(p => p.Scrobble != null);
        foreach (var (id, time, scrobble) in plays)
        {
            form.Add(new("id", NoctisRemoteIds.ToServerTrackId(id)));
            form.Add(new("time", time.ToString(CultureInfo.InvariantCulture)));
            if (verdicts) form.Add(new("noctisScrobble", scrobble ?? PlayScrobbleWire.Of(PlayScrobble.No)));
        }
        return PostFormAsync("scrobble", form, signingIn: false, withKey: true, ct);
    }

    /// <summary>savePlayQueue (form POST): the queue in order (id=, repeated), the playing song
    /// (current=) and its place (position=, ms). A computer too old to know the call answers with an
    /// error, which the caller ignores.</summary>
    public Task SavePlayQueueAsync(PlayQueueShare share, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>();
        foreach (var id in share.Ids) form.Add(new("id", NoctisRemoteIds.ToServerTrackId(id)));
        form.Add(new("current", NoctisRemoteIds.ToServerTrackId(share.Current)));
        form.Add(new("position", Math.Max(0, share.PositionMs).ToString(CultureInfo.InvariantCulture)));
        return PostFormAsync("savePlayQueue", form, signingIn: false, withKey: true, ct);
    }

    // ── The phone's songs → the computer ─────────────────────────────────

    /// <summary>Songs per noctisHasTracks call: the server takes up to 300, but the form must also
    /// stay under its 1 MB body limit with long titles percent-encoded (≤ 100 × 1000 × 9 bytes).</summary>
    public const int HasTracksBatch = 100;

    /// <summary>The server's cap on one song (NoctisServer.MaxUploadBytes): bigger ones are not sent.</summary>
    public const long MaxUploadBytes = 500L * 1024 * 1024;

    /// <summary>After the last byte is sent: the desktop adds the song to its library before it answers.</summary>
    private static readonly TimeSpan UploadAnswerTimeout = TimeSpan.FromMinutes(2);

    /// <summary>noctisHasTracks (form POST): for each song, in order, whether the desktop's library
    /// already has it (same title and artist, lengths within 2 s).</summary>
    public async Task<bool[]> HasTracksAsync(IReadOnlyList<SongIdentity> songs, CancellationToken ct)
    {
        var form = new List<KeyValuePair<string, string>>(songs.Count * 3);
        foreach (var song in songs)
        {
            form.Add(new("title", Cap(song.Title, MaxStringLength) ?? string.Empty));
            form.Add(new("artist", Cap(song.Artist, MaxStringLength) ?? string.Empty));
            form.Add(new("duration", Math.Max(0, song.DurationSeconds).ToString(CultureInfo.InvariantCulture)));
        }
        var r = await PostFormAsync("noctisHasTracks", form, signingIn: false, withKey: true, ct).ConfigureAwait(false);
        var have = new bool[songs.Count];
        if (r.TryGetProperty("noctisHasTracks", out var h) && h.ValueKind == JsonValueKind.Object
            && h.TryGetProperty("have", out var answers) && answers.ValueKind == JsonValueKind.Array)
        {
            var i = 0;
            foreach (var answer in answers.EnumerateArray())
            {
                if (i >= have.Length) break;
                have[i++] = answer.ValueKind == JsonValueKind.True;
            }
        }
        return have;
    }

    /// <summary>
    /// noctisUploadTrack: sends <paramref name="source"/> as the POST body under
    /// <paramref name="fileName"/>, read once as it goes out (never held whole in memory). Like a
    /// download there is no deadline for the whole song, but a send that moves nothing for
    /// <see cref="StallTimeout"/> fails as Unreachable. A source that cannot be read fails as an
    /// <see cref="IOException"/> (that song only), not as the computer being unreachable.
    /// </summary>
    /// <param name="length">The song's size in bytes (sent as Content-Length), or -1 when unknown.</param>
    public async Task<UploadOutcome> UploadTrackAsync(Stream source, long length, string fileName, CancellationToken ct)
    {
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);
        var body = new WatchdogStream(source,
            onBytes: () => stall.CancelAfter(StallTimeout),
            onEnd: () => stall.CancelAfter(UploadAnswerTimeout));
        using var request = Request(HttpMethod.Post, "noctisUploadTrack", new KeyValuePair<string, string>[] { new("name", fileName) });
        request.Content = new StreamContent(body, 81920);
        request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");
        if (length >= 0) request.Content.Headers.ContentLength = length;
        JsonElement r;
        try
        {
            using var response = await SendAsync(request, HttpCompletionOption.ResponseContentRead, "upload", stall.Token, ct).ConfigureAwait(false);
            r = await ReadEnvelopeAsync(response, "upload", signingIn: false, stall.Token, ct).ConfigureAwait(false);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Unreachable && body.SourceFailed)
        {
            throw new IOException("The song could not be read on the phone.");
        }
        var status = r.TryGetProperty("noctisUpload", out var u) ? Str(u, "status") : null;
        return status switch
        {
            "added" => UploadOutcome.Added,
            "exists" => UploadOutcome.AlreadyThere,
            _ => throw Log(new NoctisServerException(NoctisErrorKind.Server, "The computer did not take the song."), "upload", 200),
        };
    }

    /// <summary>
    /// The upload body: reads straight through to the song's stream and re-arms the stall timer on
    /// every read that brings bytes. Remembers a failed read, which HttpClient reports as a failed
    /// request. Does not own the stream (the caller disposes it).
    /// </summary>
    private sealed class WatchdogStream(Stream inner, Action onBytes, Action onEnd) : Stream
    {
        public bool SourceFailed { get; private set; }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            try { return Seen(inner.Read(buffer, offset, count)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { SourceFailed = true; throw; }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            try { return Seen(await inner.ReadAsync(buffer, ct).ConfigureAwait(false)); }
            catch (Exception ex) when (ex is not OperationCanceledException) { SourceFailed = true; throw; }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

        private int Seen(int read)
        {
            if (read > 0) onBytes(); else onEnd();
            return read;
        }
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
            return ParseEnvelope(body, signingIn, what);
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
        try
        {
            // A streamed answer is not capped by MaxResponseContentBufferSize (that holds for
            // buffered sends only), and an error envelope is a few hundred bytes.
            await response.Content.LoadIntoBufferAsync(MaxErrorEnvelopeBytes, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException
                                   || (ex is OperationCanceledException && !callerToken.IsCancellationRequested))
        {
            throw Log(Unreachable(), what, (int)response.StatusCode);
        }
        await ReadEnvelopeAsync(response, what, signingIn: false, token, callerToken).ConfigureAwait(false);
        // An "ok" envelope where a file was expected.
        throw Log(new NoctisServerException(NoctisErrorKind.Server, "The server sent no file."), what, (int)response.StatusCode);
    }

    internal static JsonElement ParseEnvelope(string body, bool signingIn, string? what = null)
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
        throw MapError(code, signingIn, what);
    }

    /// <param name="what">The call ("download", …): a 70 on a download is a song whose file is gone
    /// from the computer, which the Account page counts apart from other failures.</param>
    internal static NoctisServerException MapError(int code, bool signingIn, string? what = null) => code switch
    {
        40 or 41 when signingIn && what == PairSignInMethod => new(NoctisErrorKind.PairingExpired, "This sign-in code is not valid any more."),
        // A desktop from before QR sign-in answers "Unknown method" (0) to it.
        0 when what == PairSignInMethod => new(NoctisErrorKind.Outdated, "This desktop's Noctis is too old to sign in by QR code. Update it."),
        40 or 41 when signingIn => new(NoctisErrorKind.BadCredentials, "Wrong user name or password."),
        70 when what == "download" => new(NoctisErrorKind.FileMissing, "This song's file is missing on your computer."),
        40 or 41 => new(NoctisErrorKind.SignedOut, "This phone was signed out on the desktop. Sign in again."),
        50 => new(NoctisErrorKind.SyncDisabled, "Sync is turned off on the desktop (Settings → Account & Devices)."),
        _ => new(NoctisErrorKind.Server, $"The server refused the request (error {code})."),
    };

    private static NoctisServerException Unreachable() =>
        new(NoctisErrorKind.Unreachable, "Can't reach the desktop. Check that it is on, Noctis Server is running, and both are on the same network.");

    private static NoctisServerException NotNoctis() =>
        new(NoctisErrorKind.NotNoctis, "Something answered at that address, but not a Noctis server.");

    /// <summary>First contact failed before any answer: a name that does not resolve, a TLS
    /// handshake that failed (the port speaks plain http, or not https at all), else unreachable.
    /// Read from the exception chain by kind and type name: SocketsHttpHandler sets
    /// HttpRequestError, Android's handler wraps Java's UnknownHostException / SSLException.</summary>
    internal static NoctisServerException ConnectFailure(HttpRequestException ex)
    {
        if (ex.HttpRequestError == HttpRequestError.NameResolutionError) return UnknownHost();
        if (ex.HttpRequestError == HttpRequestError.SecureConnectionError) return NotNoctis();
        for (Exception? e = ex.InnerException; e != null; e = e.InnerException)
        {
            if (e is System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData }
                || e.GetType().Name.Contains("UnknownHost", StringComparison.Ordinal))
                return UnknownHost();
            if (e is System.Security.Authentication.AuthenticationException
                || e.GetType().Name.Contains("SSL", StringComparison.OrdinalIgnoreCase))
                return NotNoctis();
        }
        return Unreachable();
    }

    private static NoctisServerException UnknownHost() =>
        new(NoctisErrorKind.UnknownHost, "No computer answers to that name.");

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
