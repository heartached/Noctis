using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using Noctis.Models;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The phone's signed-in link to a desktop Noctis Server. Persisted under the platform's
/// no-backup directory; the password is never stored, only the per-device key the desktop
/// issued at sign-in.
/// </summary>
public sealed record NoctisAccount
{
    /// <summary>"https://host:port", no trailing slash, no path.</summary>
    public required string ServerUrl { get; init; }
    public required string UserName { get; init; }
    /// <summary>The per-device API key ("nk_…") from noctisSignIn.</summary>
    public required string DeviceKey { get; init; }
    /// <summary>Pinned SHA-256 of the server's leaf certificate, "AB:CD:…" upper-case hex.</summary>
    public required string Fingerprint { get; init; }
    /// <summary>Random id for this install, stable across sign-ins.</summary>
    public required string DeviceId { get; init; }
    public required string DeviceName { get; init; }
    /// <summary>The desktop's name as the server reported it.</summary>
    public string ServerName { get; init; } = "";
    /// <summary>The desktop's profile name ("Username" in its Settings), shown over the account's
    /// email; null when the desktop has none, or for an account saved before it was sent.
    /// Refreshed on every sync.</summary>
    public string? DisplayName { get; init; }
    public DateTime? LastSyncUtc { get; init; }

    /// <summary>Never prints <see cref="DeviceKey"/> (a record's generated ToString would).</summary>
    public override string ToString() => $"NoctisAccount {{ UserName = {UserName}, ServerUrl = {ServerUrl}, DeviceName = {DeviceName} }}";
}

/// <summary>Why a server call failed, for the UI to word.</summary>
public enum NoctisErrorKind
{
    Unreachable,
    /// <summary>The certificate no longer matches the pinned fingerprint.</summary>
    CertificateChanged,
    BadCredentials,
    /// <summary>Too many failed sign-ins; retry later.</summary>
    LockedOut,
    /// <summary>Sync is turned off on the desktop (Subsonic error 50).</summary>
    SyncDisabled,
    /// <summary>The device key was revoked or is unknown.</summary>
    SignedOut,
    InvalidAddress,
    Server,
    /// <summary>The phone is too full to download more (a reserve is always left free).</summary>
    StorageFull,
    /// <summary>The desktop lists the song but its file is gone from the computer (download error 70).</summary>
    FileMissing,
    /// <summary>First contact: something answered at the address, but not a Noctis server over
    /// https (another web page, a plain-http port, a router login).</summary>
    NotNoctis,
    /// <summary>First contact: the address's name does not resolve ("studio-pc.lan" typed wrong).</summary>
    UnknownHost,
    /// <summary>The desktop answers but is too old to sign phones in (no noctisSignIn).</summary>
    Outdated,
}

public sealed class NoctisServerException(NoctisErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public NoctisErrorKind Kind { get; } = kind;
}

public enum NoctisSyncStage { Idle, Catalog, Covers, State, Playlists, Plays, Done, Failed }

public sealed record NoctisSyncProgress(NoctisSyncStage Stage, int Done, int Total);

public sealed record NoctisSyncResult(int Songs, int Playlists, int StateChangesPulled, int StateChangesPushed, int PlaysSent);

/// <param name="Missing">Of <paramref name="Failed"/>, the songs whose file is missing on the computer.</param>
public sealed record NoctisDownloadProgress(int Pending, int Completed, int Failed, long BytesUsed, int Missing = 0);

/// <summary>Sending the phone's own songs to the computer: <paramref name="Done"/> of <paramref name="Total"/> handled.</summary>
public sealed record NoctisUploadProgress(int Done, int Total);

/// <param name="Sent">Songs the computer added.</param>
/// <param name="AlreadyThere">Songs the computer already had (same song, or the same file).</param>
/// <param name="Failed">Songs that could not be read or sent; the next press tries them again.</param>
/// <param name="Previously">Songs sent by an earlier press, not asked about again.</param>
public sealed record NoctisUploadResult(int Sent, int AlreadyThere, int Failed, int Previously);

/// <summary>
/// Builds the HTTP handler the account service talks through. The service passes a
/// certificate check (true = accept); the platform wires it into its TLS stack. Windows and
/// tests use a SocketsHttpHandler; Android an HttpClientHandler (AndroidMessageHandler).
/// </summary>
public delegate HttpMessageHandler NoctisHandlerFactory(Func<X509Certificate2, bool> acceptCertificate);

public interface INoctisAccountService
{
    NoctisAccount? Account { get; }
    bool IsSignedIn { get; }
    bool IsSyncing { get; }

    /// <summary>The computer refused this phone's key (removed there, or its password changed) and
    /// the phone signed itself out. Cleared by the next sign-in. Lets the Account page say why
    /// when the sync at app start was the one that found out.</summary>
    bool WasSignedOutByComputer => false;

    /// <summary>The desktop's profile picture as a local file (fetched at sign-in and on each sync,
    /// kept across restarts), or null: signed out, or the desktop has none. A new picture gets a
    /// new path (image caches key by path).</summary>
    string? AvatarPath { get; }

    /// <summary>Raised (any thread) when Account, IsSyncing, AvatarPath or download state changes.</summary>
    event EventHandler? StateChanged;
    event EventHandler<NoctisSyncProgress>? SyncProgress;
    event EventHandler<NoctisDownloadProgress>? DownloadProgress;

    /// <summary>Raised (any thread) right after the service rewrote playlists.json: a sync applied
    /// the desktop's playlists, or sign-out removed them. Whoever shows playlists re-reads it.</summary>
    event EventHandler? PlaylistsChanged;

    /// <summary>
    /// First contact: connects without credentials and returns the server certificate's
    /// SHA-256 fingerprint for the user to compare with the desktop's Settings.
    /// </summary>
    Task<string> ProbeFingerprintAsync(string serverUrl, CancellationToken ct = default);

    /// <summary>Signs in with the fingerprint the user confirmed; stores the device key.</summary>
    Task SignInAsync(string serverUrl, string userName, string password, string confirmedFingerprint,
        CancellationToken ct = default);

    /// <summary>Revokes this device's key on the server (best effort), forgets the account,
    /// removes the desktop's songs from the phone library and, if asked, the downloads.</summary>
    Task SignOutAsync(bool removeDownloads, CancellationToken ct = default);

    /// <summary>Full sync: catalog, covers, favorites/ratings, playlists, queued plays.</summary>
    Task<NoctisSyncResult> SyncNowAsync(CancellationToken ct = default);

    /// <summary>Queue a finished play of a desktop song (sent as a scrobble on the next sync).</summary>
    void RecordPlay(Track track, DateTime playedUtc);

    /// <summary>Plays queued for the computer that the next sync has not sent yet.</summary>
    int PendingPlayCount => 0;

    /// <summary>One of the computer's playlists (synced, not made on the phone).</summary>
    bool IsSyncedPlaylist(Guid playlistId) => false;

    /// <summary>The phone deleted a playlist: one of the computer's is deleted there too on the
    /// next sync (instead of being restored as a stale save's loss); a phone-only one is ignored.</summary>
    void RecordPlaylistDeleted(Guid playlistId) { }

    // ── Downloads ──
    bool IsRemote(Track track);
    bool IsDownloaded(Track track);
    long DownloadedBytes { get; }
    int DownloadedCount { get; }
    Task DownloadAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task DownloadAllAsync(CancellationToken ct = default);
    Task RemoveDownloadsAsync(IEnumerable<Track> tracks);
    Task RemoveAllDownloadsAsync();

    // ── The phone's own songs → the computer ──

    /// <summary>A send of the phone's songs is running.</summary>
    bool IsUploading { get; }

    /// <summary>Raised (any thread) after each song of a send.</summary>
    event EventHandler<NoctisUploadProgress>? UploadProgress;

    /// <summary>
    /// Sends the phone's own songs (not the desktop's) that the computer does not have yet, one at
    /// a time, into its library. Songs sent before (remembered per account) are skipped, so a
    /// cancelled or failed send resumes on the next call. One send at a time: a call while one
    /// runs gets the running one, and cancelling its token stops that one too.
    /// </summary>
    Task<NoctisUploadResult> SendLocalSongsAsync(CancellationToken ct = default);

    /// <summary>
    /// For the player: the local file of a downloaded desktop song, else its authenticated
    /// https stream URL; null when <paramref name="filePath"/> is not a desktop song.
    /// </summary>
    string? ResolvePlaybackUri(string filePath);
}
