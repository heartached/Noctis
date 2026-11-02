using System.Collections.Specialized;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>Which part of the queue a row of <see cref="PlayerViewModel.QueueRows"/> is.</summary>
public enum QueueRowKind
{
    /// <summary>A song that already played (Keep Played Songs only).</summary>
    Played,
    /// <summary>The loaded song (Keep Played Songs only; otherwise it sits above the list).</summary>
    Current,
    /// <summary>An Up Next song.</summary>
    Upcoming,
}

/// <summary>
/// GitHub #134: the Queue panel's rows. With Keep Played Songs off they are Up Next, as
/// before. With it on the queue reads as one list in play order — what played (oldest
/// first), the loaded song in place, then Up Next — the way MusicBee's Now Playing list
/// does, and every row can be selected, removed, played or dragged like an Up Next one.
///
/// <see cref="QueueRows"/> mirrors <see cref="History"/>, <see cref="CurrentTrack"/> and
/// <see cref="UpNext"/> edit by edit (an advance is an insert, a remove and a replace, not
/// a reset), so the virtualized list keeps its realized rows and the selection, keyed by
/// row, follows them.
/// </summary>
public partial class PlayerViewModel
{
    /// <summary>The Queue panel's rows: Up Next, or with Keep Played Songs on, the played
    /// songs (oldest first), the loaded song and Up Next.</summary>
    public BulkObservableCollection<Track> QueueRows { get; } = new();

    /// <summary>Played rows at the top of <see cref="QueueRows"/>.</summary>
    public int QueuePlayedRowCount => KeepPlayedInQueue ? History.Count : 0;

    /// <summary>The loaded song's row in <see cref="QueueRows"/>, or -1 (none, or Keep Played
    /// Songs off: the panel shows it above the list).</summary>
    public int QueueCurrentRow => KeepPlayedInQueue && CurrentTrack != null ? History.Count : -1;

    /// <summary>Rows before Up Next's first song.</summary>
    private int QueueUpNextOffset => QueuePlayedRowCount + (QueueCurrentRow >= 0 ? 1 : 0);

    /// <summary>Nothing at all to show: no loaded song, nothing up next and no played rows.
    /// The panel's "Your queue is empty" shows only then (it showed whenever Up Next was
    /// empty, under a playing song and its played songs).</summary>
    public bool IsQueueEmpty => CurrentTrack == null && UpNext.Count == 0 && QueuePlayedRowCount == 0;

    /// <summary>A song is loaded above an empty Up Next list (Keep Played Songs off).</summary>
    public bool ShowQueueNothingUpNext => !KeepPlayedInQueue && CurrentTrack != null && UpNext.Count == 0;

    /// <summary>There are played rows to clear.</summary>
    public bool HasPlayedQueueRows => QueuePlayedRowCount > 0;

    private void InitQueueRows()
    {
        UpNext.CollectionChanged += OnUpNextChangedForRows;
        History.CollectionChanged += OnHistoryChangedForRows;
        RebuildQueueRows();
        // GitHub #154: rows added while the panel is open get their missing-file mark too.
        QueueRows.CollectionChanged += (_, _) => { if (IsQueuePopupOpen) RefreshMissingMarksSoon(); };
    }

    public QueueRowKind GetQueueRowKind(int row)
    {
        if (row < QueuePlayedRowCount) return QueueRowKind.Played;
        return row == QueueCurrentRow ? QueueRowKind.Current : QueueRowKind.Upcoming;
    }

    /// <summary>The Up Next index of an upcoming row, or -1.</summary>
    public int QueueRowToUpNextIndex(int row)
    {
        var i = row - QueueUpNextOffset;
        return i >= 0 && i < UpNext.Count ? i : -1;
    }

    /// <summary>The History index (0 = newest) of a played row, or -1.</summary>
    private int QueueRowToHistoryIndex(int row)
        => row >= 0 && row < QueuePlayedRowCount ? History.Count - 1 - row : -1;

    /// <summary>The panel's position label for a row: Up Next rows count from 1 (the next
    /// song); played rows and the loaded song carry a mark instead of a number.</summary>
    public string QueueRowNumber(int row)
    {
        var i = QueueRowToUpNextIndex(row);
        return i >= 0 ? (i + 1).ToString() : string.Empty;
    }

    /// <summary>Double-click: an upcoming row plays (see <see cref="PlayFromUpNextAt"/>), a
    /// played row plays again with what followed it put back in front of Up Next, and the
    /// loaded song resumes if it is paused or stopped.</summary>
    public void PlayQueueRow(int row)
    {
        switch (GetQueueRowKind(row))
        {
            case QueueRowKind.Played:
                PlayFromHistoryAt(QueueRowToHistoryIndex(row));
                break;
            case QueueRowKind.Current:
                if (State != PlaybackState.Playing) PlayPause();
                break;
            default:
                var i = QueueRowToUpNextIndex(row);
                if (i >= 0) PlayFromUpNextAt(i);
                break;
        }
    }

    /// <summary>Removes the rows at <paramref name="rows"/> (any order): upcoming rows leave
    /// Up Next as <see cref="RemoveManyFromQueue"/> does, played rows leave the played list.
    /// The loaded song is never removed here.</summary>
    public void RemoveQueueRows(IEnumerable<int> rows)
    {
        var list = rows.Distinct().ToList();
        var upNext = list.Select(QueueRowToUpNextIndex).Where(i => i >= 0).ToList();
        var history = list.Select(QueueRowToHistoryIndex).Where(i => i >= 0).ToList();
        if (upNext.Count > 0) RemoveManyFromQueue(upNext);
        if (history.Count > 0) RemovePlayedEntries(history);
    }

    /// <summary>"Clear Played": drops every played row (Keep Played Songs). Previous then
    /// has no played song to go back to.</summary>
    public void ClearPlayed()
    {
        if (History.Count == 0) return;
        DebugLogger.Info(DebugLogger.Category.Queue, "ClearPlayed", $"cleared={History.Count}");
        MarkQueueChanged();
        History.Clear();
        _queueHistoryDepth = 0;
    }

    /// <summary>Removes the History entries at <paramref name="historyIndices"/>. Played
    /// rows are a record of what played: Repeat All's cycle is not touched.</summary>
    private void RemovePlayedEntries(IReadOnlyCollection<int> historyIndices)
    {
        var drop = historyIndices.Where(i => i >= 0 && i < History.Count).Distinct()
            .OrderByDescending(i => i).ToList();
        if (drop.Count == 0) return;
        DebugLogger.Info(DebugLogger.Category.Queue, "RemovePlayed", $"count={drop.Count}");
        MarkQueueChanged();
        var depth = _queueHistoryDepth;
        _deferPlayedSync = true;
        try
        {
            // High → low so the lower indices stay valid.
            foreach (var i in drop)
            {
                if (i < depth) _queueHistoryDepth--;
                History.RemoveAt(i);
            }
        }
        finally { _deferPlayedSync = false; }
        _queueHistoryDepth = Math.Max(0, _queueHistoryDepth);
        SyncPlayedTracks();
    }

    /// <summary>Drag of one row from <paramref name="from"/> to slot <paramref name="to"/>
    /// (ObservableCollection.Move semantics).</summary>
    public void MoveQueueRow(int from, int to)
    {
        if (!KeepPlayedInQueue)
        {
            MoveInQueue(from, to);
            return;
        }
        if (from == to) return;
        MoveQueueRows(new[] { from }, to > from ? to + 1 : to);
    }

    /// <summary>
    /// Moves the rows at <paramref name="rows"/> as one block to <paramref name="insertIndex"/>,
    /// an insertion index in the CURRENT rows (the <see cref="MoveBlockInQueue"/> contract).
    /// With Keep Played Songs on the loaded song stays put and the block lands on whichever
    /// side of it the slot is: dropped above it a row joins the played songs, below it Up
    /// Next. Returns the block's first row after the move, or -1.
    /// </summary>
    public int MoveQueueRows(IReadOnlyCollection<int> rows, int insertIndex)
    {
        if (!KeepPlayedInQueue) return MoveBlockInQueue(rows, insertIndex);

        var current = QueueCurrentRow;
        var block = rows.Where(r => r >= 0 && r < QueueRows.Count && r != current).Distinct().Order().ToList();
        if (block.Count == 0) return -1;
        insertIndex = Math.Clamp(insertIndex, 0, QueueRows.Count);
        var landAt = insertIndex - block.Count(r => r < insertIndex);
        var contiguous = block[^1] - block[0] == block.Count - 1;
        if (contiguous && landAt == block[0]) return -1;

        // Both inside Up Next: the Up Next move, as before.
        var offset = QueueUpNextOffset;
        if (block[0] >= offset && insertIndex >= offset)
        {
            var moved = MoveBlockInQueue(block.Select(r => r - offset).ToList(), insertIndex - offset);
            return moved < 0 ? -1 : moved + offset;
        }

        CancelAutoMixTransition("queue changed");
        MarkQueueChanged();
        var tracks = block.Select(r => QueueRows[r]).ToList();

        _deferPlayedSync = true;
        try
        {
            // Lift the block out, high → low; each row maps through the live counts.
            for (var k = block.Count - 1; k >= 0; k--)
            {
                var row = block[k];
                var up = QueueRowToUpNextIndex(row);
                if (up >= 0)
                {
                    UpNext.RemoveAt(up);
                }
                else
                {
                    var h = QueueRowToHistoryIndex(row);
                    if (h < _queueHistoryDepth) _queueHistoryDepth--;
                    History.RemoveAt(h);
                }
            }

            // Drop it at landAt: a slot at or before the loaded song's row is played.
            for (var k = 0; k < tracks.Count; k++)
            {
                var q = landAt + k;
                var played = History.Count;
                var hasCurrent = CurrentTrack != null;
                if (q < played || (q == played && hasCurrent))
                {
                    History.Insert(played - q, tracks[k]);
                    // A row placed among this queue's played songs counts as this queue's.
                    if (played - q <= _queueHistoryDepth) _queueHistoryDepth++;
                }
                else
                {
                    UpNext.Insert(q - played - (hasCurrent ? 1 : 0), tracks[k]);
                }
            }
        }
        finally { _deferPlayedSync = false; }
        _queueHistoryDepth = Math.Clamp(_queueHistoryDepth, 0, History.Count);
        SyncPlayedTracks();
        DebugLogger.Info(DebugLogger.Category.Queue, "MoveQueueRows", $"count={block.Count}, landAt={landAt}");
        return landAt;
    }

    // ── Mirror ──

    private void RebuildQueueRows()
    {
        var rows = new List<Track>(UpNext.Count + (KeepPlayedInQueue ? History.Count + 1 : 0));
        if (KeepPlayedInQueue)
        {
            for (var i = History.Count - 1; i >= 0; i--) rows.Add(History[i]);
            if (CurrentTrack != null) rows.Add(CurrentTrack);
        }
        rows.AddRange(UpNext);
        QueueRows.ReplaceAll(rows);
        NotifyQueueRowState();
    }

    /// <summary>Re-checks the mirror's length after an edit; a mismatch (an event shape the
    /// mirror does not translate) rebuilds it rather than leave the panel showing a stale list.</summary>
    private void VerifyQueueRows()
    {
        if (QueueRows.Count != QueueUpNextOffset + UpNext.Count) RebuildQueueRows();
        else NotifyQueueRowState();
    }

    private void NotifyQueueRowState()
    {
        OnPropertyChanged(nameof(IsQueueEmpty));
        OnPropertyChanged(nameof(ShowQueueNothingUpNext));
        OnPropertyChanged(nameof(HasPlayedQueueRows));
    }

    /// <summary>Above this many items one edit is cheaper as a rebuild.</summary>
    private const int QueueRowsMirrorLimit = 256;

    private void OnUpNextChangedForRows(object? sender, NotifyCollectionChangedEventArgs e)
    {
        var offset = QueueUpNextOffset;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewItems is { Count: <= QueueRowsMirrorLimit } added:
                for (var k = 0; k < added.Count; k++)
                    QueueRows.Insert(offset + e.NewStartingIndex + k, (Track)added[k]!);
                break;
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is { Count: <= QueueRowsMirrorLimit } removed:
                for (var k = 0; k < removed.Count; k++)
                    QueueRows.RemoveAt(offset + e.OldStartingIndex);
                break;
            case NotifyCollectionChangedAction.Replace when e.NewStartingIndex >= 0 && e.NewItems is { Count: 1 } replaced:
                QueueRows[offset + e.NewStartingIndex] = (Track)replaced[0]!;
                break;
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0 && e.NewItems is { Count: 1 }:
                QueueRows.Move(offset + e.OldStartingIndex, offset + e.NewStartingIndex);
                break;
            default:
                RebuildQueueRows();
                return;
        }
        VerifyQueueRows();
    }

    private void OnHistoryChangedForRows(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!KeepPlayedInQueue)
        {
            NotifyQueueRowState();
            return;
        }
        // Played row r is History[Count - 1 - r]: History is newest-first, the rows oldest-first.
        var count = History.Count;
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0 && e.NewItems is { Count: <= QueueRowsMirrorLimit } added:
            {
                var before = count - added.Count;
                var at = before - e.NewStartingIndex;
                for (var j = 0; j < added.Count; j++)
                    QueueRows.Insert(at + j, (Track)added[added.Count - 1 - j]!);
                break;
            }
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0 && e.OldItems is { Count: <= QueueRowsMirrorLimit } removed:
            {
                var before = count + removed.Count;
                var at = before - e.OldStartingIndex - removed.Count;
                for (var j = 0; j < removed.Count; j++)
                    QueueRows.RemoveAt(at);
                break;
            }
            case NotifyCollectionChangedAction.Replace when e.NewStartingIndex >= 0 && e.NewItems is { Count: 1 } replaced:
                QueueRows[count - 1 - e.NewStartingIndex] = (Track)replaced[0]!;
                break;
            default:
                RebuildQueueRows();
                return;
        }
        VerifyQueueRows();
    }

    private void OnCurrentTrackChangedForRows(Track? oldValue, Track? newValue)
    {
        if (!KeepPlayedInQueue)
        {
            NotifyQueueRowState();
            return;
        }
        var at = History.Count;
        if (oldValue != null && newValue != null && at < QueueRows.Count && ReferenceEquals(QueueRows[at], oldValue))
            QueueRows[at] = newValue;
        else if (oldValue == null && newValue != null && at <= QueueRows.Count)
            QueueRows.Insert(at, newValue);
        else if (oldValue != null && newValue == null && at < QueueRows.Count && ReferenceEquals(QueueRows[at], oldValue))
            QueueRows.RemoveAt(at);
        else
        {
            RebuildQueueRows();
            return;
        }
        VerifyQueueRows();
    }
}
