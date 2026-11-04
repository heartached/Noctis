using System.Collections.Concurrent;
using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Sync;

namespace Noctis.Mobile.Services.Account;

/// <summary>
/// Hands the library's user-state saves to the account service. Create it first, pass it to
/// the <see cref="LibraryService"/> as its sync recorder, then to
/// <see cref="NoctisAccountService"/>, which attaches itself (the library is built before the
/// service that needs it, hence the indirection).
/// </summary>
public sealed class NoctisStateRecorder : ITrackStateRecorder
{
    private volatile ITrackStateRecorder? _target;

    internal void Attach(ITrackStateRecorder target) => _target = target;

    public void RecordTrackStates(IEnumerable<Track> tracks) => _target?.RecordTrackStates(tracks);
}

/// <summary>
/// The phone's account on a desktop Noctis Server: sign-in with a pinned certificate, the
/// desktop's songs as <see cref="SourceType.NoctisServer"/> tracks in the shared library,
/// covers, two-way favorites/ratings/playlists through the sync ledger, plays as scrobbles,
/// and offline downloads.
///
/// Threading: every public method may be called from the UI thread; network and file work
/// runs on the pool. Library tracks are only mutated through <c>marshal</c> (default: the
/// Avalonia UI thread), because their observable properties are bound. Events fire on any
/// thread. <see cref="SyncNowAsync"/> is single-flight: a call while a sync runs gets the
/// running sync's task.
///
/// State rules: a new desktop song takes the catalog's favorite/rating/play count; a known one
/// takes the catalog's favorite/rating unless the phone has an unpushed edit for it, and its
/// play count only grows. Ledger items then apply newest-wins against unpushed phone edits.
/// The phone pushes favorite/rating/dislike changes only (play count 0 and no last-played, so
/// the desktop's max-merge ignores them); plays travel as scrobbles.
/// </summary>
public sealed partial class NoctisAccountService : INoctisAccountService, ITrackStateRecorder
{
    private const int PushChunk = 2000;
    private const int ScrobbleBatch = 500;
    private const int MaxQueuedPlays = 10_000;
    private const int MaxPlaylistTracks = 10_000;
    private const int MaxPlaylistName = 200;
    private const int MaxPlaylistDescription = 2000;
    private const long MaxPushBytes = 3L * 1024 * 1024; // the server refuses bodies over 4 MB
    private const int CoverConcurrency = 4;
    private const int CoverFailureLimit = 8;
    private const int DownloadConcurrency = 2;
    private static readonly TimeSpan CoverMaxAge = TimeSpan.FromDays(30);
    private static readonly HashSet<string> AllowedExtensions = new(StringComparer.Ordinal)
    {
        "mp3", "flac", "m4a", "aac", "ogg", "oga", "opus", "wav", "aiff", "aif", "wma", "ape", "wv", "alac",
        "mp4", "m4b", "dsf", "dff",
    };

    private readonly ILibraryService _library;
    private readonly IPersistenceService _persistence;
    private readonly NoctisHandlerFactory _handlerFactory;
    private readonly NoctisAccountStore _store;
    private readonly string _offlineDir;
    private readonly string _deviceId;
    private readonly Action<Action> _marshal;
    private readonly TimeProvider _time;

    // _gate guards _account, _client, _sync and _pending. Never held across an await.
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private volatile NoctisAccount? _account;
    private NoctisServerClient? _client;
    private NoctisSyncState _sync;
    private NoctisPendingState _pending;
    /// <summary>Phone track id → desktop song id, the reverse of <see cref="NoctisSyncState.Links"/> (under _gate).</summary>
    private Dictionary<Guid, Guid> _linkedByLocal = new();

    private Task<NoctisSyncResult>? _syncTask;
    private CancellationTokenSource? _syncCts;
    private volatile bool _isSyncing;

    private sealed record DownloadedFile(string Path, long Size);
    private readonly ConcurrentDictionary<Guid, DownloadedFile> _downloaded = new();
    /// <summary>Songs downloading now, each with its own stop (Remove Download on it).</summary>
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _inFlight = new();
    /// <summary>Songs whose download was removed: a batch that still holds one skips it, and one
    /// that finishes it throws the file away. Cleared when the song is asked for again.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _withdrawn = new();
    /// <summary>A finished download joins <see cref="_downloaded"/> and a removal withdraws it under this,
    /// so a removal can never land between the check and the add.</summary>
    private readonly object _downloadCommit = new();
    private readonly SemaphoreSlim _downloadSlots = new(DownloadConcurrency, DownloadConcurrency);
    private readonly List<Task> _downloadRuns = new();
    private CancellationTokenSource _downloadCts = new();
    private int _dlPending, _dlCompleted, _dlFailed, _dlMissing;

    /// <summary>Song suffixes from this process's last catalog (download file extensions).</summary>
    private readonly ConcurrentDictionary<Guid, string> _suffixes = new();
    private readonly ConcurrentDictionary<Guid, long> _sizes = new();

    /// <summary>Space downloads and covers always leave free on the phone: "Download everything" on a
    /// desktop library can be far larger than the phone (the owner's is ~306 GB).</summary>
    internal const long StorageReserveBytes = 1536L * 1024 * 1024;

    /// <summary>Free bytes on the volume holding a path; tests substitute a full phone.</summary>
    internal Func<string, long> FreeSpace { get; init; } = DefaultFreeSpace;

    /// <summary>
    /// Completes once the phone's library has read library.json (AndroidApp: after
    /// LibraryService.LoadAsync). A sync waits for it: the Account page is reachable before the
    /// load ends, and importing into a library that has not loaded yet saves library.json with
    /// the desktop's songs only, dropping every local one.
    /// </summary>
    public Task LibraryReady { get; init; } = Task.CompletedTask;

    /// <summary>How long a download may receive nothing before it counts as failed (tests shorten it).</summary>
    internal TimeSpan DownloadStallTimeout { get; init; } = NoctisServerClient.DefaultStallTimeout;

    /// <summary>
    /// This phone has no ALAC decoder: desktop songs that may be ALAC (an MP4-family suffix) are
    /// streamed and downloaded with <c>format=flac</c>, and the desktop sends a lossless FLAC copy
    /// of the ALAC ones (AAC, or a desktop without ffmpeg, still sends the original). Downloads
    /// already on the phone are left as they are.
    /// </summary>
    public bool PreferFlacForAlac { get; init; }

    private static bool MayBeAlac(string suffix) => suffix is "m4a" or "mp4" or "m4b" or "alac";

    /// <summary>Whether to ask the desktop for FLAC instead of this song's original.</summary>
    private bool WantsFlac(Guid id)
    {
        if (!PreferFlacForAlac) return false;
        if (_suffixes.TryGetValue(id, out var suffix)) return MayBeAlac(suffix);
        // Before this run's first catalog (suffixes are per process): the codec that catalog mapped,
        // which is "ALAC" only for an MP4-family song at a lossless bitrate (CodecFor).
        return _library.GetTrackById(id) is { SourceType: SourceType.NoctisServer } t && t.Codec == "ALAC";
    }

    private static long DefaultFreeSpace(string path)
    {
        // statvfs on the path itself (Unix DriveInfo accepts any directory); unknown = no limit,
        // so a platform that cannot tell never blocks downloads.
        try { return new DriveInfo(path).AvailableFreeSpace; }
        catch (Exception) { return long.MaxValue; }
    }
    /// <summary>Albums the server had no cover for this process (not re-asked every sync).</summary>
    private readonly ConcurrentDictionary<Guid, byte> _coverMisses = new();

    /// <param name="library">The shared library (desktop songs join it as NoctisServer tracks).</param>
    /// <param name="persistence">For covers (GetArtworkPath) and playlists.json.</param>
    /// <param name="handlerFactory">Builds the TLS handler; the service passes the pin check.</param>
    /// <param name="accountDir">No-backup directory for the account files (Android NoBackupFilesDir).</param>
    /// <param name="offlineDir">Where downloads go (Android FilesDir/offline).</param>
    /// <param name="deviceName">Shown in the desktop's device list (e.g. Build.Model); 1–64 printable chars.</param>
    /// <param name="stateRecorder">The recorder the LibraryService was built with, so phone favorite/rating
    /// edits of desktop songs are queued for the next sync. Null = those edits are not pushed.</param>
    /// <param name="marshal">Runs an action on the UI thread (default Avalonia's dispatcher).</param>
    /// <param name="time">Clock (tests).</param>
    public NoctisAccountService(ILibraryService library, IPersistenceService persistence, NoctisHandlerFactory handlerFactory,
        string accountDir, string offlineDir, string deviceName, NoctisStateRecorder? stateRecorder = null,
        Action<Action>? marshal = null, TimeProvider? time = null)
    {
        _library = library;
        _persistence = persistence;
        _handlerFactory = handlerFactory;
        _store = new NoctisAccountStore(accountDir);
        _offlineDir = Path.GetFullPath(offlineDir);
        DeviceName = SanitizeDeviceName(deviceName);
        _marshal = marshal ?? (a => Avalonia.Threading.Dispatcher.UIThread.Post(a));
        _time = time ?? TimeProvider.System;

        _deviceId = _store.LoadOrCreateDeviceId();
        _account = _store.LoadAccount();
        _sync = _store.LoadSyncState();
        RebuildLinkIndexLocked();
        _pending = _store.LoadPending();
        // A play the last run held for its verdict can no longer get one (the song's clock went
        // with that run): counted only, never sent on to Last.fm.
        for (var i = 0; i < _pending.Plays.Count; i++)
            if (_pending.Plays[i].Scrobble == PlayScrobbleWire.Pending)
                _pending.Plays[i] = _pending.Plays[i] with { Scrobble = PlayScrobbleWire.Of(PlayScrobble.No) };
        IndexDownloads();
        LoadCachedAvatar();
        stateRecorder?.Attach(this);
    }

    /// <summary>Tests: unpushed favorite/rating edits and queued plays.</summary>
    internal int PendingTrackCount { get { lock (_gate) return _pending.Tracks.Count; } }
    public int PendingPlayCount { get { lock (_gate) return _pending.Plays.Count; } }

    public string DeviceId => _deviceId;
    public string DeviceName { get; }

    public NoctisAccount? Account => _account;
    public bool IsSignedIn => _account is not null;
    public bool IsSyncing => _isSyncing;

    public event EventHandler? StateChanged;
    public event EventHandler<NoctisSyncProgress>? SyncProgress;
    public event EventHandler<NoctisDownloadProgress>? DownloadProgress;
    public event EventHandler? PlaylistsChanged;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Sign-in / sign-out ────────────────────────────────────────────────

    public async Task<string> ProbeFingerprintAsync(string serverUrl, CancellationToken ct = default)
    {
        var url = NoctisServerClient.NormalizeServerUrl(serverUrl);
        return await Task.Run(() => NoctisServerClient.ProbeFingerprintAsync(_handlerFactory, url, ct), ct).ConfigureAwait(false);
    }

    public async Task SignInAsync(string serverUrl, string userName, string password, string confirmedFingerprint,
        CancellationToken ct = default)
    {
        var url = NoctisServerClient.NormalizeServerUrl(serverUrl);
        var user = (userName ?? string.Empty).Trim();
        if (user.Length == 0 || string.IsNullOrEmpty(password))
            throw new NoctisServerException(NoctisErrorKind.BadCredentials, "Enter your user name and password.");
        var fingerprint = url.StartsWith("https://", StringComparison.Ordinal)
            ? NoctisServerClient.NormalizeFingerprint(confirmedFingerprint)
            : string.Empty;

        SignInResult result;
        // The pinned handler refuses any other certificate, so this only succeeds against the
        // certificate the user confirmed.
        using (var client = new NoctisServerClient(_handlerFactory, url, fingerprint, deviceKey: null))
            result = await Task.Run(() => client.SignInAsync(user, password, _deviceId, DeviceName, ct), ct).ConfigureAwait(false);

        CompleteSignIn(url, fingerprint, result, user);
    }

    public async Task SignInWithPairingAsync(Noctis.Services.Server.PairingPayload pairing, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(pairing);
        var fingerprint = NoctisServerClient.NormalizeFingerprint(pairing.Fingerprint);
        NoctisServerException? unreachable = null, otherCertificate = null;
        foreach (var url in pairing.ServerUrls)
        {
            try
            {
                SignInResult result;
                // Pinned to the code's fingerprint: a computer at this address with any other
                // certificate fails the TLS handshake, before the secret is written.
                using (var client = new NoctisServerClient(_handlerFactory, url, fingerprint, deviceKey: null))
                    result = await Task.Run(() => client.PairSignInAsync(pairing.Secret, _deviceId, DeviceName, ct), ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(result.User))
                    throw new NoctisServerException(NoctisErrorKind.Server, "The server did not say which account signed in.");
                CompleteSignIn(url, fingerprint, result, result.User!);
                return;
            }
            // Nothing reached a server that holds the pin there: the next address may.
            catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.CertificateChanged)
            {
                otherCertificate = ex;
            }
            catch (NoctisServerException ex) when (ex.Kind is NoctisErrorKind.Unreachable or NoctisErrorKind.NotNoctis or NoctisErrorKind.UnknownHost)
            {
                unreachable ??= ex;
            }
        }
        // Something at the code's address that is not the computer which showed it says so;
        // else the computer could not be reached at any of its addresses.
        throw otherCertificate ?? unreachable
              ?? new NoctisServerException(NoctisErrorKind.Unreachable, "Can't reach the desktop.");
    }

    /// <summary>A sign-in the server accepted (password or QR): the account is stored, with its pin.</summary>
    private void CompleteSignIn(string url, string fingerprint, SignInResult result, string user)
    {
        var account = new NoctisAccount
        {
            ServerUrl = url,
            UserName = string.IsNullOrWhiteSpace(result.User) ? user : result.User!,
            DeviceKey = result.ApiKey,
            Fingerprint = fingerprint,
            DeviceId = _deviceId,
            DeviceName = DeviceName,
            ServerName = result.Server ?? string.Empty,
            DisplayName = result.DisplayName,
        };
        lock (_gate)
        {
            // Another server or user: nothing from the old checkpoint or queue applies.
            if (!SameIdentity(_sync.ServerUrl, _sync.UserName, account) || !SameIdentity(_pending.ServerUrl, _pending.UserName, account))
            {
                _sync = new NoctisSyncState { ServerUrl = account.ServerUrl, UserName = account.UserName };
                RebuildLinkIndexLocked();
                _pending = new NoctisPendingState { ServerUrl = account.ServerUrl, UserName = account.UserName };
                _store.SaveSyncState(_sync);
                _store.SavePending(_pending);
            }
            _store.SaveAccount(account);
            _client?.Dispose();
            _client = null;
            _account = account;
            _signedOutByComputer = false;
        }
        DebugLog.Write("Account", $"signed in (sync {(result.SyncEnabled ? "on" : "off")} on the desktop)");
        RaiseStateChanged();
        StartAvatarRefresh();
    }

    private static bool SameIdentity(string url, string user, NoctisAccount account) =>
        string.Equals(url, account.ServerUrl, StringComparison.OrdinalIgnoreCase)
        && string.Equals(user, account.UserName, StringComparison.OrdinalIgnoreCase);

    public async Task SignOutAsync(bool removeDownloads, CancellationToken ct = default)
    {
        // Stop whatever is running for this account first.
        Task? running;
        lock (_gate)
        {
            _syncCts?.Cancel();
            running = _syncTask;
        }
        if (running is not null)
        {
            try { await running.ConfigureAwait(false); } catch { /* cancelled or failed: irrelevant now */ }
        }
        await StopDownloadsAsync().ConfigureAwait(false);
        await StopUploadsAsync().ConfigureAwait(false);

        NoctisServerClient? client;
        List<Guid> syncedPlaylists;
        lock (_gate)
        {
            client = _account is null ? null : GetClientLocked();
            syncedPlaylists = _sync.Playlists.Keys
                .Select(k => NoctisRemoteIds.TryParseSyncId(k, out var g) ? g : Guid.Empty)
                .Where(g => g != Guid.Empty).ToList();
        }

        if (client is not null)
        {
            // Best effort: the key dies on the desktop too. Offline or already revoked is fine.
            try
            {
                using var shortWait = CancellationTokenSource.CreateLinkedTokenSource(ct);
                shortWait.CancelAfter(TimeSpan.FromSeconds(5));
                await Task.Run(() => client.SignOutAsync(shortWait.Token), shortWait.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is NoctisServerException or OperationCanceledException)
            {
                DebugLog.Write("Account", "sign-out: key not revoked on the desktop (unreachable or already gone)");
            }
        }

        lock (_gate)
        {
            _store.DeleteAccount();
            _store.DeleteSyncData();
            _sync = new NoctisSyncState();
            RebuildLinkIndexLocked();
            _pending = new NoctisPendingState();
            _client?.Dispose();
            _client = null;
            _account = null;
        }
        ClearAvatar();
        RaiseStateChanged();

        var playlistsRemoved = await Task.Run(async () =>
        {
            // Before the library has loaded there is nothing to remove yet, and the load would
            // bring the desktop's songs back. A failed load: remove what there is.
            await LibraryReady.ContinueWith(_ => { }, TaskScheduler.Default).ConfigureAwait(false);
            await _library.RemoveRemoteTracksAsync().ConfigureAwait(false);
            var removed = false;
            if (syncedPlaylists.Count > 0)
            {
                var ids = syncedPlaylists.ToHashSet();
                var playlists = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
                if (playlists.RemoveAll(p => ids.Contains(p.Id)) > 0)
                {
                    await _persistence.SavePlaylistsAsync(playlists).ConfigureAwait(false);
                    removed = true;
                }
            }
            if (removeDownloads) DeleteAllDownloadFiles();
            else DeletePartFiles();
            DeleteSavedLyrics();
            return removed;
        }).ConfigureAwait(false);
        // Only now: a reload on the StateChanged above would still read the desktop's playlists.
        if (playlistsRemoved) RaisePlaylistsChanged();
        DebugLog.Write("Account", $"signed out (downloads {(removeDownloads ? "removed" : "kept")})");
        RaiseDownloadProgress();
        RaiseStateChanged();
    }

    /// <summary>The server rejected the key: forget the account locally (downloads and the
    /// library stay until the user signs in again or signs out).</summary>
    private volatile bool _signedOutByComputer;
    public bool WasSignedOutByComputer => _signedOutByComputer;

    private void ForgetAccountLocally()
    {
        lock (_gate)
        {
            if (_account is null) return;
            _store.DeleteAccount();
            _client?.Dispose();
            _client = null;
            _account = null;
            _signedOutByComputer = true;
        }
        ClearAvatar();
        DebugLog.Write("Account", "the desktop no longer accepts this phone's key; signed out locally");
        RaiseStateChanged();
    }

    private NoctisServerClient GetClient()
    {
        lock (_gate) return GetClientLocked();
    }

    private NoctisServerClient GetClientLocked()
    {
        var account = _account ?? throw new NoctisServerException(NoctisErrorKind.SignedOut, "Not signed in.");
        return _client ??= new NoctisServerClient(_handlerFactory, account.ServerUrl, account.Fingerprint, account.DeviceKey)
        {
            StallTimeout = DownloadStallTimeout,
        };
    }

    // ── Sync ─────────────────────────────────────────────────────────────

    public Task<NoctisSyncResult> SyncNowAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_syncTask is { IsCompleted: false } running) return running;
            if (_account is null) return Task.FromException<NoctisSyncResult>(new NoctisServerException(NoctisErrorKind.SignedOut, "Not signed in."));
            _syncCts?.Dispose();
            _syncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _syncCts.Token;
            _isSyncing = true;
            _syncTask = Task.Run(() => RunSyncAsync(token), CancellationToken.None);
            return _syncTask;
        }
    }

    private async Task<NoctisSyncResult> RunSyncAsync(CancellationToken ct)
    {
        RaiseStateChanged();
        try
        {
            var result = await SyncCoreAsync(ct).ConfigureAwait(false);
            RaiseSyncProgress(NoctisSyncStage.Done, 1, 1);
            DebugLog.Write("Account", $"sync done: {result.Songs} songs, {result.Playlists} playlists, " +
                $"{result.StateChangesPulled} pulled, {result.StateChangesPushed} pushed, {result.PlaysSent} plays");
            return result;
        }
        catch (Exception ex)
        {
            // Sync switched off on the computer still ran to the end (catalog, covers, plays):
            // it reads as finished, not as "didn't finish, try again".
            RaiseSyncProgress(ex is NoctisServerException { Kind: NoctisErrorKind.SyncDisabled } ? NoctisSyncStage.Done : NoctisSyncStage.Failed, 0, 0);
            if (ex is NoctisServerException { Kind: NoctisErrorKind.SignedOut }) ForgetAccountLocally();
            DebugLog.Write("Account", $"sync failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
            throw;
        }
        finally
        {
            _isSyncing = false;
            RaiseStateChanged();
        }
    }

    private async Task<NoctisSyncResult> SyncCoreAsync(CancellationToken ct)
    {
        var client = GetClient();

        // 1. Catalog → the library.
        RaiseSyncProgress(NoctisSyncStage.Catalog, 0, 0);
        await LibraryReady.WaitAsync(ct).ConfigureAwait(false);
        var albumArtists = await client.GetAlbumArtistsAsync(ct).ConfigureAwait(false);
        var (songs, settled) = await FetchCatalogAsync(client, ct).ConfigureAwait(false);
        var (tracks, linked) = await ImportCatalogAsync(songs, albumArtists, keepUnlisted: !settled, ct).ConfigureAwait(false);
        RaiseSyncProgress(NoctisSyncStage.Catalog, tracks.Count + linked, tracks.Count + linked);

        // 2. Covers.
        await SyncCoversAsync(client, tracks, ct).ConfigureAwait(false);

        // 3–4. Ledger: pull, push favorite/rating edits, playlists. Sync can be off on the
        // desktop (error 50): the catalog and plays still go through, then the caller hears it.
        var pulled = 0;
        var pushed = 0;
        var playlistCount = 0;
        NoctisServerException? syncOff = null;
        try
        {
            RaiseSyncProgress(NoctisSyncStage.State, 0, 0);
            var pulledPlaylists = new Dictionary<Guid, PlaylistSyncState>();
            long checkpoint;
            (pulled, checkpoint) = await PullAsync(client, pulledPlaylists, ct).ConfigureAwait(false);
            pushed = await PushTrackStatesAsync(client, ct).ConfigureAwait(false);
            RaiseSyncProgress(NoctisSyncStage.Playlists, 0, pulledPlaylists.Count);
            (playlistCount, var playlistsPushed) = await SyncPlaylistsAsync(client, pulledPlaylists, ct).ConfigureAwait(false);
            pushed += playlistsPushed;
            // The checkpoint moves only once every pulled item (playlists too) is applied, so a
            // sync that fails midway re-pulls rather than loses anything.
            lock (_gate) _sync.Seq = checkpoint;
            SaveSyncState();
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.SyncDisabled)
        {
            syncOff = ex;
        }

        // 5. Plays.
        var plays = await SendPlaysAsync(client, ct).ConfigureAwait(false);

        // 6. The desktop's profile picture and name (best effort: never fail the sync).
        await RefreshAvatarAsync(ct).ConfigureAwait(false);
        var (nameKnown, displayName, serverName, scrobbling) = await FetchDisplayNameAsync(client, ct).ConfigureAwait(false);

        NoctisAccount? updated;
        lock (_gate)
        {
            // An unanswered ask keeps the name shown; an answer without one clears it (the desktop's was removed).
            // The computer's name follows a rename in its Settings (sign-in only had the old one).
            updated = _account is null ? null
                : _account with
                {
                    LastSyncUtc = UtcNow,
                    DisplayName = nameKnown ? displayName : _account.DisplayName,
                    // Where the computer sends the phone's plays: an answer without it is a computer
                    // that sends them nowhere (an older one), so it clears too.
                    ComputerScrobbling = nameKnown ? scrobbling : _account.ComputerScrobbling,
                    ServerName = serverName ?? _account.ServerName,
                };
            if (updated is not null)
            {
                _store.SaveAccount(updated);
                _account = updated;
            }
        }
        RaiseStateChanged();

        // 7. Lyrics for downloaded songs that have none saved (after LastSyncUtc, so they count as current).
        await PrefetchDownloadedLyricsAsync(ct).ConfigureAwait(false);
        if (syncOff is not null) throw syncOff;
        return new NoctisSyncResult(tracks.Count + linked, playlistCount, pulled, pushed, plays);
    }

    /// <summary>
    /// search3 pages between two getScanStatus reads. Settled = neither read saw a scan and both
    /// counted exactly the songs listed: only then is a song missing from the list really gone.
    /// While the desktop scans it lists only what the scan has reached (it publishes partial
    /// lists every 1.5 s), so an unsettled catalog must never remove anything. Re-read once when
    /// the library merely changed between pages; not during a scan, which outlasts a re-read.
    /// </summary>
    private async Task<(List<RemoteSong> Songs, bool Settled)> FetchCatalogAsync(NoctisServerClient client, CancellationToken ct)
    {
        var progress = new InlineProgress(n => RaiseSyncProgress(NoctisSyncStage.Catalog, n, 0));
        List<RemoteSong> songs = new();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var before = await client.GetScanStatusAsync(ct).ConfigureAwait(false);
            songs = await client.GetAllSongsAsync(progress, ct).ConfigureAwait(false);
            var after = await client.GetScanStatusAsync(ct).ConfigureAwait(false);
            var scanning = before.Scanning || after.Scanning;
            if (!scanning && before.Count >= 0 && before.Count == after.Count && songs.Count == after.Count) return (songs, true);
            var retry = attempt == 0 && !scanning;
            DebugLog.Write("Account", $"catalog: {songs.Count} songs listed, the desktop counts {before.Count}, then {after.Count}" +
                $"{(scanning ? " (scanning)" : "")}; {(retry ? "re-reading" : "adding only, nothing removed")}");
            if (!retry) break;
        }
        return (songs, false);
    }

    /// <param name="keepUnlisted">The catalog may be partial (desktop scanning or changing): desktop
    /// songs already here that it does not list stay, instead of leaving (and dropping out of the
    /// queue) until the next sync brings them back as new songs.</param>
    /// <returns>The desktop songs added as their own rows, and how many more the phone already has
    /// as its own files (linked: one song, the local file plays, see <see cref="UpdateLinks"/>).</returns>
    private async Task<(List<Track> Tracks, int Linked)> ImportCatalogAsync(List<RemoteSong> songs, Dictionary<Guid, string> albumArtists,
        bool keepUnlisted, CancellationToken ct)
    {
        var known = new HashSet<Guid>(_library.Tracks.Where(t => t.SourceType == SourceType.NoctisServer).Select(t => t.Id));
        if (songs.Count == 0 && known.Count > 0)
        {
            // Same rule as a scan: an empty answer never wipes a populated set. A desktop that is
            // still loading its library at startup lists nothing for a moment.
            DebugLog.Write("Account", $"catalog: the desktop listed no songs; keeping the {known.Count} already here");
            int kept;
            lock (_gate) kept = _sync.Links.Count;
            return (_library.Tracks.Where(t => t.SourceType == SourceType.NoctisServer).ToList(), kept);
        }
        var links = UpdateLinks(songs, keepUnlisted, out var fresh);
        var now = UtcNow;
        var tracks = new List<Track>(songs.Count);
        lock (_gate)
        {
            foreach (var song in songs)
            {
                // The phone has this one as a file of its own: no second row.
                if (links.ContainsKey(song.Id)) continue;
                var track = MapSong(song, albumArtists, now);
                _suffixes[song.Id] = song.Suffix;
                if (song.Size > 0) _sizes[song.Id] = song.Size;
                if (!known.Contains(song.Id))
                {
                    // New to the phone: the catalog's state (an unsent phone edit wins).
                    var hex = song.Id.ToString("N");
                    if (_pending.Tracks.TryGetValue(hex, out var p))
                    {
                        track.IsFavorite = p.Favorite;
                        if (p.Favorite && p.FavoritedAt is { } at) track.FavoritedAt = at;
                        track.Rating = p.Rating;
                        track.IsDisliked = p.Disliked;
                    }
                    else
                    {
                        track.IsFavorite = song.Starred;
                        track.Rating = song.UserRating;
                        SetBaselineLocked(hex, song.Starred, song.UserRating, false);
                    }
                    track.PlayCount = song.PlayCount;
                }
                tracks.Add(track);
            }
        }
        if (keepUnlisted)
        {
            var listed = new HashSet<Guid>(songs.Select(s => s.Id));
            var unlisted = _library.Tracks.Where(t => t.SourceType == SourceType.NoctisServer && !listed.Contains(t.Id) && !links.ContainsKey(t.Id)).ToList();
            if (unlisted.Count > 0) DebugLog.Write("Account", $"catalog: keeping {unlisted.Count} songs the desktop did not list this time");
            tracks.AddRange(unlisted);
        }
        SaveSyncState();
        await _library.ReplaceRemoteTracksAsync(tracks, ct).ConfigureAwait(false);
        if (links.Count > 0) DiscardDownloadsOf(links.Keys);

        // Known songs: the catalog's favorite/rating (unless an edit is pending); counts only grow.
        // A song just linked to a file of the phone's own: that file takes the desktop's state and
        // keeps what the phone had where the desktop has nothing.
        var updates = new List<(Track Track, bool Favorite, int Rating, bool? Disliked, int PlayCount, bool Preferences)>();
        var pendingChanged = false;
        lock (_gate)
        {
            foreach (var song in songs)
            {
                var hex = song.Id.ToString("N");
                if (links.TryGetValue(song.Id, out var own) && fresh.Contains(song.Id))
                {
                    bool fav, disliked;
                    int rating;
                    if (_pending.Tracks.TryGetValue(hex, out var p)) (fav, rating, disliked) = (p.Favorite, p.Rating, p.Disliked);
                    else
                    {
                        (fav, rating, disliked) = (own.IsFavorite || song.Starred, song.UserRating > 0 ? song.UserRating : own.Rating, own.IsDisliked);
                        SetBaselineLocked(hex, song.Starred, song.UserRating, false);
                        if (fav != song.Starred || rating != song.UserRating || disliked)
                        {
                            _pending.Tracks[hex] = new PendingTrackState(fav, rating, disliked,
                                fav ? own.FavoritedAt ?? now : null, now);
                            pendingChanged = true;
                        }
                    }
                    updates.Add((own, fav, rating, disliked, song.PlayCount, true));
                    continue;
                }
                var lib = links.TryGetValue(song.Id, out var linkedTrack) ? linkedTrack
                    : known.Contains(song.Id) ? _library.GetTrackById(song.Id) : null;
                if (lib is null || (lib.SourceType != SourceType.NoctisServer && !links.ContainsKey(song.Id))) continue;
                var preferences = !_pending.Tracks.ContainsKey(hex);
                if (preferences) SetBaselineLocked(hex, song.Starred, song.UserRating, lib.IsDisliked);
                if ((preferences && (lib.IsFavorite != song.Starred || lib.Rating != song.UserRating)) || song.PlayCount > lib.PlayCount)
                    updates.Add((lib, song.Starred, song.UserRating, null, song.PlayCount, preferences));
            }
        }
        if (pendingChanged) SchedulePendingSave();
        if (updates.Count > 0)
        {
            SaveSyncState();
            var favoriteChanged = new List<Track>();
            await OnUiAsync(() =>
            {
                foreach (var (t, fav, rating, disliked, count, preferences) in updates)
                {
                    if (preferences)
                    {
                        if (t.IsFavorite != fav) { t.IsFavorite = fav; favoriteChanged.Add(t); }
                        t.Rating = rating;
                        if (disliked is { } d) t.IsDisliked = d;
                    }
                    if (count > t.PlayCount) t.PlayCount = count;
                }
            }).ConfigureAwait(false);
            await _library.SaveTrackUserStateAsync(updates.Select(u => u.Track).ToList()).ConfigureAwait(false);
            if (favoriteChanged.Count > 0) await OnUiAsync(() => _library.NotifyFavoritesChanged(favoriteChanged)).ConfigureAwait(false);
        }
        return (tracks, links.Count);
    }

    /// <summary>
    /// Pairs desktop songs with the phone's own files (<see cref="NoctisLocalMatcher"/>) and records
    /// the pairs in the sync state. A pair ends when its file left the library or the desktop dropped
    /// the song (its id goes to <see cref="NoctisSyncState.Retired"/> until the playlists are pointed
    /// back); the desktop song then shows up as its own row again and streams. Returns desktop id ->
    /// the phone's track; <paramref name="fresh"/> holds the pairs formed by this call.
    /// </summary>
    private Dictionary<Guid, Track> UpdateLinks(List<RemoteSong> songs, bool keepUnlisted, out HashSet<Guid> fresh)
    {
        var sticky = new Dictionary<Guid, Guid>();
        lock (_gate)
            foreach (var (d, l) in _sync.Links)
                if (NoctisRemoteIds.TryParseSyncId(d, out var did) && NoctisRemoteIds.TryParseSyncId(l, out var lid)) sticky[did] = lid;

        var matched = NoctisLocalMatcher.Match(songs, _library.Tracks, sticky);
        if (keepUnlisted)
        {
            // A partial catalog proves nothing about the songs it left out: their pairs stay.
            var listed = new HashSet<Guid>(songs.Select(s => s.Id));
            var taken = new HashSet<Guid>(matched.Values);
            foreach (var (d, l) in sticky)
                if (!listed.Contains(d) && _library.GetTrackById(l) is { SourceType: SourceType.Local } && taken.Add(l)) matched[d] = l;
        }

        var result = new Dictionary<Guid, Track>(matched.Count);
        foreach (var (d, l) in matched)
            if (_library.GetTrackById(l) is { SourceType: SourceType.Local } t) result[d] = t;
        fresh = new HashSet<Guid>(result.Keys.Where(d => !sticky.TryGetValue(d, out var old) || old != result[d].Id));

        lock (_gate)
        {
            foreach (var (d, l) in sticky)
                if (!result.TryGetValue(d, out var t) || t.Id != l) _sync.Retired[l.ToString("N")] = d.ToString("N");
            _sync.Links = result.ToDictionary(kv => kv.Key.ToString("N"), kv => kv.Value.Id.ToString("N"));
            foreach (var t in result.Values) _sync.Retired.Remove(t.Id.ToString("N"));
            RebuildLinkIndexLocked();
        }
        if (fresh.Count > 0 || sticky.Count != result.Count)
            DebugLog.Write("Account", $"catalog: {result.Count} desktop songs are files already on the phone ({fresh.Count} new)");
        return result;
    }

    private void RebuildLinkIndexLocked()
    {
        var map = new Dictionary<Guid, Guid>(_sync.Links.Count);
        foreach (var (d, l) in _sync.Links)
            if (NoctisRemoteIds.TryParseSyncId(d, out var did) && NoctisRemoteIds.TryParseSyncId(l, out var lid)) map[lid] = did;
        _linkedByLocal = map;
    }

    /// <summary>The desktop song a track stands for: itself when it is a desktop row, or the desktop
    /// song the phone's own file was paired with. Null for a song only the phone has.</summary>
    public Guid? DesktopIdOf(Track track)
    {
        if (track is null) return null;
        if (IsRemote(track)) return track.Id;
        if (track.SourceType != SourceType.Local) return null;
        lock (_gate) return _linkedByLocal.TryGetValue(track.Id, out var d) ? d : null;
    }

    /// <summary>The phone's track a synced id points at: the desktop row, or the file paired with it.</summary>
    private Track? ResolveSyncTarget(Guid desktopId)
    {
        var lib = _library.GetTrackById(desktopId);
        if (lib is { SourceType: SourceType.NoctisServer }) return lib;
        if (_sync.Links.TryGetValue(desktopId.ToString("N"), out var l) && NoctisRemoteIds.TryParseSyncId(l, out var lid)
            && _library.GetTrackById(lid) is { SourceType: SourceType.Local } own) return own;
        return null;
    }

    /// <summary>A desktop song that now plays from a file on the phone has no use for its download.</summary>
    private void DiscardDownloadsOf(IEnumerable<Guid> ids)
    {
        foreach (var id in ids)
        {
            DownloadedFile? f;
            lock (_downloadCommit) _downloaded.TryRemove(id, out f);
            if (f != null) NoctisServerClient.TryDelete(f.Path);
        }
    }

    private static Track MapSong(RemoteSong s, Dictionary<Guid, string> albumArtists, DateTime now)
    {
        var artist = string.IsNullOrWhiteSpace(s.Artist) ? "Unknown Artist" : s.Artist;
        var album = string.IsNullOrWhiteSpace(s.Album) ? "Unknown Album" : s.Album;
        var albumArtist = s.AlbumId is { } a && albumArtists.TryGetValue(a, out var aa) && !string.IsNullOrWhiteSpace(aa) ? aa : artist;
        var created = s.Created is { } c && c <= now ? c : now;
        return new Track
        {
            Id = s.Id,
            FilePath = NoctisRemoteIds.ToPath(s.Id),
            SourceType = SourceType.NoctisServer,
            SourceTrackId = NoctisRemoteIds.ToServerTrackId(s.Id),
            Title = string.IsNullOrWhiteSpace(s.Title) ? "Unknown Title" : s.Title,
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            // The desktop's own album id, so its albums group (and their covers file) the same way here.
            AlbumId = s.AlbumId ?? Track.ComputeAlbumId(albumArtist, album),
            Duration = TimeSpan.FromSeconds(s.DurationSeconds),
            TrackNumber = s.Track,
            DiscNumber = s.Disc > 0 ? s.Disc : 1,
            Year = s.Year,
            Genre = s.Genre,
            Bitrate = s.BitRate,
            SampleRate = s.SamplingRate,
            FileSize = s.Size,
            Codec = CodecFor(s.Suffix, s.BitRate),
            DateAdded = created,
            LastModified = created,
        };
    }

    /// <summary>A codec label Track's badge logic understands (it cannot look at a remote path's extension).</summary>
    internal static string CodecFor(string suffix, int bitrate) => suffix switch
    {
        "flac" => "FLAC",
        "mp3" => "MP3",
        // The server does not say whether an MP4 holds ALAC or AAC; ALAC runs far above AAC's rates.
        "m4a" or "mp4" or "m4b" => bitrate > 600 ? "ALAC" : "AAC",
        "alac" => "ALAC",
        "aac" => "AAC",
        "ogg" or "oga" => "Vorbis",
        "opus" => "Opus",
        "wav" => "PCM",
        "aiff" or "aif" => "AIFF (PCM)",
        "wma" => "WMA",
        "ape" => "Monkey's Audio",
        "wv" => "WavPack",
        "dsf" or "dff" => "DSD",
        _ => suffix.ToUpperInvariant(),
    };

    private async Task SyncCoversAsync(NoctisServerClient client, List<Track> tracks, CancellationToken ct)
    {
        var now = UtcNow;
        var due = tracks.Select(t => t.AlbumId).Distinct()
            .Where(id => id != Track.UnknownAlbumBucketId && !_coverMisses.ContainsKey(id))
            .Where(id => CoverIsDue(_persistence.GetArtworkPath(id), now))
            .ToList();
        RaiseSyncProgress(NoctisSyncStage.Covers, 0, due.Count);
        if (due.Count == 0) return;

        var done = 0;
        var written = 0;
        var failed = 0;
        var full = false;
        // The catalog decides how many covers are written (one per album, each capped on its
        // own), so they keep the downloads' reserve too, or a desktop could fill the phone with them.
        var coverDir = Path.GetDirectoryName(_persistence.GetArtworkPath(due[0])) ?? _offlineDir;
        // Covers are not worth a sync: a cover that fails (a timeout, a dropped connection) is
        // skipped and asked again next sync, and the favourites, playlists and plays still go.
        // Many failures mean the desktop is gone; stop asking and let the next stage say so.
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = CoverConcurrency, CancellationToken = stage.Token },
                async (albumId, token) =>
                {
                    if (FreeSpace(coverDir) < StorageReserveBytes)
                    {
                        full = true;
                        stage.Cancel();
                        return;
                    }
                    try
                    {
                        if (await client.DownloadCoverAsync(albumId, _persistence.GetArtworkPath(albumId), token).ConfigureAwait(false))
                            Interlocked.Increment(ref written);
                        else
                            _coverMisses.TryAdd(albumId, 0);
                    }
                    catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.Server)
                    {
                        _coverMisses.TryAdd(albumId, 0);
                    }
                    catch (NoctisServerException ex) when (ex.Kind is not (NoctisErrorKind.SignedOut or NoctisErrorKind.CertificateChanged))
                    {
                        if (Interlocked.Increment(ref failed) >= CoverFailureLimit) stage.Cancel();
                    }
                    RaiseSyncProgress(NoctisSyncStage.Covers, Interlocked.Increment(ref done), due.Count);
                }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* gave up after CoverFailureLimit, or the phone is full */ }
        finally
        {
            if (full) DebugLog.Write("Account", $"covers: {written} saved, then stopped: the phone is too full (the reserve stays free)");
            else if (failed > 0) DebugLog.Write("Account", $"covers: {written} saved, {failed} failed{(stage.IsCancellationRequested && !ct.IsCancellationRequested ? ", stopped asking" : "")}");
            // Rebuild so albums pick the new files up.
            if (written > 0) _library.NotifyMetadataChanged();
        }
    }

    /// <summary>The desktop's profile name for the header and the computer's name (getNoctisSyncStatus).
    /// Known = false when the ask failed (unreachable, a desktop without it): the names shown stay.</summary>
    private static async Task<(bool Known, string? Name, string? ServerName, ComputerScrobbling? Scrobbling)> FetchDisplayNameAsync(NoctisServerClient client, CancellationToken ct)
    {
        try
        {
            var (name, server, scrobbling) = await client.GetDisplayNameAsync(ct).ConfigureAwait(false);
            return (true, name, server, scrobbling);
        }
        catch (NoctisServerException ex)
        {
            DebugLog.Write("Account", $"profile name not fetched: {ex.Kind}");
            return (false, null, null, null);
        }
    }

    private static bool CoverIsDue(string path, DateTime now)
    {
        try
        {
            return !File.Exists(path) || now - File.GetLastWriteTimeUtc(path) > CoverMaxAge;
        }
        catch { return true; }
    }

    /// <summary>Pulls the ledger after the checkpoint (looping while the server says there is
    /// more), applies song states, collects playlist states. Returns the applied count and the
    /// new checkpoint (not yet saved).</summary>
    private async Task<(int Applied, long Checkpoint)> PullAsync(NoctisServerClient client, Dictionary<Guid, PlaylistSyncState> playlists,
        CancellationToken ct)
    {
        long since;
        lock (_gate) since = _sync.Seq;
        // From the start (first sign-in, or again after a sign-out removed the desktop's songs
        // and playlists): our own entries are the only copy of what this phone last edited, so
        // they apply too. Later pulls skip them: the phone already has what it pushed.
        var fromStart = since == 0;
        var applied = 0;
        for (var round = 0; round < 1000; round++)
        {
            var page = await client.GetSyncChangesAsync(since, _deviceId, DeviceName, ct).ConfigureAwait(false);
            var trackItems = new List<(Guid Id, PulledItem Item)>();
            var maxSeq = since;
            foreach (var item in page.Items)
            {
                maxSeq = Math.Max(maxSeq, item.Seq);
                // Our own pushes echo back: nothing to apply.
                if (!fromStart && string.Equals(item.Device, _deviceId, StringComparison.Ordinal)) continue;
                if (!NoctisRemoteIds.TryParseSyncId(item.Id, out var id)) continue;
                switch (item.Kind)
                {
                    case SyncKinds.Track:
                        trackItems.Add((id, item));
                        break;
                    case SyncKinds.Playlist when ParsePlaylistState(item.Payload, item.UpdatedUtc) is { } state:
                        playlists[id] = state; // pages are in seq order: the later one is newer
                        break;
                }
            }
            applied += await ApplyTrackItemsAsync(trackItems).ConfigureAwait(false);
            RaiseSyncProgress(NoctisSyncStage.State, applied, 0);

            var next = page.Items.Count > 0 ? maxSeq : since;
            // "more" comes from servers that page the ledger; an older server truncates silently,
            // which shows as a last item below the reported sequence.
            var more = page.More || (page.Items.Count > 0 && maxSeq < page.Seq);
            if (!more || next <= since) return (applied, next);
            since = next;
        }
        return (applied, since);
    }

    private async Task<int> ApplyTrackItemsAsync(List<(Guid Id, PulledItem Item)> items)
    {
        if (items.Count == 0) return 0;
        var now = UtcNow;
        var updates = new List<(Track Track, TrackSyncState State, bool Preferences)>();
        var pendingChanged = false;
        lock (_gate)
        {
            foreach (var (id, item) in items)
            {
                var lib = ResolveSyncTarget(id);
                if (lib is null) continue;
                var state = ParseTrackState(item.Payload, now);
                var hex = id.ToString("N");
                var preferences = true;
                if (_pending.Tracks.TryGetValue(hex, out var p))
                {
                    // An unsent phone edit newer than this item wins (it is pushed next).
                    if (p.UpdatedUtc >= item.UpdatedUtc) preferences = false;
                    else { _pending.Tracks.Remove(hex); pendingChanged = true; }
                }
                // Echo guard first: the library save below must not look like a phone edit.
                if (preferences) SetBaselineLocked(hex, state.Favorite, state.Rating, state.Disliked);
                updates.Add((lib, state, preferences));
            }
        }
        if (pendingChanged) SchedulePendingSave();
        if (updates.Count == 0) return 0;
        SaveSyncState();

        var changed = new List<Track>();
        var favoriteChanged = new List<Track>();
        await OnUiAsync(() =>
        {
            foreach (var (t, s, preferences) in updates)
            {
                var any = false;
                if (preferences)
                {
                    if (t.IsFavorite != s.Favorite) { t.IsFavorite = s.Favorite; favoriteChanged.Add(t); any = true; }
                    if (s.Favorite && s.FavoritedAt is { } at && t.FavoritedAt != at) { t.FavoritedAt = at; any = true; }
                    if (t.Rating != s.Rating) { t.Rating = s.Rating; any = true; }
                    if (t.IsDisliked != s.Disliked) { t.IsDisliked = s.Disliked; any = true; }
                }
                // Counts only grow (the desktop's rule): a device that missed plays never rolls them back.
                if (s.PlayCount > t.PlayCount) { t.PlayCount = s.PlayCount; any = true; }
                if (s.LastPlayed is { } lp && (t.LastPlayed is null || lp > t.LastPlayed)) { t.LastPlayed = lp; any = true; }
                if (any) changed.Add(t);
            }
        }).ConfigureAwait(false);
        if (changed.Count > 0) await _library.SaveTrackUserStateAsync(changed).ConfigureAwait(false);
        if (favoriteChanged.Count > 0) await OnUiAsync(() => _library.NotifyFavoritesChanged(favoriteChanged)).ConfigureAwait(false);
        return changed.Count;
    }

    /// <summary>Untrusted payload → a clamped state (rating 0..5, counts ≥ 0, times not in the future).</summary>
    internal static TrackSyncState ParseTrackState(JsonElement p, DateTime now)
    {
        static DateTime? NotFuture(DateTime? d, DateTime now) => d is { } v ? (v > now ? now : v) : null;
        return new TrackSyncState(
            NoctisServerClient.Bool(p, "favorite"),
            Math.Clamp(NoctisServerClient.Int(p, "rating"), 0, 5),
            NoctisServerClient.Bool(p, "disliked"),
            Math.Max(0, NoctisServerClient.Int(p, "playCount")),
            NotFuture(NoctisServerClient.Date(p, "lastPlayed"), now),
            NotFuture(NoctisServerClient.Date(p, "favoritedAt"), now));
    }

    /// <summary>Untrusted payload → a playlist state: capped strings, only valid non-empty track Guids.</summary>
    internal PlaylistSyncState? ParsePlaylistState(JsonElement p, DateTime updatedUtc)
    {
        if (p.ValueKind != JsonValueKind.Object) return null;
        var now = UtcNow;
        var ids = new List<Guid>();
        if (p.TryGetProperty("trackIds", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in arr.EnumerateArray())
            {
                if (ids.Count >= MaxPlaylistTracks) break;
                if (e.ValueKind != JsonValueKind.String) continue;
                var s = e.GetString();
                if ((Guid.TryParseExact(s, "D", out var g) || Guid.TryParseExact(s, "N", out g)) && g != Guid.Empty) ids.Add(g);
            }
        }
        var modified = NoctisServerClient.Date(p, "modifiedAt") ?? updatedUtc;
        if (modified > now) modified = now;
        return new PlaylistSyncState(
            NoctisServerClient.Cap(NoctisServerClient.Str(p, "name"), MaxPlaylistName) ?? string.Empty,
            NoctisServerClient.Cap(NoctisServerClient.Str(p, "description"), MaxPlaylistDescription) ?? string.Empty,
            NoctisServerClient.Cap(NoctisServerClient.Str(p, "color"), 32) ?? string.Empty,
            ids, modified, NoctisServerClient.Bool(p, "deleted"));
    }

    private async Task<int> PushTrackStatesAsync(NoctisServerClient client, CancellationToken ct)
    {
        List<KeyValuePair<string, PendingTrackState>> pending;
        lock (_gate) pending = _pending.Tracks.Where(kv => NoctisRemoteIds.TryParseSyncId(kv.Key, out _)).ToList();
        if (pending.Count == 0) return 0;
        var now = UtcNow;
        var sent = 0;
        var all = pending.Select(kv => (Entry: kv, Item: new PushItem(SyncKinds.Track, kv.Key.ToLowerInvariant(),
            new TrackSyncState(kv.Value.Favorite, Math.Clamp(kv.Value.Rating, 0, 5), kv.Value.Disliked,
                // Plays travel as scrobbles: 0 and no date leave the desktop's max-merge untouched.
                PlayCount: 0, LastPlayed: null,
                FavoritedAt: kv.Value.Favorite ? NotAfter(kv.Value.FavoritedAt, now) : null),
            NotAfter(kv.Value.UpdatedUtc, now) ?? now)));
        foreach (var batch in PushChunks(all, x => x.Item))
        {
            var items = batch.Select(x => x.Item).ToList();
            var chunk = batch.Select(x => x.Entry).ToList();
            await client.PushSyncChangesAsync(_deviceId, DeviceName, items, ct).ConfigureAwait(false);
            lock (_gate)
            {
                foreach (var (hex, state) in chunk)
                {
                    // Only if untouched since the snapshot: a newer edit stays queued.
                    if (_pending.Tracks.TryGetValue(hex, out var current) && ReferenceEquals(current, state))
                        _pending.Tracks.Remove(hex);
                    SetBaselineLocked(hex, state.Favorite, state.Rating, state.Disliked);
                }
            }
            sent += items.Count;
            SchedulePendingSave();
            SaveSyncState();
        }
        return sent;
    }

    /// <summary>
    /// Desktop playlists on the phone: pulled states apply unless the phone edited that playlist
    /// later (ModifiedAt, last writer wins); a synced playlist missing or older on the phone is
    /// restored from the last agreed copy (the phone has no playlist delete, so "missing" means a
    /// stale in-memory save dropped it); phone edits of synced playlists are pushed. Playlists
    /// created on the phone stay on the phone. Returns (desktop playlists on the phone, pushed).
    /// </summary>
    private async Task<(int Count, int Pushed)> SyncPlaylistsAsync(NoctisServerClient client, Dictionary<Guid, PlaylistSyncState> pulled,
        CancellationToken ct)
    {
        var phone = await _persistence.LoadPlaylistsAsync().ConfigureAwait(false);
        Dictionary<string, PlaylistSyncState> agreed;
        Dictionary<string, DateTime> deleted;
        lock (_gate)
        {
            agreed = new Dictionary<string, PlaylistSyncState>(_sync.Playlists, StringComparer.OrdinalIgnoreCase);
            deleted = new Dictionary<string, DateTime>(_pending.DeletedPlaylists, StringComparer.OrdinalIgnoreCase);
        }
        var now = UtcNow;
        var changed = false;

        // Deleted on the phone: gone from the phone's list even if a stale save brought it back,
        // unless the computer edited it after the delete (last writer wins, the edit is newer).
        var resolved = new List<string>();
        foreach (var (hex, at) in deleted)
        {
            if (!NoctisRemoteIds.TryParseSyncId(hex, out var id) || !agreed.ContainsKey(hex)) { resolved.Add(hex); continue; }
            if (pulled.TryGetValue(id, out var state) && (state.Deleted || Newer(state.ModifiedAt, at))) { resolved.Add(hex); continue; }
            pulled.Remove(id);
            if (phone.RemoveAll(p => p.Id == id) > 0) changed = true;
        }
        foreach (var hex in resolved) deleted.Remove(hex);

        foreach (var (id, state) in pulled)
        {
            var hex = id.ToString("N");
            var local = phone.FirstOrDefault(p => p.Id == id);
            var phoneEdited = local is not null && agreed.TryGetValue(hex, out var last) && Newer(local.ModifiedAt, last.ModifiedAt);
            if (phoneEdited && !Newer(state.ModifiedAt, local!.ModifiedAt)) continue;
            if (state.Deleted)
            {
                if (local is not null) { phone.Remove(local); changed = true; }
                agreed.Remove(hex);
                continue;
            }
            if (local is null)
            {
                local = new Playlist { Id = id, CreatedAt = state.ModifiedAt };
                phone.Add(local);
            }
            ApplyPlaylistState(local, state);
            agreed[hex] = state;
            changed = true;
        }

        foreach (var (hex, state) in agreed.ToList())
        {
            if (!NoctisRemoteIds.TryParseSyncId(hex, out var id) || state.Deleted) { agreed.Remove(hex); continue; }
            if (pulled.ContainsKey(id) || deleted.ContainsKey(hex)) continue;
            var local = phone.FirstOrDefault(p => p.Id == id);
            if (local is null)
            {
                local = new Playlist { Id = id, CreatedAt = state.ModifiedAt };
                phone.Add(local);
                ApplyPlaylistState(local, state);
                changed = true;
            }
            else if (Newer(state.ModifiedAt, local.ModifiedAt))
            {
                ApplyPlaylistState(local, state);
                changed = true;
            }
        }

        if (RepointPlaylists(phone, agreed)) changed = true;

        var toPush = new List<(string Hex, PlaylistSyncState State)>();
        foreach (var (hex, at) in deleted)
            toPush.Add((hex, PlaylistSyncState.Tombstone(Guid.Empty, NotAfter(Utc(at), now) ?? now)));
        foreach (var (hex, state) in agreed)
        {
            if (deleted.ContainsKey(hex)) continue;
            var local = phone.FirstOrDefault(p => NoctisRemoteIds.TryParseSyncId(hex, out var id) && p.Id == id);
            if (local is null || !Newer(local.ModifiedAt, state.ModifiedAt)) continue;
            var modified = NotAfter(Utc(local.ModifiedAt), now) ?? now;
            // Within the server's limits, or it drops the whole item: name ≤ 200, description
            // ≤ 2000, ≤ 10000 songs, colour #RRGGBB[AA] or none (none keeps the desktop's).
            toPush.Add((hex, new PlaylistSyncState(
                NoctisServerClient.Cap(local.Name ?? string.Empty, MaxPlaylistName)!,
                NoctisServerClient.Cap(local.Description ?? string.Empty, MaxPlaylistDescription)!,
                IsSyncColor(local.Color) ? local.Color! : string.Empty,
                DesktopTrackIds(local.TrackIds),
                modified, Deleted: false)));
        }

        if (changed)
        {
            await _persistence.SavePlaylistsAsync(phone).ConfigureAwait(false);
            RaisePlaylistsChanged();
        }

        foreach (var chunk in PushChunks(toPush, p => new PushItem(SyncKinds.Playlist, p.Hex.ToLowerInvariant(), p.State, p.State.ModifiedAt)))
        {
            var items = chunk.Select(p => new PushItem(SyncKinds.Playlist, p.Hex.ToLowerInvariant(), p.State, p.State.ModifiedAt)).ToList();
            await client.PushSyncChangesAsync(_deviceId, DeviceName, items, ct).ConfigureAwait(false);
            var tombstones = false;
            foreach (var (hex, state) in chunk)
            {
                if (!state.Deleted)
                {
                    agreed[hex] = state;
                    continue;
                }
                // Deleted on the computer now: no longer one of the playlists the phone keeps in step.
                agreed.Remove(hex);
                lock (_gate)
                {
                    if (_pending.DeletedPlaylists.TryGetValue(hex, out var at) && at == deleted[hex])
                        _pending.DeletedPlaylists.Remove(hex);
                }
                tombstones = true;
            }
            if (tombstones) SchedulePendingSave();
        }

        lock (_gate)
        {
            _sync.Playlists = agreed;
            // Settled without a push (the computer deleted or re-edited it first): not asked again.
            foreach (var hex in resolved) _pending.DeletedPlaylists.Remove(hex);
        }
        if (resolved.Count > 0) SchedulePendingSave();
        return (agreed.Count, toPush.Count);
    }

    /// <summary>
    /// A desktop playlist lists the desktop's song ids. A song the phone has as a file of its own
    /// shows once, as that file, so the playlist points at the file's id; when the pair ends (the
    /// file was deleted, or sign-in changed) it points back at the desktop song.
    /// </summary>
    private bool RepointPlaylists(List<Playlist> phone, Dictionary<string, PlaylistSyncState> agreed)
    {
        Dictionary<Guid, Guid> toLocal = new(), retired = new();
        lock (_gate)
        {
            foreach (var (d, l) in _sync.Links)
                if (NoctisRemoteIds.TryParseSyncId(d, out var did) && NoctisRemoteIds.TryParseSyncId(l, out var lid)) toLocal[did] = lid;
            foreach (var (l, d) in _sync.Retired)
                if (NoctisRemoteIds.TryParseSyncId(l, out var lid) && NoctisRemoteIds.TryParseSyncId(d, out var did)) retired[lid] = did;
        }
        if (toLocal.Count == 0 && retired.Count == 0) return false;
        var changed = false;
        foreach (var playlist in phone)
        {
            if (!agreed.ContainsKey(playlist.Id.ToString("N"))) continue;
            var ids = playlist.TrackIds;
            for (var i = 0; i < ids.Count; i++)
            {
                if (toLocal.TryGetValue(ids[i], out var local)) { ids[i] = local; changed = true; }
                else if (retired.TryGetValue(ids[i], out var desktop) && _library.GetTrackById(desktop) is not null) { ids[i] = desktop; changed = true; }
            }
        }
        lock (_gate)
        {
            // Pointed back wherever the desktop song is on the phone again; the rest wait for it.
            foreach (var (l, d) in retired)
                if (_library.GetTrackById(d) is not null) _sync.Retired.Remove(l.ToString("N"));
        }
        return changed;
    }

    /// <summary>A phone playlist's songs as the desktop knows them (a paired file under its desktop song's id).</summary>
    private List<Guid> DesktopTrackIds(IEnumerable<Guid> ids)
    {
        Dictionary<Guid, Guid> map;
        lock (_gate) map = _linkedByLocal;
        return ids.Where(g => g != Guid.Empty).Take(MaxPlaylistTracks).Select(g => map.TryGetValue(g, out var d) ? d : g).ToList();
    }

    private static bool IsSyncColor(string? color) =>
        color is { Length: 7 or 9 } && color[0] == '#' && color.AsSpan(1).ContainsAnyExcept("0123456789abcdefABCDEF") == false;

    /// <summary>
    /// Splits a push into requests the server accepts: at most <see cref="PushChunk"/> items and
    /// well under its 4 MB body limit (a playlist can carry 10000 ids, ~400 KB).
    /// </summary>
    internal static IEnumerable<List<T>> PushChunks<T>(IEnumerable<T> source, Func<T, PushItem> item)
    {
        var chunk = new List<T>();
        long bytes = 0;
        foreach (var entry in source)
        {
            var size = System.Text.Encoding.UTF8.GetByteCount(SyncJson.Serialize(item(entry).Payload)) + 200L;
            if (chunk.Count > 0 && (chunk.Count >= PushChunk || bytes + size > MaxPushBytes))
            {
                yield return chunk;
                chunk = new List<T>();
                bytes = 0;
            }
            chunk.Add(entry);
            bytes += size;
        }
        if (chunk.Count > 0) yield return chunk;
    }

    private static void ApplyPlaylistState(Playlist local, PlaylistSyncState state)
    {
        if (!string.IsNullOrWhiteSpace(state.Name)) local.Name = state.Name;
        local.Description = state.Description ?? string.Empty;
        if (IsSyncColor(state.Color)) local.Color = state.Color;
        local.TrackIds = state.TrackIds?.Where(g => g != Guid.Empty).Take(MaxPlaylistTracks).ToList() ?? new List<Guid>();
        local.ModifiedAt = Utc(state.ModifiedAt);
    }

    private async Task<int> SendPlaysAsync(NoctisServerClient client, CancellationToken ct)
    {
        List<PendingPlay> plays;
        // A play still waiting for its verdict (the song is playing) stays for a later sync.
        lock (_gate) plays = _pending.Plays.Where(p => p.Scrobble != PlayScrobbleWire.Pending).ToList();
        if (plays.Count == 0) return 0;
        RaiseSyncProgress(NoctisSyncStage.Plays, 0, plays.Count);
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var sent = 0;
        foreach (var batch in plays.Chunk(ScrobbleBatch))
        {
            var valid = batch
                .Select(p => (Ok: NoctisRemoteIds.TryParseSyncId(p.Id, out var id), Id: id, Time: Math.Clamp(p.Time, 0, nowMs), p.Scrobble))
                .Where(p => p.Ok).Select(p => (p.Id, p.Time, p.Scrobble)).ToList();
            if (valid.Count > 0) await client.ScrobbleAsync(valid, ct).ConfigureAwait(false);
            var sentSet = new HashSet<PendingPlay>(batch, ReferenceEqualityComparer.Instance);
            lock (_gate) _pending.Plays.RemoveAll(p => sentSet.Contains(p));
            sent += valid.Count;
            SchedulePendingSave();
            RaiseSyncProgress(NoctisSyncStage.Plays, sent, plays.Count);
        }
        return sent;
    }

    // ── Phone-side changes ───────────────────────────────────────────────

    public void RecordPlay(Track track, DateTime playedUtc) => Record(track, playedUtc, null);

    public void RecordStartedPlay(Track track, DateTime playedUtc) => Record(track, playedUtc, PlayScrobbleWire.Pending);

    private void Record(Track track, DateTime playedUtc, string? scrobble)
    {
        if (DesktopIdOf(track) is not { } desktopId) return;
        var utc = playedUtc.Kind == DateTimeKind.Local ? playedUtc.ToUniversalTime() : DateTime.SpecifyKind(playedUtc, DateTimeKind.Utc);
        var ms = Math.Clamp(new DateTimeOffset(utc).ToUnixTimeMilliseconds(), 0, _time.GetUtcNow().ToUnixTimeMilliseconds());
        lock (_gate)
        {
            // One song plays at a time: a play still held when the next starts never counted.
            if (scrobble == PlayScrobbleWire.Pending) DecideHeld(_ => true, PlayScrobble.No);
            _pending.Plays.Add(new PendingPlay(desktopId.ToString("N"), ms, scrobble));
            if (_pending.Plays.Count > MaxQueuedPlays) _pending.Plays.RemoveRange(0, _pending.Plays.Count - MaxQueuedPlays);
        }
        SchedulePendingSave();
    }

    public void DecidePlay(Track track, PlayScrobble verdict)
    {
        if (DesktopIdOf(track) is not { } desktopId) return;
        var id = desktopId.ToString("N");
        bool decided;
        lock (_gate) decided = DecideHeld(p => p.Id == id, verdict, latestOnly: true);
        if (decided) SchedulePendingSave();
    }

    /// <summary>Gives held plays matching <paramref name="match"/> their verdict (the latest only, or
    /// all of them). Under _gate.</summary>
    private bool DecideHeld(Func<PendingPlay, bool> match, PlayScrobble verdict, bool latestOnly = false)
    {
        var any = false;
        for (var i = _pending.Plays.Count - 1; i >= 0; i--)
        {
            var p = _pending.Plays[i];
            if (p.Scrobble != PlayScrobbleWire.Pending || !match(p)) continue;
            _pending.Plays[i] = p with { Scrobble = PlayScrobbleWire.Of(verdict) };
            any = true;
            if (latestOnly) break;
        }
        return any;
    }

    public ComputerScrobbling? ComputerScrobbling
    {
        get { lock (_gate) return _account?.ComputerScrobbling; }
    }

    public Task SavePlayQueueAsync(PlayQueueShare share, CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_account is null) return Task.CompletedTask;
        }
        return GetClient().SavePlayQueueAsync(share, ct);
    }

    public bool IsSyncedPlaylist(Guid playlistId)
    {
        lock (_gate) return _account is not null && _sync.Playlists.ContainsKey(playlistId.ToString("N"));
    }

    /// <summary>A playlist the phone deleted: one of the computer's is queued for a tombstone on
    /// the next sync (SyncPlaylistsAsync), which otherwise restored it as a stale save's loss.</summary>
    public void RecordPlaylistDeleted(Guid playlistId)
    {
        var hex = playlistId.ToString("N");
        lock (_gate)
        {
            if (_account is null || !_sync.Playlists.ContainsKey(hex)) return;
            _pending.DeletedPlaylists[hex] = UtcNow;
        }
        SchedulePendingSave();
    }

    /// <summary>The library saved user state (from <see cref="NoctisStateRecorder"/>, on a worker
    /// thread): a desktop song whose favorite/rating/dislike differs from the last agreed state is
    /// queued for the next sync. Anything else (play-position saves, our own applied pulls) is not.</summary>
    void ITrackStateRecorder.RecordTrackStates(IEnumerable<Track> tracks)
    {
        var now = UtcNow;
        var changed = false;
        foreach (var t in tracks)
        {
            if (t is null || DesktopIdOf(t) is not { } desktopId) continue;
            var hex = desktopId.ToString("N");
            var fav = t.IsFavorite;
            var rating = Math.Clamp(t.Rating, 0, 5);
            var disliked = t.IsDisliked;
            lock (_gate)
            {
                var agreed = _sync.Baseline.TryGetValue(hex, out var b) ? b : Default;
                if (agreed.Favorite == fav && agreed.Rating == rating && agreed.Disliked == disliked)
                {
                    if (_pending.Tracks.Remove(hex)) changed = true;
                    continue;
                }
                if (_pending.Tracks.TryGetValue(hex, out var p) && p.Favorite == fav && p.Rating == rating && p.Disliked == disliked)
                    continue;
                _pending.Tracks[hex] = new PendingTrackState(fav, rating, disliked, fav ? t.FavoritedAt : null, now);
                changed = true;
            }
        }
        if (changed) SchedulePendingSave();
    }

    private static readonly SyncedTrackState Default = new(false, 0, false);

    private void SetBaselineLocked(string hex, bool favorite, int rating, bool disliked)
    {
        if (!favorite && rating == 0 && !disliked) _sync.Baseline.Remove(hex);
        else _sync.Baseline[hex] = new SyncedTrackState(favorite, rating, disliked);
    }

    private void SchedulePendingSave() => _ = Task.Run(SavePendingNow);

    private void SavePendingNow()
    {
        lock (_saveGate)
        {
            NoctisPendingState copy;
            lock (_gate)
            {
                copy = new NoctisPendingState
                {
                    ServerUrl = _pending.ServerUrl,
                    UserName = _pending.UserName,
                    Tracks = new Dictionary<string, PendingTrackState>(_pending.Tracks),
                    Plays = _pending.Plays.ToList(),
                    DeletedPlaylists = new Dictionary<string, DateTime>(_pending.DeletedPlaylists),
                };
            }
            try
            {
                _store.SavePending(copy);
                _pendingSaveFailures = 0;
            }
            catch (Exception ex)
            {
                DebugLog.Write("Account", $"pending save failed ({ex.GetType().Name})");
                // Until a save lands the queued plays live only in memory, so try again shortly
                // instead of waiting for the next change (a process kill in between loses them).
                if (++_pendingSaveFailures <= PendingSaveRetries)
                    _ = Task.Delay(TimeSpan.FromSeconds(_pendingSaveFailures)).ContinueWith(_ => SavePendingNow(), TaskScheduler.Default);
            }
        }
    }

    private const int PendingSaveRetries = 5;
    private int _pendingSaveFailures; // under _saveGate

    private void SaveSyncState()
    {
        lock (_saveGate)
        {
            NoctisSyncState copy;
            lock (_gate)
            {
                copy = new NoctisSyncState
                {
                    ServerUrl = _sync.ServerUrl,
                    UserName = _sync.UserName,
                    Seq = _sync.Seq,
                    Baseline = new Dictionary<string, SyncedTrackState>(_sync.Baseline),
                    Playlists = new Dictionary<string, PlaylistSyncState>(_sync.Playlists),
                    Links = new Dictionary<string, string>(_sync.Links),
                    Retired = new Dictionary<string, string>(_sync.Retired),
                };
            }
            try { _store.SaveSyncState(copy); }
            catch (Exception ex) { DebugLog.Write("Account", $"sync state save failed ({ex.GetType().Name})"); }
        }
    }

    // ── Downloads ────────────────────────────────────────────────────────

    public bool IsRemote(Track track) =>
        track is not null && track.SourceType == SourceType.NoctisServer
        && NoctisRemoteIds.TryParsePath(track.FilePath, out var id) && id == track.Id;

    public bool IsDownloaded(Track track) => IsRemote(track) && _downloaded.ContainsKey(track.Id);

    public long DownloadedBytes => _downloaded.Values.Sum(f => f.Size);

    public int DownloadedCount => _downloaded.Count;

    /// <summary>Whether the phone is on an unmetered network (Wi-Fi). Null = unknown, which never blocks
    /// a download. With Settings → Downloads → Wi-Fi only on, a download asked for on mobile data is refused.</summary>
    public Func<bool>? IsOnWifi { get; init; }

    public async Task DownloadAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        var ids = tracks.Where(IsRemote).Select(t => t.Id).Distinct().Where(id => !_downloaded.ContainsKey(id)).ToList();
        if (ids.Count > 0 && IsOnWifi is { } onWifi && !onWifi()
            && (await _persistence.LoadSettingsAsync().ConfigureAwait(false)).MobileDownloadWifiOnly)
            throw new NoctisServerException(NoctisErrorKind.WifiRequired, "Downloads are set to Wi-Fi only.");
        foreach (var id in ids) _withdrawn.TryRemove(id, out _);   // asked for again: no longer removed
        await RunDownloadsAsync(ids, ct).ConfigureAwait(false);
    }

    public Task DownloadAllAsync(CancellationToken ct = default) => DownloadAsync(_library.Tracks.ToList(), ct);

    private Task RunDownloadsAsync(List<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return Task.CompletedTask;
        NoctisServerClient client;
        try { client = GetClient(); }
        catch (NoctisServerException ex) { return Task.FromException(ex); }

        Task run;
        lock (_downloadRuns)
        {
            var stop = _downloadCts.Token;
            run = Task.Run(() => DownloadBatchAsync(client, ids, ct, stop), CancellationToken.None);
            _downloadRuns.Add(run);
        }
        _ = run.ContinueWith(t => { lock (_downloadRuns) _downloadRuns.Remove(t); }, TaskScheduler.Default);
        // New downloads get their lyrics saved too, so they show with the desktop off.
        _ = run.ContinueWith(_ => PrefetchDownloadedLyricsAsync(CancellationToken.None), TaskScheduler.Default).Unwrap();
        return run;
    }

    private async Task DownloadBatchAsync(NoctisServerClient client, List<Guid> ids, CancellationToken ct, CancellationToken stop)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, stop);
        var token = linked.Token;
        // Wi-Fi only holds for the whole batch, not only when it was asked for: a Download All
        // that loses Wi-Fi stops rather than carrying on over mobile data.
        var onWifi = IsOnWifi;
        var wifiOnly = onWifi != null && (await _persistence.LoadSettingsAsync().ConfigureAwait(false)).MobileDownloadWifiOnly;
        var queue = new ConcurrentQueue<Guid>(ids);
        // With no other batch running, this one counts from zero: "Downloading 3 of 22" and
        // "1 failed" are about this run, not every run since the app started.
        if (Interlocked.Add(ref _dlPending, ids.Count) == ids.Count)
        {
            Interlocked.Exchange(ref _dlCompleted, 0);
            Interlocked.Exchange(ref _dlFailed, 0);
            Interlocked.Exchange(ref _dlMissing, 0);
        }
        RaiseDownloadProgress();
        NoctisServerException? fatal = null;
        int done = 0, failed = 0, missing = 0;

        async Task Worker()
        {
            while (queue.TryDequeue(out var id))
            {
                try
                {
                    // Removed while it waited in this batch's queue: skipped, not downloaded anyway.
                    if (token.IsCancellationRequested || _downloaded.ContainsKey(id) || _withdrawn.ContainsKey(id)) continue;
                    using var song = CancellationTokenSource.CreateLinkedTokenSource(token);
                    if (!_inFlight.TryAdd(id, song)) continue;
                    try
                    {
                        if (_withdrawn.ContainsKey(id)) continue;   // removed just before it was marked in flight
                        await _downloadSlots.WaitAsync(song.Token).ConfigureAwait(false);
                        try
                        {
                            if (wifiOnly && !onWifi!())
                                throw new NoctisServerException(NoctisErrorKind.WifiRequired, "Downloads are set to Wi-Fi only.");
                            await DownloadOneAsync(client, id, song.Token).ConfigureAwait(false);
                            Interlocked.Increment(ref _dlCompleted);
                            Interlocked.Increment(ref done);
                        }
                        finally { _downloadSlots.Release(); }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    // Remove Download on this song mid-transfer: stopped, neither done nor failed.
                    catch (Exception) when (song.IsCancellationRequested || _withdrawn.ContainsKey(id)) { }
                    catch (NoctisServerException ex) when (ex.Kind is NoctisErrorKind.SignedOut or NoctisErrorKind.CertificateChanged or NoctisErrorKind.StorageFull or NoctisErrorKind.WifiRequired)
                    {
                        // Every other song would fail the same way.
                        Interlocked.CompareExchange(ref fatal, ex, null);
                        Interlocked.Increment(ref _dlFailed);
                        Interlocked.Increment(ref failed);
                        try { linked.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.FileMissing)
                    {
                        // The desktop lists the song but its file is gone: nothing to retry here,
                        // and the page says so apart from failures that a retry may fix.
                        Interlocked.Increment(ref _dlMissing);
                        Interlocked.Increment(ref _dlFailed);
                        Interlocked.Increment(ref missing);
                        Interlocked.Increment(ref failed);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _dlFailed);
                        Interlocked.Increment(ref failed);
                        DebugLog.Write("Account", $"download failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
                    }
                    finally { _inFlight.TryRemove(new KeyValuePair<Guid, CancellationTokenSource>(id, song)); }
                }
                finally
                {
                    Interlocked.Decrement(ref _dlPending);
                    RaiseDownloadProgress();
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, DownloadConcurrency).Select(_ => Worker())).ConfigureAwait(false);
        // Once more with every worker done: two workers' last reports can arrive out of order, the
        // later one read before the other finished (a test saw Pending = 1 as the last report of a
        // finished batch), and the page takes Pending > 0 as "still downloading".
        RaiseDownloadProgress();
        // One line per batch, so a download that silently did nothing is visible in the log.
        var missingNote = Volatile.Read(ref missing) is > 0 and var m ? $" ({m} missing on the computer)" : "";
        DebugLog.Write("Account", $"downloads: {ids.Count} asked, {Volatile.Read(ref done)} done, " +
            $"{Volatile.Read(ref failed)} failed{missingNote}{(token.IsCancellationRequested ? ", cancelled" : "")}");
        RaiseStateChanged();
        if (fatal is not null)
        {
            if (fatal.Kind == NoctisErrorKind.SignedOut) ForgetAccountLocally();
            throw fatal;
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task DownloadOneAsync(NoctisServerClient client, Guid id, CancellationToken ct)
    {
        Directory.CreateDirectory(_offlineDir);
        var need = _sizes.GetValueOrDefault(id) + StorageReserveBytes;
        if (FreeSpace(_offlineDir) < need)
            throw new NoctisServerException(NoctisErrorKind.StorageFull, "The phone is too full to download more.");
        var hex = id.ToString("N");
        var part = InsideOffline(hex + ".part");
        try
        {
            var flac = WantsFlac(id);
            // The reserve holds for what arrives too, not only for the size the catalog gave.
            var contentType = await client.DownloadTrackAsync(id, part, ct, flac,
                hasRoom: () => FreeSpace(_offlineDir) >= StorageReserveBytes).ConfigureAwait(false);
            var final = InsideOffline(hex + "." + ExtensionFor(id, contentType, flac));
            if (_downloaded.TryGetValue(id, out var old) && old.Path != final) NoctisServerClient.TryDelete(old.Path);
            File.Move(part, final, overwrite: true);
            bool removed;
            lock (_downloadCommit)
            {
                removed = _withdrawn.ContainsKey(id);
                if (!removed) _downloaded[id] = new DownloadedFile(final, new FileInfo(final).Length);
            }
            if (removed)
            {
                // Remove Download came as the last bytes landed: the file goes, as it asked.
                NoctisServerClient.TryDelete(final);
                throw new OperationCanceledException("The download was removed.");
            }
        }
        finally
        {
            NoctisServerClient.TryDelete(part);
        }
    }

    /// <summary>The download's extension: "flac" when FLAC was asked for and the desktop sent it
    /// (audio/flac), else the catalog's suffix when allow-listed, else the content type's, else
    /// "bin". Never anything the server names (Content-Disposition, path).</summary>
    private string ExtensionFor(Guid id, string? contentType, bool askedFlac = false)
    {
        var type = (contentType ?? string.Empty).ToLowerInvariant();
        if (askedFlac && type is "audio/flac" or "audio/x-flac") return "flac";
        if (_suffixes.TryGetValue(id, out var s) && AllowedExtensions.Contains(s)) return s;
        var ext = type switch
        {
            "audio/mpeg" or "audio/mp3" => "mp3",
            "audio/flac" or "audio/x-flac" => "flac",
            "audio/mp4" or "audio/x-m4a" or "audio/m4a" => "m4a",
            "audio/aac" or "audio/x-aac" => "aac",
            "audio/ogg" or "application/ogg" => "ogg",
            "audio/opus" => "opus",
            "audio/wav" or "audio/x-wav" or "audio/wave" => "wav",
            "audio/aiff" or "audio/x-aiff" => "aiff",
            "audio/x-ms-wma" => "wma",
            "audio/x-ape" => "ape",
            "audio/x-wavpack" => "wv",
            "audio/x-dsf" => "dsf",
            "audio/x-dff" => "dff",
            _ => "bin",
        };
        return ext;
    }

    /// <summary>A path in the offline folder; throws if it would land anywhere else.</summary>
    private string InsideOffline(string fileName)
    {
        var full = Path.GetFullPath(Path.Combine(_offlineDir, fileName));
        var root = _offlineDir.EndsWith(Path.DirectorySeparatorChar) ? _offlineDir : _offlineDir + Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
            || Path.GetDirectoryName(full) != Path.TrimEndingDirectorySeparator(_offlineDir))
            throw new InvalidOperationException("Download path escapes the offline folder.");
        return full;
    }

    public Task RemoveDownloadsAsync(IEnumerable<Track> tracks)
    {
        var ids = tracks.Where(IsRemote).Select(t => t.Id).ToList();
        return Task.Run(() =>
        {
            foreach (var id in ids)
            {
                // A running batch that holds the song skips it, or stops it mid-transfer; removing
                // only the finished file left both to download it anyway.
                DownloadedFile? f;
                lock (_downloadCommit)
                {
                    _withdrawn[id] = 0;
                    _downloaded.TryRemove(id, out f);
                }
                if (f != null) NoctisServerClient.TryDelete(f.Path);
                if (_inFlight.TryGetValue(id, out var song))
                {
                    try { song.Cancel(); }
                    catch (ObjectDisposedException) { /* it just finished */ }
                }
            }
            RaiseDownloadProgress();
            RaiseStateChanged();
        });
    }

    public async Task RemoveAllDownloadsAsync()
    {
        await StopDownloadsAsync().ConfigureAwait(false);
        await Task.Run(DeleteAllDownloadFiles).ConfigureAwait(false);
        RaiseDownloadProgress();
        RaiseStateChanged();
    }

    /// <summary>Cancels running downloads and waits for them to wind down.</summary>
    private async Task StopDownloadsAsync()
    {
        Task[] runs;
        lock (_downloadRuns)
        {
            _downloadCts.Cancel();
            _downloadCts = new CancellationTokenSource();
            runs = _downloadRuns.ToArray();
        }
        try { await Task.WhenAll(runs).ConfigureAwait(false); } catch { /* cancelled */ }
        Interlocked.Exchange(ref _dlCompleted, 0);
        Interlocked.Exchange(ref _dlFailed, 0);
        Interlocked.Exchange(ref _dlMissing, 0);
    }

    private void DeleteAllDownloadFiles()
    {
        _downloaded.Clear();
        try
        {
            if (!Directory.Exists(_offlineDir)) return;
            foreach (var f in Directory.EnumerateFiles(_offlineDir)) NoctisServerClient.TryDelete(f);
        }
        catch (Exception ex) { DebugLog.Write("Account", $"removing downloads failed ({ex.GetType().Name})"); }
    }

    private void DeletePartFiles()
    {
        try
        {
            if (!Directory.Exists(_offlineDir)) return;
            foreach (var f in Directory.EnumerateFiles(_offlineDir, "*.part")) NoctisServerClient.TryDelete(f);
        }
        catch { /* best effort */ }
    }

    /// <summary>What is on disk: "&lt;32 hex&gt;.&lt;allowed ext|bin&gt;" files; leftover .part files are removed.</summary>
    private void IndexDownloads()
    {
        try
        {
            Directory.CreateDirectory(_offlineDir);
            foreach (var file in Directory.EnumerateFiles(_offlineDir))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".part", StringComparison.Ordinal)) { NoctisServerClient.TryDelete(file); continue; }
                var dot = name.IndexOf('.');
                if (dot != 32) continue;
                var ext = name[(dot + 1)..];
                if (!(AllowedExtensions.Contains(ext) || ext == "bin")) continue;
                if (!NoctisRemoteIds.IsLowerHex32(name.AsSpan(0, 32)) || !Guid.TryParseExact(name.AsSpan(0, 32), "N", out var id)) continue;
                _downloaded[id] = new DownloadedFile(Path.GetFullPath(file), new FileInfo(file).Length);
            }
        }
        catch (Exception ex) { DebugLog.Write("Account", $"offline folder unreadable ({ex.GetType().Name})"); }
    }

    // ── Playback ─────────────────────────────────────────────────────────

    public string? ResolvePlaybackUri(string filePath)
    {
        if (!NoctisRemoteIds.TryParsePath(filePath, out var id)) return null;
        if (_downloaded.TryGetValue(id, out var local) && File.Exists(local.Path)) return local.Path;
        var account = _account;
        if (account is null) return null;
        // No key here: the player sends it as the X-Noctis-Key header.
        var url = $"{account.ServerUrl}/rest/stream?id={NoctisRemoteIds.ToServerTrackId(id)}&c={NoctisServerClient.ClientName}&v={NoctisServerClient.ApiVersion}";
        return WantsFlac(id) ? url + "&format=" + NoctisServerClient.FlacFormat : url;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private Task OnUiAsync(Action action)
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _marshal(() =>
        {
            try { action(); tcs.TrySetResult(); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return tcs.Task;
    }

    private static DateTime Utc(DateTime d) =>
        d.Kind == DateTimeKind.Local ? d.ToUniversalTime() : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    private static DateTime? NotAfter(DateTime? d, DateTime now) => d is { } v ? (Utc(v) > now ? now : Utc(v)) : null;

    /// <summary>a is later than b by more than serializer rounding.</summary>
    private static bool Newer(DateTime a, DateTime b) => Utc(a) - Utc(b) > TimeSpan.FromMilliseconds(1);

    private static string SanitizeDeviceName(string? name)
    {
        var clean = new string((name ?? string.Empty).Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length > 64) clean = clean[..64];
        return clean.Length == 0 ? "Android phone" : clean;
    }

    private void RaiseStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    private void RaisePlaylistsChanged() => PlaylistsChanged?.Invoke(this, EventArgs.Empty);

    private void RaiseSyncProgress(NoctisSyncStage stage, int done, int total) =>
        SyncProgress?.Invoke(this, new NoctisSyncProgress(stage, done, total));

    private void RaiseDownloadProgress() =>
        DownloadProgress?.Invoke(this, new NoctisDownloadProgress(Math.Max(0, Volatile.Read(ref _dlPending)),
            Volatile.Read(ref _dlCompleted), Volatile.Read(ref _dlFailed), DownloadedBytes, Volatile.Read(ref _dlMissing)));

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
