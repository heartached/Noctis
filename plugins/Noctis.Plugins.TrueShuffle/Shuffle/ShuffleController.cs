using System.Diagnostics;
using Avalonia.Threading;

namespace Noctis.Plugins.TrueShuffle.Shuffle;

/// <summary>What is shuffled.</summary>
public enum SourceKind { Queue, Playlist, Library }

/// <param name="PlaylistId">For <see cref="SourceKind.Playlist"/>: <see cref="PlaylistInfo.Id"/>.</param>
/// <param name="Name">As the window shows it.</param>
public sealed record ShuffleSource(SourceKind Kind, string? PlaylistId, string Name)
{
    public static ShuffleSource Queue { get; } = new(SourceKind.Queue, null, "Current queue");
    public static ShuffleSource Library { get; } = new(SourceKind.Library, null, "Whole library");
}

/// <summary>
/// A True Shuffle session (ported from True Shuffle's plugin and rolling queue onto plugin API 1.5).
///
/// Applying a mode makes a plan off the UI thread (a 50,000-song library is copied and ordered on a
/// worker), then puts only a window of it in Noctis's queue (<see cref="WindowSize"/> songs) behind the
/// songs the user queued themselves; the playing song plays on. When the shuffled songs up next are
/// down to <see cref="RefillAt"/>, the next part of the plan is added, and when the plan is used up the
/// next one is made the same way. Songs the user adds with Play Next or Add to Queue play first, in the
/// order they were added (Play Next ahead of the others), and stay first whatever the shuffle does.
/// A song counts as heard when it starts. Replacing the queue in Noctis (playing an album, clearing
/// it) ends the session; the queue is left as it is.
///
/// Everything runs on the UI thread except plan making. Nothing is fetched from the network.
/// </summary>
public sealed class ShuffleController : IDisposable
{
    public const int WindowSize = 200, RefillAt = 20, ForYouWindow = 25, ForYouRefillAt = 6;

    private readonly IPluginHost _host;
    private readonly CycleStore _cycle;
    private readonly ListenStore _listens;
    private readonly PlayTracker _tracker = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer? _sampler;
    private readonly List<string> _mine = new();          // the user's own songs up next, in play order
    private readonly List<string> _recent = new();        // this sitting's songs, newest first (For you)
    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);
    private HashSet<string>? _seen;                       // the remembered cycle (loaded on first use)
    private string? _last;
    private List<PlayRecord>? _plays;
    private Session? _session;
    private int _generation;
    private bool _saving, _saveAgain, _disposed;
    private string? _firstUpNext;                         // the song that was up next when the last one started

    /// <summary>Tests: the random draw and the clock of a plan.</summary>
    public Func<int, int>? Draw { get; set; }
    public Func<DateTimeOffset>? Now { get; set; }

    public ShuffleController(IPluginHost host, bool sample = true)
    {
        _host = host;
        _cycle = new CycleStore(host.DataDirectory);
        _listens = new ListenStore(host.DataDirectory);
        host.NowPlaying.TrackChanged += OnTrackChanged;
        host.NowPlaying.IsPlayingChanged += OnIsPlayingChanged;
        Playback.TracksQueued += OnTracksQueued;
        if (sample)
        {
            _sampler = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Sample());
            _sampler.Start();
        }
        StartTracking();
    }

    private IPlaybackControl Playback => _host.Playback;
    private ILibraryReader Library => _host.Library;

    /// <summary>The session in progress, or null.</summary>
    public Session? Current => _session;

    /// <summary>A line for the window: what the shuffle is doing.</summary>
    public string Status { get; private set; } = "Choose a mode and press Shuffle. Nothing changes until you do.";

    /// <summary>Raised on the UI thread when <see cref="Current"/> or <see cref="Status"/> changed.</summary>
    public event EventHandler? Changed;

    /// <summary>The user's own songs up next, in the order they will play (tests).</summary>
    public IReadOnlyList<string> Mine => _mine;

    /// <summary>The remembered cycle (tests).</summary>
    public IReadOnlyCollection<string> Seen => _seen ?? (IReadOnlyCollection<string>)Array.Empty<string>();

    /// <summary>The running save of the remembered cycle (tests wait on it).</summary>
    public Task Saved { get; private set; } = Task.CompletedTask;

    // ── Starting and ending ──

    /// <summary>
    /// Applies <paramref name="mode"/> to <paramref name="source"/>: makes the plan on a worker thread
    /// and puts its first part up next. If another mode is chosen while this one is being prepared,
    /// the latest choice wins. Call on the UI thread.
    /// </summary>
    public async Task<bool> StartAsync(ShuffleMode mode, ShuffleSource source, bool skipDisliked, string? exploration = null)
    {
        var generation = ++_generation;
        SetStatus("Preparing the shuffle…");
        try
        {
            await EnsureCycleAsync();
            var queue = Playback.GetQueue();
            IReadOnlyList<PlaylistInfo>? playlists = null;
            if (source.Kind == SourceKind.Playlist || mode == ShuffleMode.ForYou)
                playlists = Library.GetPlaylists(); // on the UI thread: playlists are the app's live lists
            if (source.Kind == SourceKind.Playlist && playlists!.FirstOrDefault(p => p.Id == source.PlaylistId) is null)
            {
                SetStatus("That playlist is gone. Pick another source.");
                return false;
            }
            var request = new PlanRequest(mode, source, skipDisliked, queue, playlists, _seen!.ToArray(), _last,
                new HashSet<string>(_mine, StringComparer.Ordinal), mode == ShuffleMode.ForYou ? ForYouSnapshot(exploration, queue.CurrentTrackId) : null,
                Draw, Now?.Invoke() ?? DateTimeOffset.UtcNow);
            var plan = await Task.Run(() => MakePlan(request));
            if (generation != _generation || _disposed) return false; // a later choice won
            if (plan.Order.Count == 0 && plan.Built.Eligible == 0)
            {
                SetStatus(source.Kind == SourceKind.Queue
                    ? "There is nothing else in the queue to shuffle. Try Whole library or a playlist."
                    : "There are no songs in this source the shuffle can play.");
                return false;
            }
            if (plan.Order.Count == 0 && mode == ShuffleMode.NeverPlayed)
            {
                SetStatus("Every song in this source has been played. Never played has nothing left.");
                return false;
            }
            AdoptSeen(request.Seen, plan.Built);
            _session = new Session(mode, source, skipDisliked, exploration, plan.Order, plan.Songs, plan.Members);
            _session.Owned.UnionWith(_mine);
            _session.StartCurrent = queue.CurrentTrackId;
            _playing = queue.CurrentTrackId;
            var window = _session.Take(mode == ShuffleMode.ForYou ? ForYouWindow : WindowSize, _seen!, Playback.GetQueue(), mode, Clock());
            ApplyUpNext(Playback.GetQueue(), window, keepShuffled: false);
            SetStatus($"{ShuffleModes.Name(mode)} on {source.Name}: {plan.Built.Eligible} songs"
                      + (plan.Built.NewCycle ? ". Every song had been heard, so a new cycle started." : "."));
            return true;
        }
        catch (Exception ex)
        {
            if (generation == _generation) SetStatus("The shuffle could not be prepared: " + ex.GetBaseException().Message);
            _host.Log("start failed: " + ex);
            return false;
        }
    }

    /// <summary>Ends the session. The queue stays as it is.</summary>
    public void End()
    {
        _generation++;
        if (_session is null) return;
        _session = null;
        SetStatus("True Shuffle ended. The queue stays as it is.");
    }

    /// <summary>Forgets the remembered cycle (not Noctis's play counts or history).</summary>
    public async Task ResetCycleAsync()
    {
        await EnsureCycleAsync();
        _seen!.Clear();
        _last = null;
        await SaveNowAsync();
        SetStatus("The remembered songs were cleared. Every song can come up again.");
    }

    // ── Plans ──

    private sealed record PlanRequest(ShuffleMode Mode, ShuffleSource Source, bool SkipDisliked, PlaybackQueue Queue,
        IReadOnlyList<PlaylistInfo>? Playlists, string[] Seen, string? Last, HashSet<string> Mine, ForYouInputs? ForYou,
        Func<int, int>? Draw, DateTimeOffset Now);

    private sealed record ForYouInputs(List<PlayRecord> Plays, string[] Recent, string[] Skipped, double Exploration);

    private sealed record PlanResult(List<string> Order, Dictionary<string, Song> Songs, List<string> Members, ShufflePlan Built);

    private ForYouInputs ForYouSnapshot(string? exploration, string? current)
    {
        var recent = new List<string>(_recent.Count + 1);
        if (current != null) recent.Add(current);
        foreach (var r in _recent) if (r != current) recent.Add(r);
        return new ForYouInputs(_plays ??= SafeLoadPlays(), recent.ToArray(), _skipped.ToArray(), ForYouContext.ExplorationFor(exploration));
    }

    private List<PlayRecord> SafeLoadPlays()
    {
        try { return _listens.Load(); }
        catch (Exception ex) { _host.Log("listens.jsonl unreadable: " + ex.Message); return new List<PlayRecord>(); }
    }

    /// <summary>Worker thread: the whole library is copied and ordered here, never on the UI thread.</summary>
    private PlanResult MakePlan(PlanRequest r)
    {
        var all = Library.GetAll();
        var byId = new Dictionary<string, TrackInfo>(all.Count, StringComparer.Ordinal);
        foreach (var t in all) byId.TryAdd(t.Id, t);
        IEnumerable<string> ids = r.Source.Kind switch
        {
            SourceKind.Library => all.Select(t => t.Id),
            SourceKind.Playlist => r.Playlists!.First(p => p.Id == r.Source.PlaylistId).TrackIds,
            _ => (r.Queue.CurrentTrackId is { } c ? r.Queue.UpNext.Prepend(c) : r.Queue.UpNext).Where(id => !r.Mine.Contains(id)),
        };
        var members = ids.Where(byId.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
        return Plan(r, members, byId, upcoming: r.Mine);
    }

    private static PlanResult Plan(PlanRequest r, List<string> members, IReadOnlyDictionary<string, TrackInfo> byId, ISet<string>? upcoming)
    {
        var songs = new Dictionary<string, Song>(members.Count, StringComparer.Ordinal);
        foreach (var id in members) songs[id] = Song.From(byId[id]);
        Func<List<Song>, List<Song>>? rank = null;
        if (r.Mode == ShuffleMode.ForYou && r.ForYou is { } fy)
        {
            rank = pool =>
            {
                var facts = byId.Values.Select(TrackFacts.From).ToList();
                var lists = (r.Playlists ?? Array.Empty<PlaylistInfo>()).Select(p => (IReadOnlyCollection<string>)p.TrackIds);
                var model = TasteModel.Build(fy.Plays, facts, lists, r.Now.UtcDateTime);
                var ctx = new ForYouContext(fy.Recent, fy.Skipped, fy.Exploration, ForYouWindow, r.Draw?.Invoke(int.MaxValue) ?? Random.Shared.Next(), r.Now.UtcDateTime);
                var planned = ForYouPlanner.Plan(pool.Select(s => s.Id).ToList(), model, ctx);
                return planned.Select(id => songs[id]).ToList();
            };
        }
        var built = ShuffleEngine.Build(songs.Values, r.Mode, r.Seen, r.Queue.CurrentTrackId, r.Last, r.SkipDisliked, r.Now,
            r.Draw, upcoming, rank);
        return new PlanResult(built.Songs.Select(s => s.Id).ToList(), songs, members, built);
    }

    /// <summary>Takes over what a plan changed in the remembered cycle (a new cycle forgets the source's songs).</summary>
    private void AdoptSeen(string[] before, ShufflePlan plan)
    {
        if (!plan.NewCycle) return;
        var kept = plan.Seen as ISet<string> ?? plan.Seen.ToHashSet(StringComparer.Ordinal);
        foreach (var k in before) if (!kept.Contains(k)) _seen!.Remove(k);
        QueueSave();
    }

    // ── The queue ──

    /// <summary>
    /// Up next becomes: the user's own songs (in their order), then what is already shuffled up next
    /// (when <paramref name="keepShuffled"/>), then <paramref name="more"/>. A song the user queued is
    /// never also left further down the shuffle.
    /// </summary>
    private void ApplyUpNext(PlaybackQueue queue, IReadOnlyList<string> more, bool keepShuffled)
    {
        var present = new HashSet<string>(queue.UpNext, StringComparer.Ordinal);
        _mine.RemoveAll(id => !present.Contains(id));
        var mine = new HashSet<string>(_mine, StringComparer.Ordinal);
        var order = new List<string>(_mine.Count + queue.UpNext.Count + more.Count);
        order.AddRange(_mine);
        var placed = new HashSet<string>(_mine, StringComparer.Ordinal);
        if (keepShuffled)
            foreach (var id in queue.UpNext)
                if (!mine.Contains(id) && placed.Add(id)) order.Add(id);
        foreach (var id in more)
            if (placed.Add(id)) order.Add(id);
        _session?.Placed.UnionWith(more);
        if (!order.SequenceEqual(queue.UpNext)) Playback.SetUpNext(order);
        _expected = order;
    }

    /// <summary>Up next as the session last saw it, and the song playing then.</summary>
    private List<string> _expected = new();
    private string? _playing;

    /// <summary>
    /// Whether what plays now still comes from the session. A song change by the session's own
    /// queue leaves the song that starts somewhere in what was up next and the rest after it in the
    /// same order (some may have played or been removed); Previous puts the song that was playing
    /// back in front. Playing an album or a list replaces the queue with songs in another order, so
    /// the session is over even when all of them are in the library being shuffled.
    /// </summary>
    private bool StillOurs(Session s, string? current, IReadOnlyList<string> upNext)
    {
        if (current is null) return false;
        if (!s.Placed.Contains(current) && !s.Owned.Contains(current) && current != s.StartCurrent) return false;
        var i = upNext.Count > 0 && upNext[0] == _playing ? 1 : 0;
        var j = 0;
        for (; i < upNext.Count; i++)
        {
            while (j < _expected.Count && _expected[j] != upNext[i]) j++;
            if (j == _expected.Count) return false;
            j++;
        }
        return true;
    }

    private void OnTracksQueued(object? sender, TracksQueuedEventArgs e)
    {
        if (_disposed) return;
        // Play Next goes ahead of the user's earlier songs; Add to Queue behind them, in the order added.
        foreach (var id in e.TrackIds) _mine.Remove(id);
        if (e.PlayNext) _mine.InsertRange(0, e.TrackIds);
        else _mine.AddRange(e.TrackIds);
        if (_session is not { } s) return; // without a session Noctis's own queue order is left alone
        s.Owned.UnionWith(e.TrackIds);
        ApplyUpNext(Playback.GetQueue(), Array.Empty<string>(), keepShuffled: true);
    }

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        var queue = Playback.GetQueue();
        var current = queue.CurrentTrackId;
        EndTracking(advanced: current != null && current == _firstUpNext);
        _firstUpNext = queue.UpNext.Count > 0 ? queue.UpNext[0] : null;
        if (current != null)
        {
            _mine.Remove(current);
            _recent.Remove(current);
            _recent.Insert(0, current);
            if (_recent.Count > 200) _recent.RemoveAt(_recent.Count - 1);
        }
        StartTracking();
        if (_session is not { } s)
        {
            _playing = current;
            return;
        }

        // The user put something else on (played an album or a list, cleared the queue): Noctis
        // replaced the queue and the session is over.
        var ours = StillOurs(s, current, queue.UpNext);
        _expected = queue.UpNext.ToList();
        _playing = current;
        if (!ours)
        {
            _session = null;
            SetStatus("True Shuffle ended: something else is playing now. The queue stays as it is.");
            return;
        }
        if (current != null)
        {
            // Heard: it starts, it counts (also a song that is skipped at once).
            _seen?.Add(current);
            _last = current;
            QueueSave();
        }
        Refill(queue);
    }

    /// <summary>The shuffled songs up next are running low: the next part of the plan goes behind them.</summary>
    private void Refill(PlaybackQueue queue)
    {
        if (_session is not { } s || s.Busy) return;
        var low = s.Mode == ShuffleMode.ForYou ? ForYouRefillAt : RefillAt;
        var shuffledUpNext = 0;
        foreach (var id in queue.UpNext) if (s.Placed.Contains(id) && !_mine.Contains(id)) shuffledUpNext++;
        if (shuffledUpNext > low) return;
        var size = s.Mode == ShuffleMode.ForYou ? ForYouWindow : WindowSize;
        if (s.Mode != ShuffleMode.ForYou)
        {
            var batch = s.Take(size, _seen!, queue, s.Mode, Clock());
            if (batch.Count > 0)
            {
                ApplyUpNext(queue, batch, keepShuffled: true);
                if (shuffledUpNext + batch.Count > low) return;
            }
        }
        if (s.Remaining == 0 && !s.Finished) _ = PlanNextAsync(s);
    }

    /// <summary>The plan is used up (For you: re-picks every few songs): the next one, same mode and source.</summary>
    private async Task PlanNextAsync(Session s)
    {
        s.Busy = true;
        var generation = _generation;
        try
        {
            var queue = Playback.GetQueue();
            var playlists = s.Mode == ShuffleMode.ForYou || s.Source.Kind == SourceKind.Playlist ? Library.GetPlaylists() : null;
            var request = new PlanRequest(s.Mode, s.Source, s.SkipDisliked, queue, playlists, _seen!.ToArray(), _last,
                new HashSet<string>(_mine, StringComparer.Ordinal), s.Mode == ShuffleMode.ForYou ? ForYouSnapshot(s.Exploration, queue.CurrentTrackId) : null,
                Draw, Now?.Invoke() ?? DateTimeOffset.UtcNow);
            var members = s.Members.ToList();
            var upcoming = new HashSet<string>(queue.UpNext, StringComparer.Ordinal);
            var plan = await Task.Run(() =>
            {
                var all = Library.GetAll();
                var byId = new Dictionary<string, TrackInfo>(all.Count, StringComparer.Ordinal);
                foreach (var t in all) byId.TryAdd(t.Id, t);
                // A whole-library shuffle takes in songs added since; other sources keep their songs.
                var ids = s.Source.Kind == SourceKind.Library ? all.Select(t => t.Id).ToList() : members.Where(byId.ContainsKey).ToList();
                return Plan(request, ids, byId, upcoming);
            });
            if (!ReferenceEquals(_session, s) || generation != _generation || _disposed) return;
            if (plan.Order.Count == 0)
            {
                if (s.Mode == ShuffleMode.NeverPlayed)
                {
                    s.Finished = true;
                    Notify("Never played: no unplayed songs are left in this source. The songs already queued play on.");
                }
                return;
            }
            AdoptSeen(request.Seen, plan.Built);
            s.NextPlan(plan.Order, plan.Songs);
            var batch = s.Take(s.Mode == ShuffleMode.ForYou ? ForYouWindow : WindowSize, _seen!, Playback.GetQueue(), s.Mode, Clock());
            if (batch.Count > 0) ApplyUpNext(Playback.GetQueue(), batch, keepShuffled: true);
        }
        catch (Exception ex)
        {
            s.Finished = true;
            _host.Log("next plan failed: " + ex);
            Notify("True Shuffle could not add more songs. The songs already queued play on.");
        }
        finally { s.Busy = false; }
    }

    private DateTimeOffset Clock() => Now?.Invoke() ?? DateTimeOffset.UtcNow;

    // ── Listening log (For you) ──

    private void StartTracking()
    {
        try
        {
            if (_host.NowPlaying.Track is not { } t || Playback.GetQueue().CurrentTrackId is not { } id) return;
            _tracker.Start(id, t.Duration, DateTime.UtcNow, _clock.Elapsed, _host.NowPlaying.Position, _host.NowPlaying.IsPlaying);
        }
        catch (Exception ex) { _host.Log("tracking: " + ex.Message); }
    }

    private void EndTracking(bool advanced)
    {
        if (_tracker.TrackId is null) return;
        var remaining = TimeSpan.FromMilliseconds(_tracker.DurationMs) - _tracker.LastPosition;
        var end = PlayTracker.Classify(advanced, remaining, _tracker.Coverage);
        if (_tracker.End(end, DateTime.UtcNow, _clock.Elapsed) is not { } record) return;
        if (record.Outcome is PlayOutcome.StrongSkip or PlayOutcome.QuickSkip) _skipped.Add(record.TrackId);
        _plays?.Add(record);
        _listens.Append(record, ex => _host.Log("listens.jsonl: " + ex.Message));
    }

    private void Sample()
    {
        if (_disposed) return;
        try { _tracker.Sample(_host.NowPlaying.Position, _host.NowPlaying.IsPlaying, _clock.Elapsed); }
        catch { /* a sample more or less changes nothing */ }
    }

    private void OnIsPlayingChanged(object? sender, EventArgs e) => Sample();

    // ── The remembered cycle ──

    private async Task EnsureCycleAsync()
    {
        if (_seen != null) return;
        var data = await _cycle.LoadAsync();
        _seen ??= new HashSet<string>(data.Seen, StringComparer.Ordinal);
        _last = data.Last;
    }

    private void QueueSave()
    {
        if (_seen is null) return;
        if (_saving) { _saveAgain = true; return; }
        Saved = SaveLoopAsync();
    }

    private async Task SaveLoopAsync()
    {
        _saving = true;
        try
        {
            do
            {
                _saveAgain = false;
                try { await SaveNowAsync(); }
                catch (Exception ex) { _host.Log("cycle.json: " + ex.Message); } // retried with the next song
            } while (_saveAgain && !_disposed);
        }
        finally { _saving = false; }
    }

    private Task SaveNowAsync() => _cycle.SaveAsync(new CycleData(1, _seen!.ToArray(), _last));

    // ── Misc ──

    private void SetStatus(string status)
    {
        Status = status;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void Notify(string message)
    {
        SetStatus(message);
        try { _host.Notify(message); } catch (PluginPermissionException) { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        EndTracking(advanced: false);
        _disposed = true;
        _sampler?.Stop();
        try { _host.NowPlaying.TrackChanged -= OnTrackChanged; } catch { }
        try { _host.NowPlaying.IsPlayingChanged -= OnIsPlayingChanged; } catch { }
        try { Playback.TracksQueued -= OnTracksQueued; } catch { }
        _session = null;
        // One last save, off the UI thread (waiting here for the running save loop would wait for
        // its continuation, which needs this very thread). The store's own gate orders the writes.
        if (_seen is { } seen)
        {
            var data = new CycleData(1, seen.ToArray(), _last);
            try { Task.Run(() => _cycle.SaveAsync(data)).Wait(TimeSpan.FromSeconds(2)); }
            catch (Exception ex) { _host.Log("cycle.json on shutdown: " + ex.GetBaseException().Message); }
        }
    }
}

/// <summary>A session's plan and where it stands.</summary>
public sealed class Session
{
    public Session(ShuffleMode mode, ShuffleSource source, bool skipDisliked, string? exploration,
        List<string> plan, Dictionary<string, Song> songs, List<string> members)
    {
        Mode = mode;
        Source = source;
        SkipDisliked = skipDisliked;
        Exploration = exploration;
        Plan = plan;
        Songs = songs;
        Members = new HashSet<string>(members, StringComparer.Ordinal);
    }

    public ShuffleMode Mode { get; }
    public ShuffleSource Source { get; }
    public bool SkipDisliked { get; }
    public string? Exploration { get; }
    public List<string> Plan { get; private set; }
    public Dictionary<string, Song> Songs { get; private set; }
    /// <summary>The source's songs when it was applied (a later plan is made from them).</summary>
    public HashSet<string> Members { get; }
    /// <summary>The first song of <see cref="Plan"/> not put up next yet.</summary>
    public int Next { get; private set; }
    public int Remaining => Plan.Count - Next;
    /// <summary>Every song this session put up next.</summary>
    public HashSet<string> Placed { get; } = new(StringComparer.Ordinal);
    /// <summary>Songs the user queued themselves while the session ran.</summary>
    public HashSet<string> Owned { get; } = new(StringComparer.Ordinal);
    /// <summary>The song that was playing when the session began (Previous may go back to it).</summary>
    public string? StartCurrent { get; set; }
    public bool Busy { get; set; }
    public bool Finished { get; set; }

    public void NextPlan(List<string> plan, Dictionary<string, Song> songs)
    {
        Plan = plan;
        Songs = songs;
        Next = 0;
    }

    /// <summary>
    /// The next songs of the plan that are still wanted: not up next or playing already, not heard
    /// since the plan was made (No repeats, Discovery, Never played) and still allowed by the mode.
    /// O(<paramref name="count"/>): no library copy on the UI thread.
    /// </summary>
    public List<string> Take(int count, ISet<string> seen, PlaybackQueue queue, ShuffleMode mode, DateTimeOffset now)
    {
        var queued = new HashSet<string>(queue.UpNext, StringComparer.Ordinal);
        if (queue.CurrentTrackId is { } c) queued.Add(c);
        var batch = new List<string>(Math.Min(count, Remaining));
        while (batch.Count < count && Next < Plan.Count)
        {
            var id = Plan[Next++];
            if (!queued.Add(id)) continue;
            if (Songs.TryGetValue(id, out var song) && !ShuffleEngine.StillWanted(song, mode, seen, SkipDisliked, now)) continue;
            batch.Add(id);
        }
        return batch;
    }
}
