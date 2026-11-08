using System.Text.Json;

namespace Noctis.Services;

/// <summary>The scrobbling services a play can be owed to.</summary>
public enum ScrobbleService { LastFm, ListenBrainz }

/// <summary>One counted play waiting in <see cref="ScrobbleQueue"/>: a flag per service it is still
/// owed to, cleared when that service has taken it (or never will), the play dropped once both are clear.</summary>
public sealed class PendingScrobble
{
    public string Artist { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Album { get; set; }
    public int DurationSeconds { get; set; }
    /// <summary>When the play started, Unix seconds.</summary>
    public long Timestamp { get; set; }
    public bool LastFm { get; set; }
    public bool ListenBrainz { get; set; }

    public ScrobbleEntry ToEntry() => new(Artist, Title, Album, DurationSeconds, Timestamp);

    internal bool Owes(ScrobbleService s) => s == ScrobbleService.LastFm ? LastFm : ListenBrainz;

    internal void Clear(ScrobbleService s)
    {
        if (s == ScrobbleService.LastFm) LastFm = false;
        else ListenBrainz = false;
    }
}

/// <summary>
/// GitHub #197: the computer's scrobbles wait here, in a file in the data folder, until Last.fm or
/// ListenBrainz has taken them. A play is queued for every service it is owed to, then sent; it stays
/// while the service is signed out (session expired, or a login this computer cannot read) or can't
/// be reached (offline, 5xx, 429), and goes out on the next send that works: after the next play,
/// on signing in, at start and on a backoff timer. Sent in batches of 50, oldest first, a play leaving
/// the queue only once that service has answered for it. The design of the phone's PhoneScrobbler.
/// </summary>
public sealed class ScrobbleQueue
{
    /// <summary>Plays kept at most; the oldest go first past it.</summary>
    public const int MaxQueued = 2000;

    /// <summary>Plays per call: Last.fm's track.scrobble limit (ListenBrainz takes more).</summary>
    public const int MaxBatch = 50;

    /// <summary>Last.fm ignores scrobbles older than this, so they are dropped instead of sent.</summary>
    public static readonly TimeSpan LastFmMaxAge = TimeSpan.FromDays(14);

    private static readonly TimeSpan MinBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(30);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly string _path;
    private readonly Func<ScrobbleService, bool> _canSend;
    private readonly Func<ScrobbleService, IReadOnlyList<ScrobbleEntry>, CancellationToken, Task<ScrobbleSendResult>> _send;
    private readonly TimeProvider _time;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _flushing = new(1, 1);
    private readonly Dictionary<ScrobbleService, (int Failures, DateTime NextUtc)> _backoff = new();
    private List<PendingScrobble>? _queue;
    private Timer? _timer;

    /// <param name="path">The queue file (pending_scrobbles.json in the data folder).</param>
    /// <param name="canSend">Whether a service can be sent to now (signed in and turned on).</param>
    /// <param name="send">Sends up to <see cref="MaxBatch"/> plays to a service in one call.</param>
    public ScrobbleQueue(string path,
        Func<ScrobbleService, bool> canSend,
        Func<ScrobbleService, IReadOnlyList<ScrobbleEntry>, CancellationToken, Task<ScrobbleSendResult>> send,
        TimeProvider? time = null)
    {
        _path = path;
        _canSend = canSend;
        _send = send;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Tests turn the retry timer off and call <see cref="FlushAsync"/> themselves.</summary>
    public bool AutoRetry { get; init; } = true;

    /// <summary>Raised (any thread) when plays were queued or left the queue.</summary>
    public event EventHandler? Changed;

    private DateTime UtcNow => _time.GetUtcNow().UtcDateTime;

    /// <summary>Plays still owed to <paramref name="service"/>.</summary>
    public int PendingCount(ScrobbleService service)
    {
        lock (_gate) return Queue.Count(p => p.Owes(service));
    }

    /// <summary>Queues a counted play for the services named. The same play (artist, title and start)
    /// queued twice is kept once. Call <see cref="FlushAsync"/> after to send it.</summary>
    public void Add(ScrobbleEntry play, bool lastFm, bool listenBrainz)
    {
        if (!lastFm && !listenBrainz) return;
        if (string.IsNullOrWhiteSpace(play.Artist) || string.IsNullOrWhiteSpace(play.Title)) return;
        lock (_gate)
        {
            var queue = Queue;
            var same = queue.FirstOrDefault(p => p.Timestamp == play.Timestamp &&
                                                 string.Equals(p.Artist, play.Artist, StringComparison.Ordinal) &&
                                                 string.Equals(p.Title, play.Title, StringComparison.Ordinal));
            if (same != null)
            {
                same.LastFm |= lastFm;
                same.ListenBrainz |= listenBrainz;
            }
            else
            {
                queue.Add(new PendingScrobble
                {
                    Artist = play.Artist, Title = play.Title, Album = play.Album,
                    DurationSeconds = play.DurationSeconds, Timestamp = play.Timestamp,
                    LastFm = lastFm, ListenBrainz = listenBrainz,
                });
                if (queue.Count > MaxQueued)
                {
                    var over = queue.Count - MaxQueued;
                    queue.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
                    queue.RemoveRange(0, over);
                    DebugLog.Write("Scrobble", $"Queue full: dropped the {over} oldest play(s).");
                }
            }
            Save();
        }
        RaiseChanged();
    }

    /// <summary>The user signed out of <paramref name="service"/> on purpose: what was queued for it is
    /// no longer owed.</summary>
    public void Forget(ScrobbleService service)
    {
        bool changed;
        lock (_gate)
        {
            var queue = Queue;
            changed = queue.Any(p => p.Owes(service));
            if (!changed) return;
            foreach (var p in queue) p.Clear(service);
            queue.RemoveAll(p => !p.LastFm && !p.ListenBrainz);
            Save();
            _backoff.Remove(service);
        }
        RaiseChanged();
    }

    /// <summary>Sends what is owed to every service that can be sent to. A service backing off after a
    /// failed send is skipped until its time unless <paramref name="now"/> (a new play, a sign-in).</summary>
    public async Task FlushAsync(bool now = false, CancellationToken ct = default)
    {
        await _flushing.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await FlushServiceAsync(ScrobbleService.LastFm, now, ct).ConfigureAwait(false);
            await FlushServiceAsync(ScrobbleService.ListenBrainz, now, ct).ConfigureAwait(false);
        }
        finally
        {
            _flushing.Release();
        }
        if (AutoRetry) ScheduleRetry();
    }

    /// <summary>Sends in a little while (at start, so it doesn't compete with loading).</summary>
    public void FlushSoon(TimeSpan delay)
    {
        if (!AutoRetry) return;
        lock (_gate)
        {
            if (!Queue.Any(p => p.LastFm || p.ListenBrainz)) return;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = FlushAsync(), null, delay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task FlushServiceAsync(ScrobbleService service, bool now, CancellationToken ct)
    {
        // A rejection answers for the whole call (ListenBrainz's 400, Last.fm's error 6), so one bad
        // play would take the good ones batched with it: a rejected batch is halved until the bad
        // play goes alone, then full batches resume.
        var take = MaxBatch;
        while (!ct.IsCancellationRequested)
        {
            lock (_gate)
                if (!now && _backoff.TryGetValue(service, out var b) && b.NextUtc > UtcNow) return;
            if (!_canSend(service)) return;

            List<PendingScrobble> batch;
            lock (_gate)
            {
                if (service == ScrobbleService.LastFm) DropTooOldForLastFm();
                batch = Queue.Where(p => p.Owes(service)).OrderBy(p => p.Timestamp).Take(take).ToList();
            }
            if (batch.Count == 0) { lock (_gate) _backoff.Remove(service); return; }

            ScrobbleSendResult result;
            try
            {
                result = await _send(service, batch.Select(p => p.ToEntry()).ToList(), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                DebugLog.Write("Scrobble", $"{service}: send failed: {ex.GetType().Name}");
                result = ScrobbleSendResult.Retry;
            }

            switch (result)
            {
                case ScrobbleSendResult.Rejected when batch.Count > 1:
                    take = Math.Max(1, batch.Count / 2);
                    break;
                case ScrobbleSendResult.Ok:
                case ScrobbleSendResult.Rejected:
                    if (result == ScrobbleSendResult.Rejected)
                    {
                        take = MaxBatch;
                        DebugLog.Write("Scrobble", $"{service} will not take \"{batch[0].Artist} - {batch[0].Title}\"; dropped.");
                    }
                    lock (_gate)
                    {
                        foreach (var p in batch) p.Clear(service);
                        Queue.RemoveAll(p => !p.LastFm && !p.ListenBrainz);
                        Save();
                    }
                    lock (_gate) _backoff.Remove(service);
                    RaiseChanged();
                    break;
                default:
                    // Retry (offline, 5xx, 429) or SessionInvalid (the login needs renewing): the plays
                    // stay for a later send, after a backoff so a dead server isn't hammered.
                    TimeSpan delay;
                    lock (_gate)
                    {
                        var failures = _backoff.TryGetValue(service, out var prev) ? prev.Failures + 1 : 1;
                        delay = TimeSpan.FromTicks(Math.Min(MaxBackoff.Ticks, MinBackoff.Ticks << Math.Min(failures - 1, 10)));
                        _backoff[service] = (failures, UtcNow + delay);
                    }
                    DebugLog.Write("Scrobble", $"{service}: {result}; {PendingCount(service)} play(s) kept, next try in {delay.TotalSeconds:0} s.");
                    return;
            }
        }
    }

    // Caller holds _gate.
    private void DropTooOldForLastFm()
    {
        var cutoff = new DateTimeOffset(UtcNow - LastFmMaxAge).ToUnixTimeSeconds();
        var old = Queue.Where(p => p.LastFm && p.Timestamp < cutoff).ToList();
        if (old.Count == 0) return;
        foreach (var p in old) p.LastFm = false;
        Queue.RemoveAll(p => !p.LastFm && !p.ListenBrainz);
        Save();
        DebugLog.Write("Scrobble", $"LastFm: dropped {old.Count} play(s) older than {LastFmMaxAge.TotalDays:0} days (Last.fm no longer takes them).");
    }

    private void ScheduleRetry()
    {
        DateTime? next = null;
        lock (_gate)
        {
            foreach (var (service, b) in _backoff)
                if (Queue.Any(p => p.Owes(service)) && (next == null || b.NextUtc < next))
                    next = b.NextUtc;
            if (next == null) return;
            var wait = next.Value - UtcNow;
            if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
            _timer?.Dispose();
            _timer = new Timer(_ => _ = FlushAsync(), null, wait, Timeout.InfiniteTimeSpan);
        }
    }

    private void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    // Loaded on first use, so a queue nobody touches never reads the disk. Caller holds _gate.
    private List<PendingScrobble> Queue => _queue ??= Load();

    private List<PendingScrobble> Load()
    {
        try
        {
            if (File.Exists(_path))
                return JsonSerializer.Deserialize<List<PendingScrobble>>(File.ReadAllText(_path), Json)?
                    .Where(p => p != null && (p.LastFm || p.ListenBrainz)).ToList() ?? new();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            DebugLog.Write("Scrobble", $"Queue unreadable, starting empty: {ex.Message}");
        }
        return new();
    }

    // Caller holds _gate. Temp file then move, so a crash mid-write leaves the old queue.
    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (_queue is not { Count: > 0 })
            {
                if (File.Exists(_path)) File.Delete(_path);
                return;
            }
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_queue, Json));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DebugLog.Write("Scrobble", $"Queue save failed: {ex.Message}");
        }
    }
}
