namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>One walkable room: a whole room, or one of a divided playlist room's rooms. Cell units.</summary>
public sealed record Area(int Index, Room Room, int Part, CellRect Rect)
{
    /// <summary>The floor pets stand on (the area's bottom edge).</summary>
    public double Floor => Rect.Bottom;
}

/// <summary>
/// How pets get from one area to another.
/// <para><see cref="Door"/>: a doorway in a wall two areas share, at the floor both stand on.
/// Between two rooms that touch it is the door Jafezy asked for; inside a divided playlist room
/// it is the doorway in an interior wall.</para>
/// <para><see cref="Ladder"/>: the opening in a divided room's interior floor.</para>
/// <para><see cref="TrapDoor"/>: two rooms stacked on each other (one's floor is the other's
/// ceiling): a hatch in the upper floor and a ladder down into the room below.</para>
/// <para><see cref="Stairs"/>: two rooms side by side whose floors are at different heights: a
/// ladder up the lower room's wall to a doorway onto the higher floor.</para>
/// Since the Discord feedback (Jafezy 11-02: "albums with 1 room have nowhere to go") pets use
/// all four, so a one-cell room above, below or beside another is no longer a cell they never leave.
/// </summary>
public enum PortalKind { Door, Ladder, TrapDoor, Stairs }

/// <summary>
/// A way between <see cref="From"/> and <see cref="To"/>: a pet in <see cref="From"/> walks to
/// <see cref="FromPoint"/> (feet, cell units) and comes out at <see cref="ToPoint"/> in <see cref="To"/>.
/// Every portal is listed both ways. <see cref="Line"/> is the shared wall (x for a door, y for a
/// ladder or trap door), <see cref="SpanFrom"/>..<see cref="SpanTo"/> its shared stretch.
/// </summary>
public sealed record Portal(PortalKind Kind, Area From, Area To, double Line, double SpanFrom, double SpanTo, (double X, double Y) FromPoint, (double X, double Y) ToPoint)
{
    /// <summary>True between two different rooms (a visit), false inside one divided room.</summary>
    public bool BetweenRooms => !ReferenceEquals(From.Room, To.Room);

    /// <summary>The way a pet walks (feet, cell units): <see cref="FromPoint"/>, any steps on the
    /// way (up or down a ladder), <see cref="ToPoint"/>. Null: straight from one to the other.</summary>
    public IReadOnlyList<(double X, double Y)>? Via { get; init; }

    /// <summary>The point of <see cref="Path"/> where the pet is in <see cref="To"/> (1 = after the first step).</summary>
    public int CrossAt { get; init; } = 1;

    /// <summary>Every point the pet walks to, in order (at least <see cref="FromPoint"/> and <see cref="ToPoint"/>).</summary>
    public IReadOnlyList<(double X, double Y)> Path => Via ?? (_path ??= new[] { FromPoint, ToPoint });
    private (double X, double Y)[]? _path;
}

/// <summary>
/// The walkable layout of a building: every area (room or part of a divided room) and the
/// doors, ladders and (for later) trap doors between them. Rebuilt whenever the building changes;
/// pure data, no drawing.
/// </summary>
public sealed class ApartmentMap
{
    /// <summary>How far from a wall a pet stands to use a door (cell units).</summary>
    public const double DoorStep = 0.16;
    private const double Eps = 1e-6;

    /// <summary>Trap doors (rooms stacked on each other) are used: pets climb between them.</summary>
    public const bool UseTrapDoors = true;
    /// <summary>The least headroom (cells) a doorway at the top of stairs needs.</summary>
    public const double StairsHeadroom = 0.3;

    private readonly List<Area> _areas = new();
    private readonly List<Portal> _portals = new();
    private readonly Dictionary<Room, List<Area>> _byRoom = new();
    private readonly Dictionary<Area, List<Portal>> _from = new();

    public IReadOnlyList<Area> Areas => _areas;
    public IReadOnlyList<Portal> Portals => _portals;

    public IReadOnlyList<Area> AreasOf(Room room) => _byRoom.TryGetValue(room, out var list) ? list : Array.Empty<Area>();

    /// <summary>Portals leading out of <paramref name="area"/> that pets use.</summary>
    public IReadOnlyList<Portal> Exits(Area area) => _from.TryGetValue(area, out var list) ? list : Array.Empty<Portal>();

    public static ApartmentMap Build(IReadOnlyList<Room> rooms)
    {
        var map = new ApartmentMap();
        foreach (var room in rooms)
        {
            var list = new List<Area>();
            var parts = RoomShapes.SubRooms(room.Cells.W, room.Cells.H, room.Kind == RoomKind.Playlist ? room.Splits : 1);
            for (var i = 0; i < parts.Count; i++)
            {
                var p = parts[i];
                var area = new Area(map._areas.Count, room, i, new CellRect(room.Cells.X + p.X, room.Cells.Y + p.Y, p.W, p.H));
                map._areas.Add(area);
                list.Add(area);
                map._from[area] = new List<Portal>();
            }
            map._byRoom[room] = list;
        }

        for (var i = 0; i < map._areas.Count; i++)
            for (var j = 0; j < map._areas.Count; j++)
            {
                if (i == j) continue;
                var a = map._areas[i];
                var b = map._areas[j];
                // A door: a's right wall is b's left wall, both stand on the same floor, and the wall
                // they share reaches that floor.
                if (Near(a.Rect.Right, b.Rect.X) && Near(a.Floor, b.Floor))
                {
                    var top = Math.Max(a.Rect.Y, b.Rect.Y);
                    if (a.Floor - top > Eps) map.AddBothWays(PortalKind.Door, a, b, a.Rect.Right, top, a.Floor,
                        (a.Rect.Right - DoorStep, a.Floor), (b.Rect.X + DoorStep, b.Floor));
                }
                // Stairs: side by side, floors at different heights, and the higher floor meets the
                // lower room's wall with room above it to step through.
                else if (Near(a.Rect.Right, b.Rect.X) && !Near(a.Floor, b.Floor))
                    map.AddStairs(a, b);
                // Stacked: a above b. Inside one room a ladder, between rooms a trap door.
                if (Near(a.Floor, b.Rect.Y))
                {
                    var left = Math.Max(a.Rect.X, b.Rect.X);
                    var right = Math.Min(a.Rect.Right, b.Rect.Right);
                    if (right - left > Eps)
                    {
                        var x = (left + right) / 2;
                        var kind = ReferenceEquals(a.Room, b.Room) ? PortalKind.Ladder : PortalKind.TrapDoor;
                        map.AddBothWays(kind, a, b, a.Floor, left, right, (x, a.Floor), (x, b.Floor));
                    }
                }
            }
        return map;
    }

    /// <summary>Stairs between <paramref name="left"/> and <paramref name="right"/> (side by side, floors apart):
    /// on the lower floor's side a pet climbs the wall to the higher floor, then steps through.</summary>
    private void AddStairs(Area left, Area right)
    {
        var (high, low) = left.Floor < right.Floor ? (left, right) : (right, left);
        var wall = left.Rect.Right;
        var top = Math.Max(left.Rect.Y, right.Rect.Y);
        // The higher floor must reach into the lower room's wall, with headroom above it.
        if (high.Floor <= low.Rect.Y + Eps || high.Floor - top < StairsHeadroom) return;
        var highX = ReferenceEquals(high, left) ? wall - DoorStep : wall + DoorStep;
        var lowX = ReferenceEquals(low, left) ? wall - DoorStep : wall + DoorStep;
        var up = new[] { (lowX, low.Floor), (lowX, high.Floor), (highX, high.Floor) };
        var down = new[] { (highX, high.Floor), (lowX, high.Floor), (lowX, low.Floor) };
        Add(new Portal(PortalKind.Stairs, low, high, wall, top, high.Floor, up[0], up[2]) { Via = up, CrossAt = 2 });
        Add(new Portal(PortalKind.Stairs, high, low, wall, top, high.Floor, down[0], down[2]) { Via = down, CrossAt = 1 });

        void Add(Portal p)
        {
            _portals.Add(p);
            _from[p.From].Add(p);
        }
    }

    private void AddBothWays(PortalKind kind, Area a, Area b, double line, double from, double to, (double, double) pa, (double, double) pb)
    {
        var there = new Portal(kind, a, b, line, from, to, pa, pb);
        var back = new Portal(kind, b, a, line, from, to, pb, pa);
        _portals.Add(there);
        _portals.Add(back);
        if (kind != PortalKind.TrapDoor || UseTrapDoors)
        {
            _from[a].Add(there);
            _from[b].Add(back);
        }
    }

    /// <summary>Doors between two different rooms (one per pair of areas, left to right), for drawing.</summary>
    public IEnumerable<Portal> RoomDoors() => _portals.Where(p => p.Kind == PortalKind.Door && p.BetweenRooms && p.From.Rect.X < p.To.Rect.X);

    private static bool Near(double a, double b) => Math.Abs(a - b) < Eps;
}
