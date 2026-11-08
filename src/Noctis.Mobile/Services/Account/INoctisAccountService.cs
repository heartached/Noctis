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
    /// <summary>The computer's other addresses from its QR code (it lists up to four), tried in
    /// turn when <see cref="ServerUrl"/> stops answering; empty for a password sign-in.</summary>
    public IReadOnlyList<string> OtherServerUrls { get; init; } = Array.Empty<string>();
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
    /// <summary>Where the computer sends the phone's plays (getNoctisSyncStatus), refreshed on every
    /// sync; null for a computer that does not say (too old to forward them) or before the first sync.</summary>
    public ComputerScrobbling? ComputerScrobbling { get; init; }

    /// <summary>Never prints <see cref="DeviceKey"/> (a record's generated ToString would).</summary>
    public override string ToString() => $"NoctisAccount {{ UserName = {UserName}, ServerUrl = {ServerUrl}, DeviceName = {DeviceName} }}";
}

/// <summary>Whether the computer's Last.fm and ListenBrainz are signed in and on.</summary>
public sealed record ComputerScrobbling(bool LastFm, bool ListenBrainz);

/// <summary>
/// A play's scrobble verdict, sent with it to the computer (noctisScrobble=). The phone records a
/// play when it starts, before it knows whether it will count, so the verdict follows later.
/// </summary>
public enum PlayScrobble
{
    /// <summary>Passed the rule in Through Computer mode: the computer sends it on.</summary>
    Computer,
    /// <summary>Passed the rule in From This Phone mode: the phone sent it (or chose not to); never again.</summary>
    Phone,
    /// <summary>Did not pass the rule (skipped, too short): counted only.</summary>
    No,
}

/// <summary>The words noctisScrobble= carries.</summary>
public static class PlayScrobbleWire
{
    /// <summary>Held: the verdict is not in yet (never sent).</summary>
    public const string Pending = "pending";

    public static string Of(PlayScrobble verdict) => verdict switch
    {
        PlayScrobble.Computer => "computer",
        PlayScrobble.Phone => "phone",
        _ => "no",
    };
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
    /// <summary>Downloads are set to Wi-Fi only and the phone is on mobile data.</summary>
    WifiRequired,
    /// <summary>QR sign-in: the code is not (or no longer) valid — expired, already used, or
    /// replaced by a newer one. The server does not say which.</summary>
    PairingExpired,
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

    /// <summary>
    /// Signs in with the desktop's QR code: tries its addresses in order, each through a client
    /// pinned to the code's fingerprint, so the one-time secret is only ever sent over a TLS
    /// connection to that certificate. Stores the device key and pin as a password sign-in does.
    /// </summary>
    Task SignInWithPairingAsync(Noctis.Services.Server.PairingPayload pairing, CancellationToken ct = default);

    /// <summary>Revokes this device's key on the server (best effort), forgets the account,
    /// removes the desktop's songs from the phone library and, if asked, the downloads.</summary>
    Task SignOutAsync(bool removeDownloads, CancellationToken ct = default);

    /// <summary>Full sync: catalog, covers, favorites/ratings, playlists, queued plays.</summary>
    Task<NoctisSyncResult> SyncNowAsync(CancellationToken ct = default);

    /// <summary>Queue a finished play of a desktop song (sent as a scrobble on the next sync).</summary>
    void RecordPlay(Track track, DateTime playedUtc);

    /// <summary>Queue a play that has just started, held until <see cref="DecidePlay"/> says whether
    /// it passed the scrobble rule (the computer sends on only those, in Through Computer mode).</summary>
    void RecordStartedPlay(Track track, DateTime playedUtc) => RecordPlay(track, playedUtc);

    /// <summary>The scrobble verdict for the latest play of <paramref name="track"/> still held by
    /// <see cref="RecordStartedPlay"/>; it is sent with that verdict on the next sync.</summary>
    void DecidePlay(Track track, PlayScrobble verdict) { }

    /// <summary>Where the computer sends the phone's plays, as its last sync said; null when unknown
    /// (not synced yet, or a computer too old to forward them).</summary>
    ComputerScrobbling? ComputerScrobbling => null;

    /// <summary>Plays queued for the computer that the next sync has not sent yet.</summary>
    int PendingPlayCount => 0;

    /// <summary>"Pick Up on Computer": tells the computer the queue and place the phone is at (the
    /// Subsonic savePlayQueue call). Throws when the computer is unreachable or too old to know it;
    /// does nothing signed out.</summary>
    Task SavePlayQueueAsync(PlayQueueShare share, CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>One of the computer's playlists (synced, not made on the phone).</summary>
    bool IsSyncedPlaylist(Guid playlistId) => false;

    /// <summary>The phone deleted a playlist: one of the computer's is deleted there too on the
    /// next sync (instead of being restored as a stale save's loss); a phone-only one is ignored.</summary>
    void RecordPlaylistDeleted(Guid playlistId) { }

    /// <summary>The phone removed a playlist's custom cover: one of the computer's loses it there too
    /// on the next sync; a phone-only one is ignored.</summary>
    void RecordPlaylistCoverRemoved(Guid playlistId) { }

    // ── Downloads ──
    bool IsRemote(Track track);

    /// <summary>The desktop song id a track stands for: a desktop song itself, or one of the phone's
    /// own files the sync paired with a desktop song (shown once, plays the file, syncs under the
    /// desktop's id). Null for a song only the phone has.</summary>
    Guid? DesktopIdOf(Track track) => IsRemote(track) ? track.Id : null;
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
