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

    private Task<NoctisSyncResult>? _syncTask;
    private CancellationTokenSource? _syncCts;
    private volatile bool _isSyncing;

    private sealed record DownloadedFile(string Path, long Size);
    private readonly ConcurrentDictionary<Guid, DownloadedFile> _downloaded = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _downloadSlots = new(DownloadConcurrency, DownloadConcurrency);
    private readonly List<Task> _downloadRuns = new();
    private CancellationTokenSource _downloadCts = new();
    private int _dlPending, _dlCompleted, _dlFailed;

    /// <summary>Song suffixes from this process's last catalog (download file extensions).</summary>
    private readonly ConcurrentDictionary<Guid, string> _suffixes = new();
    private readonly ConcurrentDictionary<Guid, long> _sizes = new();

    /// <summary>Space downloads always leave free on the phone: "Download everything" on a desktop
    /// library can be far larger than the phone (the owner's is ~306 GB).</summary>
    internal const long StorageReserveBytes = 1536L * 1024 * 1024;

    /// <summary>Free bytes on the volume holding a path; tests substitute a full phone.</summary>
    internal Func<string, long> FreeSpace { get; init; } = DefaultFreeSpace;

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
        _pending = _store.LoadPending();
        IndexDownloads();
        stateRecorder?.Attach(this);
    }

    /// <summary>Tests: unpushed favorite/rating edits and queued plays.</summary>
    internal int PendingTrackCount { get { lock (_gate) return _pending.Tracks.Count; } }
    internal int PendingPlayCount { get { lock (_gate) return _pending.Plays.Count; } }

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

        var account = new NoctisAccount
        {
            ServerUrl = url,
            UserName = string.IsNullOrWhiteSpace(result.User) ? user : result.User!,
            DeviceKey = result.ApiKey,
            Fingerprint = fingerprint,
            DeviceId = _deviceId,
            DeviceName = DeviceName,
            ServerName = result.Server ?? string.Empty,
        };
        lock (_gate)
        {
            // Another server or user: nothing from the old checkpoint or queue applies.
            if (!SameIdentity(_sync.ServerUrl, _sync.UserName, account) || !SameIdentity(_pending.ServerUrl, _pending.UserName, account))
            {
                _sync = new NoctisSyncState { ServerUrl = account.ServerUrl, UserName = account.UserName };
                _pending = new NoctisPendingState { ServerUrl = account.ServerUrl, UserName = account.UserName };
                _store.SaveSyncState(_sync);
                _store.SavePending(_pending);
            }
            _store.SaveAccount(account);
            _client?.Dispose();
            _client = null;
            _account = account;
        }
        DebugLog.Write("Account", $"signed in (sync {(result.SyncEnabled ? "on" : "off")} on the desktop)");
        RaiseStateChanged();
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
            _pending = new NoctisPendingState();
            _client?.Dispose();
            _client = null;
            _account = null;
        }
        RaiseStateChanged();

        var playlistsRemoved = await Task.Run(async () =>
        {
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
    private void ForgetAccountLocally()
    {
        lock (_gate)
        {
            if (_account is null) return;
            _store.DeleteAccount();
            _client?.Dispose();
            _client = null;
            _account = null;
        }
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
        return _client ??= new NoctisServerClient(_handlerFactory, account.ServerUrl, account.Fingerprint, account.DeviceKey);
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
            RaiseSyncProgress(NoctisSyncStage.Failed, 0, 0);
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
        var albumArtists = await client.GetAlbumArtistsAsync(ct).ConfigureAwait(false);
        var (songs, settled) = await FetchCatalogAsync(client, ct).ConfigureAwait(false);
        var tracks = await ImportCatalogAsync(songs, albumArtists, keepUnlisted: !settled, ct).ConfigureAwait(false);
        RaiseSyncProgress(NoctisSyncStage.Catalog, tracks.Count, tracks.Count);

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

        NoctisAccount? updated;
        lock (_gate)
        {
            updated = _account is null ? null : _account with { LastSyncUtc = UtcNow };
            if (updated is not null)
            {
                _store.SaveAccount(updated);
                _account = updated;
            }
        }
        RaiseStateChanged();

        // 6. Lyrics for downloaded songs that have none saved (after LastSyncUtc, so they count as current).
        await PrefetchDownloadedLyricsAsync(ct).ConfigureAwait(false);
        if (syncOff is not null) throw syncOff;
        return new NoctisSyncResult(tracks.Count, playlistCount, pulled, pushed, plays);
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
    private async Task<List<Track>> ImportCatalogAsync(List<RemoteSong> songs, Dictionary<Guid, string> albumArtists,
        bool keepUnlisted, CancellationToken ct)
    {
        var known = new HashSet<Guid>(_library.Tracks.Where(t => t.SourceType == SourceType.NoctisServer).Select(t => t.Id));
        if (songs.Count == 0 && known.Count > 0)
        {
            // Same rule as a scan: an empty answer never wipes a populated set. A desktop that is
            // still loading its library at startup lists nothing for a moment.
            DebugLog.Write("Account", $"catalog: the desktop listed no songs; keeping the {known.Count} already here");
            return _library.Tracks.Where(t => t.SourceType == SourceType.NoctisServer).ToList();
        }
        var now = UtcNow;
        var tracks = new List<Track>(songs.Count);
        lock (_gate)
        {
            foreach (var song in songs)
            {
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
            var unlisted = _library.Tracks.Where(t => t.SourceType == SourceType.NoctisServer && !listed.Contains(t.Id)).ToList();
            if (unlisted.Count > 0) DebugLog.Write("Account", $"catalog: keeping {unlisted.Count} songs the desktop did not list this time");
            tracks.AddRange(unlisted);
        }
        SaveSyncState();
        await _library.ReplaceRemoteTracksAsync(tracks, ct).ConfigureAwait(false);

        // Known songs: the catalog's favorite/rating (unless an edit is pending); counts only grow.
        var updates = new List<(Track Track, bool Favorite, int Rating, int PlayCount, bool Preferences)>();
        lock (_gate)
        {
            foreach (var song in songs)
            {
                if (!known.Contains(song.Id)) continue;
                var lib = _library.GetTrackById(song.Id);
                if (lib is null || lib.SourceType != SourceType.NoctisServer) continue;
                var hex = song.Id.ToString("N");
                var preferences = !_pending.Tracks.ContainsKey(hex);
                if (preferences) SetBaselineLocked(hex, song.Starred, song.UserRating, lib.IsDisliked);
                if ((preferences && (lib.IsFavorite != song.Starred || lib.Rating != song.UserRating)) || song.PlayCount > lib.PlayCount)
                    updates.Add((lib, song.Starred, song.UserRating, song.PlayCount, preferences));
            }
        }
        if (updates.Count > 0)
        {
            SaveSyncState();
            var favoriteChanged = new List<Track>();
            await OnUiAsync(() =>
            {
                foreach (var (t, fav, rating, count, preferences) in updates)
                {
                    if (preferences)
                    {
                        if (t.IsFavorite != fav) { t.IsFavorite = fav; favoriteChanged.Add(t); }
                        t.Rating = rating;
                    }
                    if (count > t.PlayCount) t.PlayCount = count;
                }
            }).ConfigureAwait(false);
            await _library.SaveTrackUserStateAsync(updates.Select(u => u.Track).ToList()).ConfigureAwait(false);
            if (favoriteChanged.Count > 0) await OnUiAsync(() => _library.NotifyFavoritesChanged(favoriteChanged)).ConfigureAwait(false);
        }
        return tracks;
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
        // Covers are not worth a sync: a cover that fails (a timeout, a dropped connection) is
        // skipped and asked again next sync, and the favourites, playlists and plays still go.
        // Many failures mean the desktop is gone; stop asking and let the next stage say so.
        using var stage = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            await Parallel.ForEachAsync(due, new ParallelOptions { MaxDegreeOfParallelism = CoverConcurrency, CancellationToken = stage.Token },
                async (albumId, token) =>
                {
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
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* gave up after CoverFailureLimit */ }
        finally
        {
            if (failed > 0) DebugLog.Write("Account", $"covers: {written} saved, {failed} failed{(stage.IsCancellationRequested && !ct.IsCancellationRequested ? ", stopped asking" : "")}");
            // Rebuild so albums pick the new files up.
            if (written > 0) _library.NotifyMetadataChanged();
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
                if (string.Equals(item.Device, _deviceId, StringComparison.Ordinal)) continue;
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
                var lib = _library.GetTrackById(id);
                if (lib is null || lib.SourceType != SourceType.NoctisServer) continue;
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
        lock (_gate) agreed = new Dictionary<string, PlaylistSyncState>(_sync.Playlists, StringComparer.OrdinalIgnoreCase);
        var now = UtcNow;
        var changed = false;

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
            if (pulled.ContainsKey(id)) continue;
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

        var toPush = new List<(string Hex, PlaylistSyncState State)>();
        foreach (var (hex, state) in agreed)
        {
            var local = phone.FirstOrDefault(p => NoctisRemoteIds.TryParseSyncId(hex, out var id) && p.Id == id);
            if (local is null || !Newer(local.ModifiedAt, state.ModifiedAt)) continue;
            var modified = NotAfter(Utc(local.ModifiedAt), now) ?? now;
            // Within the server's limits, or it drops the whole item: name ≤ 200, description
            // ≤ 2000, ≤ 10000 songs, colour #RRGGBB[AA] or none (none keeps the desktop's).
            toPush.Add((hex, new PlaylistSyncState(
                NoctisServerClient.Cap(local.Name ?? string.Empty, MaxPlaylistName)!,
                NoctisServerClient.Cap(local.Description ?? string.Empty, MaxPlaylistDescription)!,
                IsSyncColor(local.Color) ? local.Color! : string.Empty,
                local.TrackIds.Where(g => g != Guid.Empty).Take(MaxPlaylistTracks).ToList(),
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
            foreach (var (hex, state) in chunk) agreed[hex] = state;
        }

        lock (_gate) _sync.Playlists = agreed;
        return (agreed.Count, toPush.Count);
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
        lock (_gate) plays = _pending.Plays.ToList();
        if (plays.Count == 0) return 0;
        RaiseSyncProgress(NoctisSyncStage.Plays, 0, plays.Count);
        var nowMs = _time.GetUtcNow().ToUnixTimeMilliseconds();
        var sent = 0;
        foreach (var batch in plays.Chunk(ScrobbleBatch))
        {
            var valid = batch
                .Select(p => (Ok: NoctisRemoteIds.TryParseSyncId(p.Id, out var id), Id: id, Time: Math.Clamp(p.Time, 0, nowMs)))
                .Where(p => p.Ok).Select(p => (p.Id, p.Time)).ToList();
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

    public void RecordPlay(Track track, DateTime playedUtc)
    {
        if (!IsRemote(track)) return;
        var utc = playedUtc.Kind == DateTimeKind.Local ? playedUtc.ToUniversalTime() : DateTime.SpecifyKind(playedUtc, DateTimeKind.Utc);
        var ms = Math.Clamp(new DateTimeOffset(utc).ToUnixTimeMilliseconds(), 0, _time.GetUtcNow().ToUnixTimeMilliseconds());
        lock (_gate)
        {
            _pending.Plays.Add(new PendingPlay(track.Id.ToString("N"), ms));
            if (_pending.Plays.Count > MaxQueuedPlays) _pending.Plays.RemoveRange(0, _pending.Plays.Count - MaxQueuedPlays);
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
            if (t is null || !IsRemote(t)) continue;
            var hex = t.Id.ToString("N");
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

    public Task DownloadAsync(IEnumerable<Track> tracks, CancellationToken ct = default)
    {
        var ids = tracks.Where(IsRemote).Select(t => t.Id).Distinct().Where(id => !_downloaded.ContainsKey(id)).ToList();
        return RunDownloadsAsync(ids, ct);
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
        var queue = new ConcurrentQueue<Guid>(ids);
        Interlocked.Add(ref _dlPending, ids.Count);
        RaiseDownloadProgress();
        NoctisServerException? fatal = null;

        async Task Worker()
        {
            while (queue.TryDequeue(out var id))
            {
                try
                {
                    if (token.IsCancellationRequested || _downloaded.ContainsKey(id) || !_inFlight.TryAdd(id, 0)) continue;
                    try
                    {
                        await _downloadSlots.WaitAsync(token).ConfigureAwait(false);
                        try
                        {
                            await DownloadOneAsync(client, id, token).ConfigureAwait(false);
                            Interlocked.Increment(ref _dlCompleted);
                        }
                        finally { _downloadSlots.Release(); }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested) { }
                    catch (NoctisServerException ex) when (ex.Kind is NoctisErrorKind.SignedOut or NoctisErrorKind.CertificateChanged or NoctisErrorKind.StorageFull)
                    {
                        // Every other song would fail the same way.
                        Interlocked.CompareExchange(ref fatal, ex, null);
                        Interlocked.Increment(ref _dlFailed);
                        try { linked.Cancel(); } catch (ObjectDisposedException) { }
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _dlFailed);
                        DebugLog.Write("Account", $"download failed: {(ex is NoctisServerException n ? n.Kind.ToString() : ex.GetType().Name)}");
                    }
                    finally { _inFlight.TryRemove(id, out _); }
                }
                finally
                {
                    Interlocked.Decrement(ref _dlPending);
                    RaiseDownloadProgress();
                }
            }
        }

        var doneBefore = Volatile.Read(ref _dlCompleted);
        var failedBefore = Volatile.Read(ref _dlFailed);
        await Task.WhenAll(Enumerable.Range(0, DownloadConcurrency).Select(_ => Worker())).ConfigureAwait(false);
        // One line per batch, so a download that silently did nothing is visible in the log.
        DebugLog.Write("Account", $"downloads: {ids.Count} asked, {Volatile.Read(ref _dlCompleted) - doneBefore} done, " +
            $"{Volatile.Read(ref _dlFailed) - failedBefore} failed{(token.IsCancellationRequested ? ", cancelled" : "")}");
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
            var contentType = await client.DownloadTrackAsync(id, part, ct, flac).ConfigureAwait(false);
            var final = InsideOffline(hex + "." + ExtensionFor(id, contentType, flac));
            if (_downloaded.TryGetValue(id, out var old) && old.Path != final) NoctisServerClient.TryDelete(old.Path);
            File.Move(part, final, overwrite: true);
            _downloaded[id] = new DownloadedFile(final, new FileInfo(final).Length);
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
                if (_downloaded.TryRemove(id, out var f)) NoctisServerClient.TryDelete(f.Path);
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
            Volatile.Read(ref _dlCompleted), Volatile.Read(ref _dlFailed), DownloadedBytes));

    private sealed class InlineProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
