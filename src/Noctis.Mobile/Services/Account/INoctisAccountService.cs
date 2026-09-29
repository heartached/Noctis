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
    public DateTime? LastSyncUtc { get; init; }
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
}

public sealed class NoctisServerException(NoctisErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public NoctisErrorKind Kind { get; } = kind;
}

public enum NoctisSyncStage { Idle, Catalog, Covers, State, Playlists, Plays, Done, Failed }

public sealed record NoctisSyncProgress(NoctisSyncStage Stage, int Done, int Total);

public sealed record NoctisSyncResult(int Songs, int Playlists, int StateChangesPulled, int StateChangesPushed, int PlaysSent);

public sealed record NoctisDownloadProgress(int Pending, int Completed, int Failed, long BytesUsed);

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

    /// <summary>Raised (any thread) when Account, IsSyncing or download state changes.</summary>
    event EventHandler? StateChanged;
    event EventHandler<NoctisSyncProgress>? SyncProgress;
    event EventHandler<NoctisDownloadProgress>? DownloadProgress;

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

    // ── Downloads ──
    bool IsRemote(Track track);
    bool IsDownloaded(Track track);
    long DownloadedBytes { get; }
    int DownloadedCount { get; }
    Task DownloadAsync(IEnumerable<Track> tracks, CancellationToken ct = default);
    Task DownloadAllAsync(CancellationToken ct = default);
    Task RemoveDownloadsAsync(IEnumerable<Track> tracks);
    Task RemoveAllDownloadsAsync();

    /// <summary>
    /// For the player: the local file of a downloaded desktop song, else its authenticated
    /// https stream URL; null when <paramref name="filePath"/> is not a desktop song.
    /// </summary>
    string? ResolvePlaybackUri(string filePath);
}
