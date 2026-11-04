using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Noctis.Helpers;

/// <summary>One column a <see cref="ColumnLayout"/> can show (GitHub #151).</summary>
public sealed class ColumnSpec
{
    /// <summary>Stable id: what the cells carry (ColumnsPanel.Column) and what settings store.</summary>
    public required string Key { get; init; }

    /// <summary>Header-menu name.</summary>
    public string Label { get; init; } = string.Empty;

    /// <summary>Width before the user resizes it. Ignored while <see cref="Star"/> flexes it.</summary>
    public double Width { get; init; }

    /// <summary>Narrowest the column gets, by a drag or by a narrow window.</summary>
    public double MinWidth { get; init; } = 40;

    /// <summary>Above zero: the column shares the leftover width with the other flexing
    /// columns, in this proportion, until the user drags it to a width of its own.</summary>
    public double Star { get; init; }

    /// <summary>Shown when nothing was saved.</summary>
    public bool DefaultVisible { get; init; } = true;

    /// <summary>The header menu can hide it (the title column cannot).</summary>
    public bool CanHide { get; init; } = true;

    /// <summary>Pinned to the right end (the rows' 3-dots button): never moved, resized, hidden or listed.</summary>
    public bool Pinned { get; init; }

    /// <summary>Sort key a header click passes on; null for a column that does not sort.</summary>
    public string? SortKey { get; init; }
}

/// <summary>
/// The shared column state of one list (GitHub #151): order, visibility and the widths the
/// user dragged. Every row panel and the header read the same instance, so a 50k-row list
/// computes its column widths once per width (<see cref="Compute"/> caches them), not per row,
/// and a drag moves the header and every realized row together.
/// <para>
/// Columns nobody resized keep the old automatic behaviour: fixed ones keep their width and
/// the <see cref="ColumnSpec.Star"/> ones (Title / Artist / Album) share what is left.
/// </para>
/// </summary>
public sealed class ColumnLayout
{
    private readonly Dictionary<string, ColumnSpec> _byKey;
    private readonly List<string> _order;
    private readonly HashSet<string> _hidden;
    private readonly Dictionary<string, double> _widths = new(StringComparer.Ordinal);
    private ColumnSpec[]? _visible;
    private double _cacheWidth = double.NaN;
    private double[] _cacheWidths = Array.Empty<double>();
    private double[] _cacheOffsets = Array.Empty<double>();

    public ColumnLayout(IEnumerable<ColumnSpec> specs)
    {
        Specs = specs.ToArray();
        _byKey = Specs.ToDictionary(s => s.Key, StringComparer.Ordinal);
        _order = DefaultOrder();
        _hidden = new HashSet<string>(Specs.Where(s => !s.DefaultVisible).Select(s => s.Key), StringComparer.Ordinal);
    }

    /// <summary>Every column, in declaration (default) order.</summary>
    public IReadOnlyList<ColumnSpec> Specs { get; }

    /// <summary>Order, visibility or a width changed: panels re-measure.</summary>
    public event EventHandler? Changed;

    /// <summary>The user finished a change (drag released, menu pick): time to save.</summary>
    public event EventHandler? Committed;

    /// <summary>
    /// The header menu asks for a column to be shown or hidden. A list whose visibility lives
    /// elsewhere (the Songs page's Show…Column settings) handles this and answers with
    /// <see cref="SetVisible"/>; with no handler the layout applies it itself.
    /// </summary>
    public event Action<string, bool>? VisibilityRequested;

    public ColumnSpec? Find(string key) => _byKey.GetValueOrDefault(key);

    /// <summary>Shown columns, left to right, the pinned ones last.</summary>
    public IReadOnlyList<ColumnSpec> Visible =>
        _visible ??= _order.Select(k => _byKey[k]).Where(s => !_hidden.Contains(s.Key)).ToArray();

    /// <summary>Every column, in the user's order (pinned ones last).</summary>
    public IReadOnlyList<ColumnSpec> Ordered => _order.Select(k => _byKey[k]).ToArray();

    public bool IsVisible(string key) => _byKey.ContainsKey(key) && !_hidden.Contains(key);

    /// <summary>The user's own width, or null while the column follows its default.</summary>
    public double? UserWidth(string key) => _widths.TryGetValue(key, out var w) ? w : null;

    public bool HasUserWidths => _widths.Count > 0;

    /// <summary>
    /// Space kept clear inside each side of a column (GitHub #157), so a text that fills its
    /// column never runs into the next column's: neighbours end up twice this apart. Header
    /// and rows inset alike (<see cref="CellInsets"/>), so labels stay over their cells.
    /// </summary>
    public double CellPadding { get; init; } = 6;

    /// <summary>
    /// Left and right inset of the cell of shown column <paramref name="visibleIndex"/>: none
    /// on the list's outer left edge (the first column keeps its place) and none for pinned
    /// columns (the 3-dots button keeps its own spacing).
    /// </summary>
    public (double Left, double Right) CellInsets(int visibleIndex)
    {
        var cols = Visible;
        if (visibleIndex < 0 || visibleIndex >= cols.Count || cols[visibleIndex].Pinned) return (0, 0);
        return (visibleIndex == 0 ? 0 : CellPadding, CellPadding);
    }

    public void SetVisible(string key, bool visible)
    {
        if (!_byKey.TryGetValue(key, out var spec) || spec.Pinned) return;
        if (!visible && !spec.CanHide) return;
        if (visible ? !_hidden.Remove(key) : !_hidden.Add(key)) return;
        OnChanged();
    }

    /// <summary>Header menu toggle: routed through <see cref="VisibilityRequested"/> when someone owns it.</summary>
    public void RequestVisibility(string key, bool visible)
    {
        if (VisibilityRequested != null) VisibilityRequested(key, visible);
        else SetVisible(key, visible);
        Commit();
    }

    /// <summary>Gives a column a width of its own (clamped to its minimum); it stops flexing.</summary>
    public void SetWidth(string key, double width)
    {
        if (!_byKey.TryGetValue(key, out var spec) || spec.Pinned) return;
        width = Math.Round(Math.Max(spec.MinWidth, width));
        if (_widths.TryGetValue(key, out var old) && old == width) return;
        _widths[key] = width;
        OnChanged();
    }

    /// <summary>Back to the automatic width (double-click on the divider).</summary>
    public void ResetWidth(string key)
    {
        if (_widths.Remove(key)) OnChanged();
    }

    /// <summary>
    /// Moves <paramref name="key"/> so it lands at <paramref name="visibleIndex"/> among the
    /// shown columns (0 = first). Pinned columns stay last whatever the index says.
    /// </summary>
    public void Move(string key, int visibleIndex)
    {
        if (!_byKey.TryGetValue(key, out var spec) || spec.Pinned) return;
        var visible = Visible.Where(s => !s.Pinned).Select(s => s.Key).ToList();
        var from = visible.IndexOf(key);
        if (from < 0) return;
        visibleIndex = Math.Clamp(visibleIndex, 0, visible.Count);
        // The index counts slots before the move; dropping just right of itself is no move.
        if (visibleIndex == from || visibleIndex == from + 1) return;

        visible.RemoveAt(from);
        if (visibleIndex > from) visibleIndex--;
        var anchor = visibleIndex < visible.Count ? visible[visibleIndex] : null;

        _order.Remove(key);
        var at = anchor != null ? _order.IndexOf(anchor) : LastMovableSlot();
        _order.Insert(at, key);
        OnChanged();
    }

    /// <summary>Order and widths back to the defaults; visibility stays as chosen.</summary>
    public void ResetLayout()
    {
        var changed = _widths.Count > 0 || !_order.SequenceEqual(DefaultOrder());
        _widths.Clear();
        _order.Clear();
        _order.AddRange(DefaultOrder());
        if (changed) OnChanged();
    }

    public void Commit() => Committed?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Widths and left edges of <see cref="Visible"/> for a row <paramref name="available"/>
    /// wide. Columns with a width (user or fixed default) take it; flexing columns share the
    /// rest by their stars, never under their minimum (the row then overflows and clips).
    /// Pinned columns sit at the right edge, so slack left by resized columns opens before
    /// them. Cached for the last width: every row asks with the same one.
    /// </summary>
    public (double[] Widths, double[] Offsets) Compute(double available)
    {
        if (available.Equals(_cacheWidth)) return (_cacheWidths, _cacheOffsets);

        var cols = Visible;
        var widths = new double[cols.Count];
        var flex = new List<int>();
        double used = 0;
        for (var i = 0; i < cols.Count; i++)
        {
            var c = cols[i];
            if (!c.Pinned && _widths.TryGetValue(c.Key, out var w)) widths[i] = w;
            else if (c.Star > 0 && !c.Pinned) { flex.Add(i); continue; }
            else widths[i] = c.Width;
            used += widths[i];
        }

        if (flex.Count > 0)
        {
            var avail = double.IsFinite(available) ? available : used + flex.Sum(i => Math.Max(cols[i].Width, cols[i].MinWidth));
            var remaining = Math.Max(0, avail - used);
            // Columns whose share falls under their minimum take the minimum and leave the
            // pool; repeat until every flexing column's share fits.
            var pool = new List<int>(flex);
            bool clamped;
            do
            {
                clamped = false;
                var stars = pool.Sum(i => cols[i].Star);
                foreach (var i in pool.ToArray())
                {
                    var share = remaining * cols[i].Star / stars;
                    if (share < cols[i].MinWidth)
                    {
                        widths[i] = cols[i].MinWidth;
                        remaining = Math.Max(0, remaining - cols[i].MinWidth);
                        pool.Remove(i);
                        clamped = true;
                    }
                }
            } while (clamped && pool.Count > 0);
            var poolStars = pool.Sum(i => cols[i].Star);
            foreach (var i in pool) widths[i] = remaining * cols[i].Star / poolStars;
        }

        var offsets = new double[cols.Count];
        double x = 0;
        for (var i = 0; i < cols.Count; i++)
        {
            if (cols[i].Pinned) break;
            offsets[i] = x;
            x += widths[i];
        }
        // Pinned columns: flush right when there is room, straight after the rest otherwise.
        var pinnedWidth = 0.0;
        for (var i = 0; i < cols.Count; i++) if (cols[i].Pinned) pinnedWidth += widths[i];
        var px = double.IsFinite(available) ? Math.Max(x, available - pinnedWidth) : x;
        for (var i = 0; i < cols.Count; i++)
        {
            if (!cols[i].Pinned) continue;
            offsets[i] = px;
            px += widths[i];
        }

        _cacheWidth = available;
        _cacheWidths = widths;
        _cacheOffsets = offsets;
        return (widths, offsets);
    }

    // ── Persistence: plain strings, so AppSettings needs no new types ──

    /// <summary>Column keys in the user's order, comma-separated; empty when it is the default.</summary>
    public string SerializeOrder()
        => _order.SequenceEqual(DefaultOrder()) ? string.Empty : string.Join(",", _order);

    /// <summary>"Key=width" pairs for the resized columns, comma-separated.</summary>
    public string SerializeWidths()
        => string.Join(",", _order.Where(_widths.ContainsKey)
            .Select(k => k + "=" + _widths[k].ToString("0", CultureInfo.InvariantCulture)));

    /// <summary>Keys of hidden columns (for lists whose visibility the layout owns itself).</summary>
    public string SerializeHidden()
        => string.Join(",", _order.Where(_hidden.Contains));

    /// <summary>
    /// Restores saved state. Unknown keys are dropped and columns the saved order lacks (new
    /// in this version) go back to their default place, so an old or hand-edited value never
    /// loses a column. A null argument leaves that part alone.
    /// </summary>
    public void Load(string? order, string? widths, string? hidden = null)
    {
        if (order != null)
        {
            var saved = Split(order).Where(k => _byKey.TryGetValue(k, out var s) && !s.Pinned).Distinct().ToList();
            _order.Clear();
            _order.AddRange(saved);
            var defaults = DefaultOrder();
            for (var d = 0; d < defaults.Count; d++)
            {
                var key = defaults[d];
                if (_order.Contains(key) || _byKey[key].Pinned) continue;
                // After the nearest earlier default neighbour that is already placed.
                var at = 0;
                for (var p = d - 1; p >= 0; p--)
                {
                    var idx = _order.IndexOf(defaults[p]);
                    if (idx >= 0) { at = idx + 1; break; }
                }
                _order.Insert(at, key);
            }
            _order.AddRange(defaults.Where(k => _byKey[k].Pinned));
        }

        if (widths != null)
        {
            _widths.Clear();
            foreach (var pair in Split(widths))
            {
                var eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var key = pair[..eq];
                if (!_byKey.TryGetValue(key, out var spec) || spec.Pinned) continue;
                if (double.TryParse(pair[(eq + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w))
                    _widths[key] = Math.Round(Math.Clamp(w, spec.MinWidth, 4000));
            }
        }

        if (hidden != null)
        {
            _hidden.Clear();
            foreach (var key in Split(hidden))
                if (_byKey.TryGetValue(key, out var spec) && spec.CanHide && !spec.Pinned) _hidden.Add(key);
        }

        OnChanged();
    }

    private static IEnumerable<string> Split(string value)
        => value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private List<string> DefaultOrder()
        => Specs.Where(s => !s.Pinned).Concat(Specs.Where(s => s.Pinned)).Select(s => s.Key).ToList();

    private int LastMovableSlot()
    {
        var i = _order.FindIndex(k => _byKey[k].Pinned);
        return i < 0 ? _order.Count : i;
    }

    private void OnChanged()
    {
        _visible = null;
        _cacheWidth = double.NaN;
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
