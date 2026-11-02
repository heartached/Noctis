using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// GitHub #154: songs in the queue whose files were moved or deleted. Every start goes through
/// <see cref="PlayTrack"/>, which looks at the file first: a missing one is passed over in the
/// direction of travel (forward for an advance, a jump or a fresh queue; back for Previous) to
/// the nearest song that can play, Repeat All wrapping once. When nothing is left to play the
/// player stops on the first missing song with the queue kept, so Play retries once the files
/// are back. Before, the player started each missing song, the engine reported "File not
/// found" and the error breaker stopped after five in a row, or at once on the last song,
/// leaving a moved album or a dead last song "playing" in silence.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>Settings ▸ Playback ▸ Missing Songs: a missing song passed over leaves the queue
    /// (true) or stays in it, marked (false, the default). Songs on a drive that is not there
    /// at all always stay: that is an unplugged drive, not deleted files.</summary>
    [ObservableProperty] private bool _removeMissingFromQueue;

    /// <summary>A short line for the notice pill: which missing songs were passed over, or that
    /// playback stopped because none of what is left can be found. UI thread.</summary>
    public event EventHandler<string>? MissingTracksNotice;

    private sealed class MissingRun
    {
        public bool Backward;
        public bool Wrapped;
        public readonly List<Track> Skipped = new();
        public readonly HashSet<Track> Removed = new(ReferenceEqualityComparer.Instance);
        public readonly Dictionary<string, bool> RootReachable = new(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Asks the engine whether the file is there now and keeps the row mark in step.</summary>
    private bool CheckMissing(Track track)
    {
        var missing = !_audioPlayer.SourceExists(track.FilePath);
        if (track.IsFileMissing != missing) track.IsFileMissing = missing;
        return missing;
    }

    /// <summary>
    /// Starts <paramref name="track"/>, or when its file is missing, the nearest song after it
    /// (before it when <paramref name="backward"/>) that can play.
    /// </summary>
    /// <param name="wrapped">The caller already wrapped Repeat All for this advance: no second wrap.</param>
    private void PlayTrack(Track track, bool backward = false, bool wrapped = false)
    {
        if (!CheckMissing(track))
        {
            PlayTrackCore(track);
            return;
        }

        var snapshot = (Current: CurrentTrack, UpNext: UpNext.ToList(), History: History.ToList(),
            Depth: _queueHistoryDepth, Preceding: _precedingInQueue.ToList(),
            Cycle: _repeatCycleTracks.ToList(), Original: _originalQueue.ToList());
        var run = new MissingRun { Backward = backward, Wrapped = wrapped };
        CancelAutoMixTransition("missing file");
        MarkQueueChanged();

        // Each step consumes a queue entry or ends the run; the bound only guards against a
        // queue edit made underneath (none is made from here).
        var guard = 2 * (UpNext.Count + History.Count + _repeatCycleTracks.Count + _precedingInQueue.Count) + 8;
        Track? next = track;
        _deferPlayedSync = true;
        try
        {
            while (next != null && CheckMissing(next))
            {
                if (guard-- <= 0) { next = null; break; }
                if (!run.Skipped.Contains(next)) run.Skipped.Add(next);
                next = run.Backward ? StepBackPastMissing(next, run) : StepForwardPastMissing(next, run);
            }
        }
        finally { _deferPlayedSync = false; }
        SyncPlayedTracks();

        DebugLogger.Info(DebugLogger.Category.Playback, "MissingFiles.Skip",
            $"skipped={run.Skipped.Count}, removed={run.Removed.Count}, backward={backward}, wrapped={run.Wrapped}, found={next != null}");
        DebugLog.Write("Audio", next != null
            ? $"Passed over {run.Skipped.Count} song(s) whose file is missing"
            : $"No song left in the queue can be found on disk ({run.Skipped.Count} missing) — stopped");

        if (next != null)
            PlayTrackCore(next);
        else
            StopOnMissing(track, snapshot.Current, snapshot.UpNext, snapshot.History, snapshot.Depth,
                snapshot.Preceding, snapshot.Cycle, snapshot.Original, run);

        var notice = DescribeMissingRun(run.Skipped.Count, run.Skipped[0].Title, run.Removed.Count > 0, stopped: next == null);
        MissingTracksNotice?.Invoke(this, notice);
    }

    /// <summary>The notice line for a run over <paramref name="count"/> missing songs.</summary>
    /// <remarks>Internal for tests (InternalsVisibleTo Noctis.Tests).</remarks>
    internal static string DescribeMissingRun(int count, string? firstTitle, bool removed, bool stopped)
    {
        if (stopped) return Loc.T("Queue.MissingStopped");
        var title = string.IsNullOrWhiteSpace(firstTitle) ? Loc.T("QueueToast.OneSong") : firstTitle!;
        return count == 1
            ? Loc.T(removed ? "Queue.MissingRemovedOne" : "Queue.MissingSkippedOne", title)
            : Loc.T(removed ? "Queue.MissingRemovedMany" : "Queue.MissingSkippedMany", count);
    }

    /// <summary>
    /// Takes a missing song out of the way of travel: off the queue (Remove from Queue, its
    /// drive present), else into History going forward or back to the front of Up Next going
    /// back — where a Next / Previous would have left a song that played.
    /// </summary>
    private void PassMissing(Track missing, bool forward, MissingRun run)
    {
        if (RemoveMissingFromQueue && IsRootReachable(missing.FilePath, run))
        {
            run.Removed.Add(missing);
            for (var i = UpNext.Count - 1; i >= 0; i--)
                if (ReferenceEquals(UpNext[i], missing)) UpNext.RemoveAt(i);
            _repeatCycleTracks.RemoveAll(t => ReferenceEquals(t, missing));
            _originalQueue.RemoveAll(t => ReferenceEquals(t, missing));
            _precedingInQueue.RemoveAll(t => ReferenceEquals(t, missing));
            return;
        }
        if (forward)
        {
            History.Insert(0, missing);
            _queueHistoryDepth++;
            TrimHistory();
        }
        else
        {
            UpNext.Insert(0, missing);
        }
    }

    private static bool IsRootReachable(string? path, MissingRun run)
    {
        if (string.IsNullOrWhiteSpace(path)) return true;
        string? root;
        try { root = Path.GetPathRoot(path); }
        catch { return true; }
        if (string.IsNullOrEmpty(root)) return true;
        if (!run.RootReachable.TryGetValue(root, out var reachable))
            run.RootReachable[root] = reachable = UnreachableRootHint(path, Directory.Exists) == null;
        return reachable;
    }

    /// <summary>The song after <paramref name="missing"/>: the head of Up Next, else (Repeat
    /// All, once per run) the start of a new pass. Null when nothing is left.</summary>
    private Track? StepForwardPastMissing(Track missing, MissingRun run)
    {
        PassMissing(missing, forward: true, run);
        if (UpNext.Count > 0)
        {
            var next = UpNext[0];
            UpNext.RemoveAt(0);
            return next;
        }
        if (RepeatMode == RepeatMode.All && !run.Wrapped)
        {
            run.Wrapped = true;
            var pass = BuildRepeatAllPass();
            if (pass is { Count: > 0 })
            {
                UpNext.ReplaceAll(pass.Skip(1).ToList());
                return pass[0];
            }
        }
        return null;
    }

    /// <summary>The song before <paramref name="missing"/>, in Previous's order: what this queue
    /// played, then the songs above its start, then older History. With nothing back there it
    /// turns round and goes forward (to the song Previous was pressed on).</summary>
    private Track? StepBackPastMissing(Track missing, MissingRun run)
    {
        PassMissing(missing, forward: false, run);
        if (PlayedInThisQueue > 0 || (_precedingInQueue.Count == 0 && History.Count > 0))
        {
            var prev = History[0];
            History.RemoveAt(0);
            if (_queueHistoryDepth > 0) _queueHistoryDepth--;
            return prev;
        }
        if (_precedingInQueue.Count > 0)
        {
            var prev = _precedingInQueue[^1];
            _precedingInQueue.RemoveAt(_precedingInQueue.Count - 1);
            return prev;
        }
        run.Backward = false;
        if (UpNext.Count == 0) return null;
        var next = UpNext[0];
        UpNext.RemoveAt(0);
        return next;
    }

    /// <summary>
    /// Nothing in the queue can play: back to the queue as it was when the run began, stopped on
    /// the song the run was asked to start (Play retries it once the files are back), minus the
    /// songs Remove from Queue took off.
    /// </summary>
    private void StopOnMissing(Track first, Track? current, List<Track> upNext, List<Track> history, int depth,
        List<Track> preceding, List<Track> cycle, List<Track> original, MissingRun run)
    {
        bool Kept(Track t) => !run.Removed.Contains(t);
        _hasPendingSeekTarget = false;
        CancelNaturalEndFallback();
        _audioPlayer.Stop();
        State = PlaybackState.Stopped;

        _deferPlayedSync = true;
        try
        {
            History.ReplaceAll(history.Where(Kept).ToList());
            UpNext.ReplaceAll(upNext.Where(Kept).ToList());
        }
        finally { _deferPlayedSync = false; }
        SyncPlayedTracks();
        _queueHistoryDepth = Math.Min(depth, History.Count);
        _precedingInQueue.Clear();
        _precedingInQueue.AddRange(preceding.Where(Kept));
        _repeatCycleTracks = cycle.Where(Kept).ToList();
        _originalQueue = original.Where(Kept).ToList();

        var stopOn = Kept(first) ? first : run.Skipped.FirstOrDefault(Kept);
        if (stopOn == null)
        {
            // Every one was taken off: the next queued song (if any) loads, else the player empties.
            if (UpNext.Count == 0)
            {
                StopAndClear("missingFilesRemoved", keepHistory: true);
                return;
            }
            stopOn = UpNext[0];
            UpNext.RemoveAt(0);
        }

        var keepPosition = ReferenceEquals(stopOn, current);
        if (!ReferenceEquals(CurrentTrack, stopOn))
        {
            CurrentTrack = stopOn;
            LoadAlbumArt(stopOn);
        }
        Duration = stopOn.Duration;
        DurationText = FormatTime(stopOn.Duration);
        if (!keepPosition)
        {
            Position = TimeSpan.Zero;
            PositionFraction = 0;
            PositionText = "0:00";
        }
        RemainingTimeText = FormatTime(Duration > Position ? Duration - Position : TimeSpan.Zero);
        SaveQueueStateInBackground();
    }

    // ── Row marks ──

    private int _missingMarksGeneration;

    /// <summary>
    /// Looks at the files of the songs the queue shows, off the UI thread, and marks the rows
    /// whose file is missing (and unmarks the ones that came back, e.g. a drive plugged in
    /// again). Runs when the Queue panel opens and as the queue changes while it is open.
    /// </summary>
    internal Task RefreshMissingMarksAsync()
    {
        var generation = ++_missingMarksGeneration;
        var tracks = new List<Track>(Math.Min(QueueRows.Count + 1, MissingMarksLimit));
        if (CurrentTrack != null) tracks.Add(CurrentTrack);
        foreach (var t in QueueRows)
        {
            if (tracks.Count >= MissingMarksLimit) break;
            tracks.Add(t);
        }
        if (tracks.Count == 0) return Task.CompletedTask;
        return Task.Run(() =>
        {
            var results = new List<(Track Track, bool Missing)>(tracks.Count);
            foreach (var t in tracks)
            {
                if (generation != _missingMarksGeneration) return;
                bool missing;
                try { missing = !_audioPlayer.SourceExists(t.FilePath); }
                catch { continue; }
                if (missing != t.IsFileMissing) results.Add((t, missing));
            }
            if (results.Count == 0) return;
            Dispatcher.UIThread.Post(() =>
            {
                foreach (var (t, missing) in results) t.IsFileMissing = missing;
            });
        });
    }

    /// <summary>Rows looked at per refresh: the first screens' worth and far beyond.</summary>
    private const int MissingMarksLimit = 2000;

    private DispatcherTimer? _missingMarksTimer;

    private void RefreshMissingMarksSoon()
    {
        if (_missingMarksTimer == null)
        {
            _missingMarksTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _missingMarksTimer.Tick += (_, _) =>
            {
                _missingMarksTimer!.Stop();
                _ = RefreshMissingMarksAsync();
            };
        }
        _missingMarksTimer.Stop();
        _missingMarksTimer.Start();
    }

    partial void OnIsQueuePopupOpenChanged(bool value)
    {
        if (value) _ = RefreshMissingMarksAsync();
    }
}
