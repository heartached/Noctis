using System.Collections.Specialized;

namespace Noctis.Helpers;

/// <summary>
/// GitHub #85: the queue panel's multi-selection, keyed by ROW INDEX. The queue can hold
/// the same Track twice and Track compares by reference, so keying on the Track (the
/// PlaylistView approach) would select, move or remove every copy at once. Each row also
/// remembers the item it held when selected: <see cref="Apply"/> re-maps the rows as the
/// queue mutates (a track advancing is UpNext.RemoveAt(0)) and, on a Reset (AddRange /
/// ReplaceAll), keeps only rows that still hold the same item.
/// </summary>
public sealed class QueueRowSelection<T> where T : class
{
    private readonly IReadOnlyList<T> _rows;
    // Highest row first: the rows a queue change moves (those at or past it) lead, so
    // Remap walks only them. Snapshot hands the rows out ascending.
    private readonly SortedDictionary<int, T> _selected = new(Comparer<int>.Create((a, b) => b.CompareTo(a)));

    public QueueRowSelection(IReadOnlyList<T> rows) => _rows = rows;

    /// <summary>Row a Shift+Click range starts from, or -1.</summary>
    public int Anchor { get; private set; } = -1;

    public int Count => _selected.Count;

    public bool Contains(int row) => _selected.ContainsKey(row);

    /// <summary>The selected rows, ascending — snapshot this per action.</summary>
    public int[] Snapshot()
    {
        var rows = _selected.Keys.ToArray();
        Array.Reverse(rows);
        return rows;
    }

    public void Clear()
    {
        _selected.Clear();
        Anchor = -1;
    }

    /// <summary>Plain click: this row only, and it becomes the anchor.</summary>
    public void SelectOnly(int row)
    {
        _selected.Clear();
        Add(row);
        Anchor = Valid(row) ? row : -1;
    }

    /// <summary>Ctrl+Click: flip this row, and it becomes the anchor.</summary>
    public void Toggle(int row)
    {
        if (!Valid(row)) return;
        if (!_selected.Remove(row)) Add(row);
        Anchor = row;
    }

    /// <summary>Shift+Click: every row from the anchor to <paramref name="row"/> (the anchor
    /// stays put). Ctrl+Shift (<paramref name="additive"/>) keeps the rows already selected.</summary>
    public void SelectRangeTo(int row, bool additive = false)
    {
        if (!Valid(row)) return;
        var anchor = Valid(Anchor) ? Anchor : row;
        if (!additive) _selected.Clear();
        foreach (var i in Range(anchor, row)) Add(i);
        Anchor = anchor;
    }

    /// <summary>Ctrl+A.</summary>
    public void SelectAll()
    {
        _selected.Clear();
        for (var i = 0; i < _rows.Count; i++) Add(i);
        if (!Valid(Anchor)) Anchor = _rows.Count > 0 ? 0 : -1;
    }

    /// <summary>Selects exactly <paramref name="rows"/> (e.g. a moved block's new rows).</summary>
    public void Select(IEnumerable<int> rows)
    {
        _selected.Clear();
        foreach (var i in rows) Add(i);
        Anchor = _selected.Count > 0 ? _selected.Keys.Min() : -1;
    }

    /// <summary>GitHub #88 rubber band: the rows a band spanning <paramref name="from"/>..<paramref name="to"/>
    /// (either order, may run past either end of the queue) covers, plus <paramref name="keep"/>
    /// (the selection when a Ctrl/Shift band started). The band's first row becomes the anchor,
    /// so a later Shift+Click extends from where the band began.</summary>
    public void SelectBand(int from, int to, IEnumerable<int>? keep = null)
    {
        _selected.Clear();
        if (keep != null)
            foreach (var i in keep) Add(i);
        var lo = Math.Max(Math.Min(from, to), 0);
        var hi = Math.Min(Math.Max(from, to), _rows.Count - 1);
        for (var i = lo; i <= hi; i++) Add(i);
        if (lo <= hi) Anchor = Math.Clamp(from, lo, hi);
        else if (!Valid(Anchor)) Anchor = -1;
    }

    /// <summary>Inclusive run of rows between <paramref name="anchor"/> and
    /// <paramref name="row"/>, in either direction, ascending.</summary>
    public static IEnumerable<int> Range(int anchor, int row)
    {
        var from = Math.Min(anchor, row);
        var to = Math.Max(anchor, row);
        return Enumerable.Range(from, to - from + 1);
    }

    /// <summary>Re-maps the selection after the queue raised <paramref name="e"/>. Call it
    /// from the collection's CollectionChanged, before anything else mutates the queue.</summary>
    public void Apply(NotifyCollectionChangedEventArgs e)
    {
        switch (e.Action)
        {
            case NotifyCollectionChangedAction.Add when e.NewStartingIndex >= 0:
            {
                var at = e.NewStartingIndex;
                var n = e.NewItems?.Count ?? 0;
                Remap(at, i => i >= at ? i + n : i);
                break;
            }
            case NotifyCollectionChangedAction.Remove when e.OldStartingIndex >= 0:
            {
                var at = e.OldStartingIndex;
                var n = e.OldItems?.Count ?? 0;
                Remap(at, i => i < at ? i : i >= at + n ? i - n : -1);
                break;
            }
            case NotifyCollectionChangedAction.Move when e.OldStartingIndex >= 0 && e.NewStartingIndex >= 0:
            {
                var from = e.OldStartingIndex;
                var to = e.NewStartingIndex;
                Remap(Math.Min(from, to), i =>
                {
                    if (i == from) return to;
                    if (from < to && i > from && i <= to) return i - 1;
                    if (to < from && i >= to && i < from) return i + 1;
                    return i;
                });
                break;
            }
            default:
            {
                // Replace / Reset (AddRange, ReplaceAll, Clear) or an index-less event:
                // keep the rows that still hold the item they held when selected.
                foreach (var row in _selected.Keys.ToList())
                    if (!Valid(row) || !ReferenceEquals(_rows[row], _selected[row]))
                        _selected.Remove(row);
                if (!Valid(Anchor)) Anchor = -1;
                break;
            }
        }
    }

    /// <summary>Re-maps the selected rows at or past <paramref name="start"/>; the rows
    /// before it keep their index and are not visited. RemoveManyFromQueue removes row by
    /// row, high to low, so re-mapping the whole selection on each Remove made deleting a
    /// Ctrl+A'd queue O(rows²) (audit U03).</summary>
    private void Remap(int start, Func<int, int> map)
    {
        var moved = _selected.TakeWhile(kv => kv.Key >= start)
                             .Select(kv => (Old: kv.Key, Row: map(kv.Key), Item: kv.Value)).ToList();
        foreach (var (old, _, _) in moved)
            _selected.Remove(old);
        foreach (var (_, row, item) in moved)
            if (row >= 0) _selected[row] = item;
        Anchor = Anchor >= 0 ? map(Anchor) : -1;
    }

    private void Add(int row)
    {
        if (Valid(row)) _selected[row] = _rows[row];
    }

    private bool Valid(int row) => row >= 0 && row < _rows.Count;
}
