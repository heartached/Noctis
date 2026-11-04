namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>What a room stands for: one album (one pet) or one playlist (up to five song pets).</summary>
public enum RoomKind { Album, Playlist }

/// <summary>A rectangle on the building grid, in whole cells.</summary>
public readonly record struct Cells(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
    public int Area => W * H;

    public bool Overlaps(Cells o) => X < o.Right && o.X < Right && Y < o.Bottom && o.Y < Bottom;

    public bool Contains(int x, int y) => x >= X && x < Right && y >= Y && y < Bottom;
}

/// <summary>A rectangle in cell units (fractions allowed): a room's sub-room.</summary>
public readonly record struct CellRect(double X, double Y, double W, double H)
{
    public double Right => X + W;
    public double Bottom => Y + H;
}

/// <summary>
/// One apartment room: what it stands for (<see cref="Source"/> is the album's
/// <see cref="TownAlbum.Key"/> or the playlist's id), where it is and how big. A playlist room
/// can be divided into up to <see cref="RoomShapes.MaxSplits"/> rooms and has its chosen songs.
/// </summary>
public sealed class Room
{
    public Room(string id, RoomKind kind, string source, Cells cells)
    {
        Id = id;
        Kind = kind;
        Source = source;
        Cells = cells;
    }

    public string Id { get; }
    public RoomKind Kind { get; }
    public string Source { get; }
    public Cells Cells { get; internal set; }

    /// <summary>Rooms it is divided into (1 = one room). Album rooms are always 1.</summary>
    public int Splits { get; internal set; } = 1;

    /// <summary>The songs the user chose to live here (track ids, at most
    /// <see cref="PlaylistPicks.Max"/>). Empty = the playlist's most played.</summary>
    public List<string> Picks { get; } = new();
}

/// <summary>
/// Room sizes. Rooms started with Jafezy's six shapes (1, 2, 4 and 8 cells); since the Discord
/// feedback (11-02, "ditch the size limit") a room can be any whole number of cells wide and high
/// that fits in the building. <see cref="All"/> keeps the classic shapes.
/// </summary>
public static class RoomShapes
{
    public const int MaxSplits = 4;

    /// <summary>The first shapes rooms had: still the familiar sizes, no longer the only ones.</summary>
    public static readonly IReadOnlyList<(int W, int H)> All = new (int, int)[] { (1, 1), (2, 1), (1, 2), (2, 2), (4, 2), (2, 4) };

    /// <summary>Any size of at least one cell each way that the biggest building can hold.</summary>
    public static bool IsValid(int w, int h) => w >= 1 && h >= 1 && w <= ApartmentGrid.MaxColumns && h <= ApartmentGrid.MaxRows;

    /// <summary>The whole-cell size nearest to a dragged size of <paramref name="w"/> x
    /// <paramref name="h"/> cells (at least one cell each way).</summary>
    public static (int W, int H) Snap(double w, double h)
        => (Math.Clamp((int)Math.Round(w, MidpointRounding.AwayFromZero), 1, ApartmentGrid.MaxColumns),
            Math.Clamp((int)Math.Round(h, MidpointRounding.AwayFromZero), 1, ApartmentGrid.MaxRows));

    /// <summary>
    /// The sub-rooms of a room of <paramref name="w"/> x <paramref name="h"/> cells divided into
    /// <paramref name="splits"/>, relative to the room (cell units). 2: side by side (one above
    /// the other in a tall room); 3: one half whole, the other half split; 4: quarters.
    /// </summary>
    public static IReadOnlyList<CellRect> SubRooms(int w, int h, int splits)
    {
        splits = Math.Clamp(splits, 1, MaxSplits);
        double hw = w / 2.0, hh = h / 2.0;
        var wide = w >= h;
        return splits switch
        {
            1 => new[] { new CellRect(0, 0, w, h) },
            2 => wide
                ? new[] { new CellRect(0, 0, hw, h), new CellRect(hw, 0, hw, h) }
                : new[] { new CellRect(0, 0, w, hh), new CellRect(0, hh, w, hh) },
            3 => wide
                ? new[] { new CellRect(0, 0, hw, h), new CellRect(hw, 0, hw, hh), new CellRect(hw, hh, hw, hh) }
                : new[] { new CellRect(0, 0, w, hh), new CellRect(0, hh, hw, hh), new CellRect(hw, hh, hw, hh) },
            _ => new[] { new CellRect(0, 0, hw, hh), new CellRect(hw, 0, hw, hh), new CellRect(0, hh, hw, hh), new CellRect(hw, hh, hw, hh) },
        };
    }
}

/// <summary>
/// The building: a fixed grid of cells ("the void") holding rooms that snap to it and never
/// overlap. All changes go through here, so every rule (bounds, sizes, overlap, one room per
/// album or playlist) holds after any sequence of adds, moves, resizes and removals.
/// </summary>
public sealed class ApartmentGrid
{
    public const int DefaultColumns = 14;
    public const int DefaultRows = 7;
    /// <summary>The building's size limits (the sliders' ends; Discord, Jafezy 11-02: up to 18 x 9).</summary>
    public const int MinColumns = 4;
    public const int MinRows = 2;
    public const int MaxColumns = 18;
    public const int MaxRows = 9;

    private readonly List<Room> _rooms = new();

    public ApartmentGrid(int columns = DefaultColumns, int rows = DefaultRows)
    {
        Columns = Math.Clamp(columns, MinColumns, MaxColumns);
        Rows = Math.Clamp(rows, MinRows, MaxRows);
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }

    /// <summary>The smallest the building can be without leaving a room outside.</summary>
    public (int Columns, int Rows) MinSize()
    {
        int c = MinColumns, r = MinRows;
        foreach (var room in _rooms)
        {
            c = Math.Max(c, room.Cells.Right);
            r = Math.Max(r, room.Cells.Bottom);
        }
        return (c, r);
    }

    /// <summary>Resizes the building (clamped to its limits). False (and unchanged) when a room
    /// would end up outside.</summary>
    public bool TrySetSize(int columns, int rows)
    {
        columns = Math.Clamp(columns, MinColumns, MaxColumns);
        rows = Math.Clamp(rows, MinRows, MaxRows);
        if (columns == Columns && rows == Rows) return true;
        var (minC, minR) = MinSize();
        if (columns < minC || rows < minR) return false;
        Columns = columns;
        Rows = rows;
        Touch();
        return true;
    }

    /// <summary>Empties the building ("Clear building"); its size stays.</summary>
    public void Clear()
    {
        if (_rooms.Count == 0) return;
        _rooms.Clear();
        Touch();
    }
    public IReadOnlyList<Room> Rooms => _rooms;

    /// <summary>Bumped on every change (the canvas and the save timer watch it).</summary>
    public int Version { get; private set; }

    /// <summary>Raised after every change.</summary>
    public event EventHandler? Changed;

    // Plain loops: these run while drawing (the shelf, pruning), where a lambda would allocate.
    public Room? Find(string id)
    {
        foreach (var r in _rooms)
            if (string.Equals(r.Id, id, StringComparison.Ordinal)) return r;
        return null;
    }

    public Room? FindSource(RoomKind kind, string source)
    {
        foreach (var r in _rooms)
            if (r.Kind == kind && string.Equals(r.Source, source, StringComparison.Ordinal)) return r;
        return null;
    }

    /// <summary>The room covering cell (<paramref name="x"/>, <paramref name="y"/>), or null.</summary>
    public Room? RoomAt(int x, int y)
    {
        foreach (var r in _rooms)
            if (r.Cells.Contains(x, y)) return r;
        return null;
    }

    public bool InBounds(Cells c) => c.X >= 0 && c.Y >= 0 && c.Right <= Columns && c.Bottom <= Rows;

    /// <summary>True when <paramref name="c"/> is an allowed size, inside the grid and free
    /// (<paramref name="ignore"/> is the room being moved or resized).</summary>
    public bool CanPlace(Cells c, Room? ignore = null)
    {
        if (!RoomShapes.IsValid(c.W, c.H) || !InBounds(c)) return false;
        foreach (var r in _rooms)
            if (!ReferenceEquals(r, ignore) && r.Cells.Overlaps(c)) return false;
        return true;
    }

    /// <summary>
    /// A new 1x1 room for <paramref name="source"/> at (<paramref name="x"/>, <paramref name="y"/>).
    /// If that album or playlist already has a room, that room moves there instead (one room each).
    /// Null when the cell is taken or outside.
    /// </summary>
    public Room? TryAdd(RoomKind kind, string source, int x, int y, string? id = null)
    {
        if (FindSource(kind, source) is { } existing)
            return TryMove(existing, x, y) ? existing : null;
        var cells = new Cells(x, y, 1, 1);
        if (!CanPlace(cells)) return null;
        var room = new Room(id ?? Guid.NewGuid().ToString("N"), kind, source, cells);
        _rooms.Add(room);
        Touch();
        return room;
    }

    /// <summary>The first free cell, reading order (for adding without a drag), or null when full.</summary>
    public (int X, int Y)? FirstFree()
    {
        for (var y = 0; y < Rows; y++)
            for (var x = 0; x < Columns; x++)
                if (CanPlace(new Cells(x, y, 1, 1))) return (x, y);
        return null;
    }

    /// <summary>Moves <paramref name="room"/> so its top-left is (<paramref name="x"/>, <paramref name="y"/>); false (and
    /// unchanged) when it would leave the grid or overlap another room.</summary>
    public bool TryMove(Room room, int x, int y)
    {
        var to = room.Cells with { X = x, Y = y };
        if (to == room.Cells) return true;
        if (!_rooms.Contains(room) || !CanPlace(to, room)) return false;
        room.Cells = to;
        Touch();
        return true;
    }

    /// <summary>The size a drag of a room's edge or corner to <paramref name="w"/> x
    /// <paramref name="h"/> cells snaps to (top-left stays, never past the building's edge), and
    /// whether it fits.</summary>
    public (Cells Cells, bool Fits) ResizeTarget(Room room, double w, double h)
    {
        var (sw, sh) = RoomShapes.Snap(w, h);
        var to = room.Cells with { W = Math.Min(sw, Math.Max(1, Columns - room.Cells.X)), H = Math.Min(sh, Math.Max(1, Rows - room.Cells.Y)) };
        return (to, CanPlace(to, room));
    }

    /// <summary>Resizes (top-left stays); false (and unchanged) when it does not fit.</summary>
    public bool TryResize(Room room, int w, int h)
    {
        var to = room.Cells with { W = w, H = h };
        if (to == room.Cells) return true;
        if (!_rooms.Contains(room) || !CanPlace(to, room)) return false;
        room.Cells = to;
        Touch();
        return true;
    }

    /// <summary>Divides a playlist room into 1 to <see cref="RoomShapes.MaxSplits"/> rooms. Album rooms stay whole.</summary>
    public bool TrySplit(Room room, int splits)
    {
        if (room.Kind != RoomKind.Playlist || splits < 1 || splits > RoomShapes.MaxSplits || !_rooms.Contains(room)) return false;
        if (room.Splits == splits) return true;
        room.Splits = splits;
        Touch();
        return true;
    }

    /// <summary>Sets a playlist room's chosen songs (at most <see cref="PlaylistPicks.Max"/>, no repeats).</summary>
    public void SetPicks(Room room, IEnumerable<string> picks)
    {
        if (room.Kind != RoomKind.Playlist) return;
        var next = PlaylistPicks.Clean(picks);
        if (next.SequenceEqual(room.Picks, StringComparer.Ordinal)) return;
        room.Picks.Clear();
        room.Picks.AddRange(next);
        Touch();
    }

    public bool Remove(Room room)
    {
        if (!_rooms.Remove(room)) return false;
        Touch();
        return true;
    }

    private void Touch()
    {
        Version++;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ── Saving ──

    public ApartmentLayout ToLayout() => new()
    {
        Columns = Columns,
        Rows = Rows,
        Rooms = _rooms.Select(r => new SavedRoom
        {
            Id = r.Id,
            Kind = r.Kind == RoomKind.Playlist ? "playlist" : "album",
            Source = r.Source,
            X = r.Cells.X, Y = r.Cells.Y, W = r.Cells.W, H = r.Cells.H,
            Splits = r.Splits,
            Picks = r.Kind == RoomKind.Playlist && r.Picks.Count > 0 ? r.Picks.ToList() : null,
        }).ToList(),
    };

    /// <summary>
    /// The building saved in <paramref name="layout"/>. Rooms whose album or playlist is gone
    /// (<paramref name="exists"/> false), with a size that is not allowed, outside the grid,
    /// overlapping an earlier room or repeating a source are left out (the rest keep their spots).
    /// </summary>
    public static ApartmentGrid FromLayout(ApartmentLayout? layout, Func<RoomKind, string, bool> exists, out int dropped)
    {
        dropped = 0;
        var grid = layout is null ? new ApartmentGrid() : new ApartmentGrid(layout.Columns, layout.Rows);
        if (layout?.Rooms is null) return grid;
        foreach (var s in layout.Rooms)
        {
            if (s is null || string.IsNullOrEmpty(s.Source)) { dropped++; continue; }
            var kind = string.Equals(s.Kind, "playlist", StringComparison.OrdinalIgnoreCase) ? RoomKind.Playlist : RoomKind.Album;
            var cells = new Cells(s.X, s.Y, s.W, s.H);
            if (!exists(kind, s.Source) || grid.FindSource(kind, s.Source) is not null || !grid.CanPlace(cells)) { dropped++; continue; }
            var room = new Room(string.IsNullOrEmpty(s.Id) || grid.Find(s.Id) is not null ? Guid.NewGuid().ToString("N") : s.Id, kind, s.Source, cells);
            if (kind == RoomKind.Playlist)
            {
                room.Splits = Math.Clamp(s.Splits, 1, RoomShapes.MaxSplits);
                if (s.Picks is not null) room.Picks.AddRange(PlaylistPicks.Clean(s.Picks));
            }
            grid._rooms.Add(room);
        }
        return grid;
    }
}

/// <summary>The building as saved in apartments.json (plain data for System.Text.Json).</summary>
public sealed class ApartmentLayout
{
    public int Version { get; set; } = 1;
    public int Columns { get; set; } = ApartmentGrid.DefaultColumns;
    public int Rows { get; set; } = ApartmentGrid.DefaultRows;
    public List<SavedRoom> Rooms { get; set; } = new();

    private static readonly System.Text.Json.JsonSerializerOptions Options = new() { WriteIndented = true };

    public string ToJson() => System.Text.Json.JsonSerializer.Serialize(this, Options);

    /// <summary>The layout in <paramref name="json"/>, or null when it is not one.</summary>
    public static ApartmentLayout? FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<ApartmentLayout>(json); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}

public sealed class SavedRoom
{
    public string Id { get; set; } = "";
    public string Kind { get; set; } = "album";
    public string Source { get; set; } = "";
    public int X { get; set; }
    public int Y { get; set; }
    public int W { get; set; } = 1;
    public int H { get; set; } = 1;
    public int Splits { get; set; } = 1;
    public List<string>? Picks { get; set; }
}

/// <summary>Which songs of a playlist live in its room: at most <see cref="Max"/>, by default the most played.</summary>
public static class PlaylistPicks
{
    public const int Max = 5;

    /// <summary>No blanks, no repeats, at most <see cref="Max"/>.</summary>
    public static List<string> Clean(IEnumerable<string> picks)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var list = new List<string>(Max);
        foreach (var p in picks)
        {
            if (string.IsNullOrEmpty(p) || !seen.Add(p)) continue;
            list.Add(p);
            if (list.Count == Max) break;
        }
        return list;
    }

    /// <summary>The playlist's <see cref="Max"/> most played songs (ties: playlist order).</summary>
    public static IReadOnlyList<string> Default(IReadOnlyList<string> playlistTracks, Func<string, int> plays)
        => playlistTracks.Distinct(StringComparer.Ordinal)
            .Select((id, i) => (id, i, plays: plays(id)))
            .Where(t => t.plays >= 0)
            .OrderByDescending(t => t.plays).ThenBy(t => t.i)
            .Take(Max).Select(t => t.id).ToList();

    /// <summary>
    /// The songs living in the room: the user's picks still in the playlist (and the library),
    /// or, when none are left, the default. <paramref name="plays"/> returns -1 for a song the
    /// library no longer has.
    /// </summary>
    public static IReadOnlyList<string> Resolve(Room room, IReadOnlyList<string> playlistTracks, Func<string, int> plays)
    {
        if (room.Picks.Count > 0)
        {
            var inList = playlistTracks.ToHashSet(StringComparer.Ordinal);
            var kept = room.Picks.Where(p => inList.Contains(p) && plays(p) >= 0).Take(Max).ToList();
            if (kept.Count > 0) return kept;
        }
        return Default(playlistTracks, plays);
    }

    /// <summary>Adds or removes <paramref name="id"/>; false when adding would pass <see cref="Max"/>.</summary>
    public static bool Toggle(List<string> picks, string id)
    {
        if (picks.Remove(id)) return true;
        if (picks.Count >= Max) return false;
        picks.Add(id);
        return true;
    }
}
