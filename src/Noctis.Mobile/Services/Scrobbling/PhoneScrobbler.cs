using System.Text.Json;
using Noctis.Helpers;
using Noctis.Mobile.Services.Account;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.Services.Scrobbling;

/// <summary>One counted play waiting for the services it still owes: a flag per service, cleared when
/// that service has taken it (or will never take it), the play dropped once both are clear.</summary>
internal sealed class QueuedPlay
{
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    public long Timestamp { get; set; }
    public bool LastFm { get; set; }
    public bool ListenBrainz { get; set; }

    public ScrobbleEntry ToEntry() => new(Artist, Title, Album, DurationSeconds, Timestamp);
}

/// <summary>
/// "From This Phone" scrobbling: decides when a play counts (the same rule as the desktop,
/// <see cref="ScrobblePlayClock"/>), queues it in <c>scrobble_queue.json</c> (survives restarts and being
/// offline) and sends the queue to Last.fm / ListenBrainz in batches of 50, retrying with backoff.
///
/// Exactly once: the route is chosen when a play counts. In Through Computer mode this class sends
/// nothing: the account sync carries the play, marked (<see cref="PlayDecided"/>) for the computer to
/// send on; in From This Phone mode the play is queued here and nowhere else, and marked so the
/// computer only counts it. A mode switch changes only the plays that come after it: what is queued stays queued
/// and is sent once by whichever route took it, never again, never dropped. Per service, a play is
/// removed from the queue only after that service answered for it.
/// </summary>
public sealed class PhoneScrobbler
{
    private const int MaxQueued = 2000;
    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly IPersistenceService _persistence;
    private readonly LastFmScrobbleClient _lastFm;
    private readonly ListenBrainzScrobbleClient _listenBrainz;
    private readonly TimeProvider _time;
    private readonly string _queuePath;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly ScrobblePlayClock _clock = new();
    private readonly Dictionary<string, (int Failures, DateTime NextUtc)> _backoff = new();
    private List<QueuedPlay> _queue = new();
    private Settings _cfg = new();
    private Track? _track;
    private bool _counted;
    private bool _loaded;
    private string? _lastFmToken;
    private Timer? _timer;

    private sealed record Settings
    {
        public bool FromPhone { get; init; }
        public bool SendPlays { get; init; } = true;
        public bool NowPlaying { get; init; } = true;
        public string LastFmKey { get; init; } = string.Empty;
        public string LastFmUser { get; init; } = string.Empty;
        public string LbToken { get; init; } = string.Empty;
        public string LbUser { get; init; } = string.Empty;
        public IReadOnlySet<string> Excluded { get; init; } = new HashSet<string>();
        public bool Taste { get; init; } = true;
    }

    public PhoneScrobbler(IPersistenceService persistence, HttpClient http, TimeProvider? time = null)
    {
        _persistence = persistence;
        _lastFm = new LastFmScrobbleClient(http);
        _listenBrainz = new ListenBrainzScrobbleClient(http);
        _time = time ?? TimeProvider.System;
        _queuePath = Path.Combine(persistence.DataDirectory, "scrobble_queue.json");
        _queue = LoadQueue();
    }

    /// <summary>Tests turn the timer off and call <see cref="FlushAsync"/> themselves.</summary>
    public bool AutoFlush { get; init; } = true;

    /// <summary>False where the phone has no account service (none built, or parked): there is no
    /// computer to send plays through, so the phone sends them itself whatever the saved route says.</summary>
    public bool HasComputerRoute { get; init; } = true;

    /// <summary>Raised (any thread) when the settings, a connection or the queue changed.</summary>
    public event EventHandler? Changed;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    // ── Settings (only this class's own fields are written) ──

    /// <summary>Loads the saved settings unless they already are (the shell and a test may both ask).</summary>
    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : LoadAsync();

    /// <summary>Reads the saved settings; call once at start and after anything else may have changed them.</summary>
    public async Task LoadAsync()
    {
        var s = await _persistence.LoadSettingsAsync().ConfigureAwait(false);
        _cfg = FromSettings(s);
        _loaded = true;
        RaiseChanged();
        if (AutoFlush && PendingCount > 0) ScheduleFlush(TimeSpan.FromSeconds(5));
    }

    private static Settings FromSettings(AppSettings s) => new()
    {
        FromPhone = s.MobileScrobbleFromPhone,
        SendPlays = s.MobileScrobbleSendPlays,
        NowPlaying = s.MobileScrobbleNowPlaying,
        LastFmKey = s.LastFmScrobblingEnabled ? s.LastFmSessionKey ?? string.Empty : string.Empty,
        LastFmUser = s.LastFmUsername ?? string.Empty,
        LbToken = s.ListenBrainzScrobblingEnabled ? s.ListenBrainzToken ?? string.Empty : string.Empty,
        LbUser = s.ListenBrainzUsername ?? string.Empty,
        Excluded = new HashSet<string>(s.ScrobbleExcludedKeys ?? new List<string>()),
        Taste = s.LastFmTasteEnabled,
    };

    public bool FromPhone => _cfg.FromPhone || !HasComputerRoute;
    public bool SendPlays => _cfg.SendPlays;
    public bool ShowNowPlaying => _cfg.NowPlaying;
    public bool LastFmConnected => _cfg.LastFmKey.Length > 0;
    public bool ListenBrainzConnected => _cfg.LbToken.Length > 0;
    public string LastFmUser => _cfg.LastFmUser;
    public string ListenBrainzUser => _cfg.LbUser;

    /// <summary>Settings › Scrobbling › Mixes and Recommendations (AppSettings.LastFmTasteEnabled):
    /// Home's From Your Last.fm and Discover on Last.fm, read from the connected account's charts.</summary>
    public bool LastFmTasteEnabled => _cfg.Taste;

    public Task SetFromPhoneAsync(bool value) => Apply(c => c with { FromPhone = value }, s => s.MobileScrobbleFromPhone = value);
    public Task SetSendPlaysAsync(bool value) => Apply(c => c with { SendPlays = value }, s => s.MobileScrobbleSendPlays = value);
    public Task SetShowNowPlayingAsync(bool value) => Apply(c => c with { NowPlaying = value }, s => s.MobileScrobbleNowPlaying = value);
    public Task SetLastFmTasteEnabledAsync(bool value) => Apply(c => c with { Taste = value }, s => s.LastFmTasteEnabled = value);

    private async Task Apply(Func<Settings, Settings> cfg, Action<AppSettings> save)
    {
        _cfg = cfg(_cfg);
        RaiseChanged();
        await _persistence.UpdateSettingsAsync(save).ConfigureAwait(false);
        if (AutoFlush && PendingCount > 0) ScheduleFlush(TimeSpan.Zero);
    }

    // ── Connecting ──

    /// <summary>Starts Last.fm's web sign-in: the page to open in the browser, or null when Last.fm could not be reached.</summary>
    public async Task<string?> BeginLastFmAuthAsync(CancellationToken ct = default)
    {
        _lastFmToken = await _lastFm.GetTokenAsync(ct).ConfigureAwait(false);
        return _lastFmToken is null ? null : LastFmScrobbleClient.AuthUrl(_lastFmToken);
    }

    /// <summary>Finishes it once the user approved Noctis in the browser; false while Last.fm has no approval yet.</summary>
    public async Task<bool> CompleteLastFmAuthAsync(CancellationToken ct = default)
    {
        if (_lastFmToken is null) return false;
        if (await _lastFm.GetSessionAsync(_lastFmToken, ct).ConfigureAwait(false) is not { } session) return false;
        _lastFmToken = null;
        var (key, user) = session;
        _cfg = _cfg with { LastFmKey = key, LastFmUser = user };
        // Plays queued before this sign-in were never owed to Last.fm: only later plays are.
        RaiseChanged();
        await _persistence.UpdateSettingsAsync(s =>
        {
            s.LastFmSessionKey = key;
            s.LastFmUsername = user;
            s.LastFmScrobblingEnabled = true;
        }).ConfigureAwait(false);
        return true;
    }

    /// <summary>Checks a pasted ListenBrainz token and keeps it. Null on success, else "invalid" or "unreachable".</summary>
    public async Task<string?> ConnectListenBrainzAsync(string token, CancellationToken ct = default)
    {
        token = token.Trim();
        var (user, reason) = await _listenBrainz.ValidateAsync(token, ct).ConfigureAwait(false);
        if (user is null) return reason ?? "invalid";
        _cfg = _cfg with { LbToken = token, LbUser = user };
        RaiseChanged();
        await _persistence.UpdateSettingsAsync(s =>
        {
            s.ListenBrainzToken = token;
            s.ListenBrainzUsername = user;
            s.ListenBrainzScrobblingEnabled = true;
        }).ConfigureAwait(false);
        return null;
    }

    /// <summary>Signs out on purpose: that service is no longer owed what was queued for it.</summary>
    public async Task DisconnectAsync(bool lastFm)
    {
        lock (_gate)
        {
            foreach (var p in _queue)
            {
                if (lastFm) p.LastFm = false;
                else p.ListenBrainz = false;
            }
            _queue.RemoveAll(p => !p.LastFm && !p.ListenBrainz);
            SaveQueue();
        }
        _cfg = lastFm ? _cfg with { LastFmKey = string.Empty, LastFmUser = string.Empty } : _cfg with { LbToken = string.Empty, LbUser = string.Empty };
        RaiseChanged();
        await _persistence.UpdateSettingsAsync(s =>
        {
            if (lastFm)
            {
                s.LastFmSessionKey = string.Empty;
                s.LastFmUsername = string.Empty;
                s.LastFmScrobblingEnabled = false;
            }
            else
            {
                s.ListenBrainzToken = string.Empty;
                s.ListenBrainzUsername = string.Empty;
                s.ListenBrainzScrobblingEnabled = false;
            }
        }).ConfigureAwait(false);
    }

    public Task<long?> LastFmPlayCountAsync(CancellationToken ct = default)
        => LastFmConnected ? _lastFm.GetPlayCountAsync(_cfg.LastFmUser, ct) : Task.FromResult<long?>(null);

    // ── Playback ──

    /// <summary>
    /// Raised once per play with its scrobble verdict, for the account sync (which recorded the play
    /// when it started): <see cref="PlayScrobble.Computer"/> when it counted in Through Computer mode,
    /// so the computer sends it on; <see cref="PlayScrobble.Phone"/> when it counted in From This
    /// Phone mode (this class owns it); <see cref="PlayScrobble.No"/> when the next song started
    /// before it counted.
    /// </summary>
    public event Action<Track, PlayScrobble>? PlayDecided;

    /// <summary>A track started playing.</summary>
    public void TrackStarted(Track track)
    {
        if (_track is { } previous && !_counted) PlayDecided?.Invoke(previous, PlayScrobble.No);
        _track = track;
        _counted = false;
        _clock.Start(UtcNow);
        if (!FromPhone || !_cfg.SendPlays || !_cfg.NowPlaying || IsExcluded(track)) return;
        if (!Sendable(track)) return;
        var entry = ToEntry(track, 0);
        var key = _cfg.LastFmKey;
        var token = _cfg.LbToken;
        if (key.Length > 0) _ = _lastFm.UpdateNowPlayingAsync(key, entry);
        if (token.Length > 0) _ = _listenBrainz.UpdateNowPlayingAsync(token, entry);
    }

    /// <summary>The player's position tick: whether audio is moving. Counts the play once, on the tick it qualifies.</summary>
    public void Tick(bool playing)
    {
        if (_track is not { } track) return;
        var now = UtcNow;
        if (playing) _clock.Resume(now);
        else _clock.Pause(now);
        if (_counted || !ScrobblePlayClock.ShouldScrobble(track.Duration, _clock.Played(now))) return;
        _counted = true;
        // Through Computer: the account sync carries the play and the computer sends it on; this
        // class sends nothing. From This Phone: this class sends it (or Send Plays is off), never the computer.
        PlayDecided?.Invoke(track, FromPhone ? PlayScrobble.Phone : PlayScrobble.Computer);
        if (!FromPhone || !_cfg.SendPlays) return;
        Enqueue(track, _clock.StartedAt);
    }

    /// <summary>Queues a counted play for every connected service that is not excluded.</summary>
    internal void Enqueue(Track track, DateTime startedUtc)
    {
        if (!Sendable(track) || IsExcluded(track)) return;
        bool lastFm = _cfg.LastFmKey.Length > 0, lb = _cfg.LbToken.Length > 0;
        if (!lastFm && !lb) return;
        var ts = new DateTimeOffset(DateTime.SpecifyKind(startedUtc, DateTimeKind.Utc)).ToUnixTimeSeconds();
        lock (_gate)
        {
            _queue.Add(new QueuedPlay
            {
                Artist = track.Artist, Title = track.Title, Album = track.Album,
                DurationSeconds = (int)track.Duration.TotalSeconds, Timestamp = ts,
                LastFm = lastFm, ListenBrainz = lb,
            });
            if (_queue.Count > MaxQueued) _queue.RemoveRange(0, _queue.Count - MaxQueued);
            SaveQueue();
        }
        RaiseChanged();
        if (AutoFlush) ScheduleFlush(TimeSpan.Zero);
    }

    private static bool Sendable(Track t) => !string.IsNullOrWhiteSpace(t.Artist) && !string.IsNullOrWhiteSpace(t.Title);
    private bool IsExcluded(Track t) => ScrobbleExclusionKeys.IsExcluded(_cfg.Excluded, t);
    private static ScrobbleEntry ToEntry(Track t, long ts) => new(t.Artist, t.Title, t.Album, (int)t.Duration.TotalSeconds, ts);

    // ── The queue ──

    /// <summary>Plays still owed to a connected service.</summary>
    public int PendingCount
    {
        get
        {
            lock (_gate) return _queue.Count(p => p.LastFm && LastFmConnected || p.ListenBrainz && ListenBrainzConnected);
        }
    }

    /// <summary>Sends what is owed. One run at a time; a service in backoff is skipped until its time.
    /// Per service, in order and in batches of 50, a play leaves the queue only once that service has
    /// answered for it (accepted, or rejected for good); a failed batch stays whole for the retry.</summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (!_cfg.SendPlays) return;
        await _flushing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushServiceAsync("lastfm", ct).ConfigureAwait(false);
            await FlushServiceAsync("listenbrainz", ct).ConfigureAwait(false);
        }
        finally
        {
            _flushing.Release();
        }
        RaiseChanged();
        if (AutoFlush && NextRetry() is { } wait) ScheduleFlush(wait);
    }

    private async Task FlushServiceAsync(string service, CancellationToken ct)
    {
        var lastFm = service == "lastfm";
        // A rejection answers for the whole call (ListenBrainz's 400, Last.fm's error 6), so one bad
        // play would take the good ones batched with it: a rejected batch is halved until the
        // bad play goes alone, then full batches resume.
        var take = LastFmScrobbleClient.MaxBatch;
        while (!ct.IsCancellationRequested)
        {
            if (_backoff.TryGetValue(service, out var b) && b.NextUtc > UtcNow) return;
            string credential = lastFm ? _cfg.LastFmKey : _cfg.LbToken;
            if (credential.Length == 0) return;
            List<QueuedPlay> batch;
            lock (_gate)
                batch = _queue.Where(p => lastFm ? p.LastFm : p.ListenBrainz).OrderBy(p => p.Timestamp)
                    .Take(take).ToList();
            if (batch.Count == 0) { _backoff.Remove(service); return; }

            var entries = batch.Select(p => p.ToEntry()).ToList();
            var result = lastFm
                ? await _lastFm.ScrobbleAsync(credential, entries, ct).ConfigureAwait(false)
                : await _listenBrainz.SubmitAsync(credential, entries, ct).ConfigureAwait(false);

            switch (result)
            {
                case ScrobbleSendResult.Rejected when batch.Count > 1:
                    take = Math.Max(1, batch.Count / 2);
                    break;
                case ScrobbleSendResult.Ok:
                case ScrobbleSendResult.Rejected:
                    if (result == ScrobbleSendResult.Rejected) take = LastFmScrobbleClient.MaxBatch;
                    lock (_gate)
                    {
                        foreach (var p in batch)
                        {
                            if (lastFm) p.LastFm = false;
                            else p.ListenBrainz = false;
                        }
                        _queue.RemoveAll(p => !p.LastFm && !p.ListenBrainz);
                        SaveQueue();
                    }
                    _backoff.Remove(service);
                    RaiseChanged();
                    break;
                case ScrobbleSendResult.SessionInvalid:
                    // The login is dead. The plays stay queued for a new sign-in to the same service.
                    _cfg = lastFm ? _cfg with { LastFmKey = string.Empty } : _cfg with { LbToken = string.Empty };
                    RaiseChanged();
                    await _persistence.UpdateSettingsAsync(s =>
                    {
                        if (lastFm) s.LastFmScrobblingEnabled = false;
                        else s.ListenBrainzScrobblingEnabled = false;
                    }).ConfigureAwait(false);
                    return;
                default:
                    var failures = _backoff.TryGetValue(service, out var prev) ? prev.Failures + 1 : 1;
                    var delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, MinBackoff.Ticks << Math.Min(failures - 1, 10)));
                    _backoff[service] = (failures, UtcNow + delay);
                    return;
            }
        }
    }

    private TimeSpan? NextRetry()
    {
        if (!_cfg.SendPlays || PendingCount == 0) return null;
        var next = _backoff.Values.Select(b => b.NextUtc).DefaultIfEmpty(DateTime.MinValue).Min();
        return next > UtcNow ? next - UtcNow : null;
    }

    private void ScheduleFlush(TimeSpan delay)
    {
        _timer?.Dispose();
        _timer = new Timer(_ => _ = FlushAsync(), null, delay, Timeout.InfiniteTimeSpan);
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    private List<QueuedPlay> LoadQueue()
    {
        try
        {
            if (File.Exists(_queuePath))
                return JsonSerializer.Deserialize<List<QueuedPlay>>(File.ReadAllText(_queuePath), Json) ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write("Scrobble", $"Queue unreadable, starting empty: {ex.Message}");
        }
        return new();
    }

    // Caller holds _gate. Temp file then move, so a crash mid-write leaves the old queue.
    private void SaveQueue()
    {
        try
        {
            var tmp = _queuePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_queue, Json));
            File.Move(tmp, _queuePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("Scrobble", $"Queue save failed: {ex.Message}");
        }
    }
}
