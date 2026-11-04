using System.Numerics;

namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>
/// A pet living in an apartment: the album's one creature in an album room, or one of a
/// playlist's songs. Positions are in grid cells (feet), so the building can be drawn at any
/// size; the canvas copies them into <see cref="Creature.Position"/> in pixels to draw.
/// </summary>
public sealed class Pet
{
    public Pet(Creature creature, Room home, bool isSong, string residentKey, string? trackId)
    {
        Creature = creature;
        Home = home;
        IsSong = isSong;
        ResidentKey = residentKey;
        TrackId = trackId;
    }

    public Creature Creature { get; }
    public TownAlbum Album => Creature.Album;
    public Room Home { get; internal set; }
    /// <summary>A playlist song (smaller) rather than an album.</summary>
    public bool IsSong { get; }
    /// <summary>The album resident it stands for (its cover; "now playing").</summary>
    public string ResidentKey { get; }
    /// <summary>The song, for song pets.</summary>
    public string? TrackId { get; }

    public Area Area { get; internal set; } = null!;
    public double X;
    public double Y;
    public double TargetX;
    public double TargetY;
    /// <summary>The door, ladder or stairs it is walking to (or along), or null.</summary>
    public Portal? Goal { get; internal set; }
    /// <summary>Which point of <see cref="Goal"/>'s path it walks to now.</summary>
    public int Leg { get; internal set; }
    /// <summary>On a ladder (moving up or down a wall or through a floor).</summary>
    public bool Climbing => Goal is { } g && Leg > 0 && Leg < g.Path.Count && Math.Abs(g.Path[Leg].X - g.Path[Leg - 1].X) < 1e-6;
    /// <summary>While visiting a neighbour: the door home.</summary>
    public Portal? Return { get; internal set; }
    public float VisitTimer;

    /// <summary>Visiting another room (through a door).</summary>
    public bool Visiting => !ReferenceEquals(Area.Room, Home);

    /// <summary>Body width in cells.</summary>
    public double Size => IsSong ? ApartmentLife.SongPetSize : ApartmentLife.AlbumPetSize;
}

/// <summary>Who should live where: one entry per pet (the view builds it from rooms, albums and picks).</summary>
public readonly record struct PetPlan(Room Room, TownAlbum Album, bool IsSong, string ResidentKey, string? TrackId);

/// <summary>
/// Life in the apartments: pets wander their room, wander between the rooms of a divided playlist
/// apartment (doorways and ladders), and now and then walk through a door into a neighbouring
/// room, say hello to whoever lives there (the plaza's talk lines), then go home. A click makes a
/// pet wave, the album playing dances. No drawing and no Avalonia; no allocations per step
/// (a new chat builds its two lines, like the plaza).
/// </summary>
public sealed class ApartmentLife
{
    /// <summary>Album pets' body width in cells (one per room, so they get to be big).</summary>
    public const double AlbumPetSize = 0.42;
    /// <summary>Song pets' body width in cells (up to five share a room).</summary>
    public const double SongPetSize = 0.28;
    private const double Wall = 0.06;
    private const float LineSeconds = 2.6f;

    private readonly Random _random;
    private readonly List<Pet> _pets = new();
    private readonly Dictionary<string, Pet> _byKey = new(StringComparer.Ordinal);

    public ApartmentLife(Random random) => _random = random;

    public ApartmentMap Map { get; private set; } = ApartmentMap.Build(Array.Empty<Room>());
    public IReadOnlyList<Pet> Pets => _pets;
    public bool Chatty { get; set; } = true;
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    /// <summary>A lyric line of the album's (or song's) songs to quote, or null.</summary>
    public Func<TownAlbum, string?>? Lyric { get; set; }
    /// <summary>The pet under the pointer: it holds still so it is easy to click.</summary>
    public Pet? Held { get; set; }
    /// <summary>How often a resting pet sets off for a door or a ladder (0..1 per rest).</summary>
    public double Wanderlust { get; set; } = 0.3;

    /// <summary>
    /// New building or new residents. Pets that stay keep their state and spot (moved with their
    /// room); newcomers appear in their room's areas in turn; pets whose room is gone leave.
    /// </summary>
    public void SetLayout(ApartmentMap map, IReadOnlyList<PetPlan> plan)
    {
        var old = new Dictionary<string, Pet>(_byKey, StringComparer.Ordinal);
        Map = map;
        _pets.Clear();
        _byKey.Clear();
        var perRoom = new Dictionary<Room, int>();
        foreach (var entry in plan)
        {
            var areas = map.AreasOf(entry.Room);
            if (areas.Count == 0) continue;
            var n = perRoom.GetValueOrDefault(entry.Room);
            perRoom[entry.Room] = n + 1;
            var key = entry.Room.Id + "|" + entry.Album.Key;
            var home = areas[n % areas.Count];
            if (old.TryGetValue(key, out var pet) && pet.Album == entry.Album)
            {
                // The old area still has the old rectangle (rooms change in place, areas are rebuilt).
                var was = pet.Area;
                pet.Home = entry.Room;
                // At home: the same part of the room. Visiting: back home (the doors may be gone).
                var area = ReferenceEquals(was.Room, entry.Room) && was.Part < areas.Count ? areas[was.Part] : home;
                // Keep the spot relative to the room (it moved or grew with it).
                var fx = was.Rect.W > 0 ? (pet.X - was.Rect.X) / was.Rect.W : 0.5;
                Place(pet, area, area.Rect.X + fx * area.Rect.W, area.Floor - (was.Floor - pet.Y));
                pet.Goal = null;
                pet.Return = null;
                if (pet.Creature.State is CreatureState.Wander) ToIdle(pet);
            }
            else
            {
                pet = new Pet(new Creature(new CastMember(entry.Album, CastReason.Random), Vector2.Zero), entry.Room, entry.IsSong, entry.ResidentKey, entry.TrackId);
                Prime(pet);
                var (x, y) = RandomSpot(pet, home);
                Place(pet, home, x, y);
            }
            _pets.Add(pet);
            _byKey[key] = pet;
        }
        foreach (var p in _pets)
            if (p.Creature.Partner is { } partner && Owner(partner) is null) EndTalk(p);
        if (Held is not null && !_pets.Contains(Held)) Held = null;
    }

    private Pet? Owner(Creature c)
    {
        foreach (var p in _pets)
            if (ReferenceEquals(p.Creature, c)) return p;
        return null;
    }

    private void Prime(Pet p)
    {
        var c = p.Creature;
        c.StateTimer = (float)(_random.NextDouble() * 2.5);
        c.BlinkTimer = 1 + (float)(_random.NextDouble() * 4);
        c.TalkCooldown = 3 + (float)(_random.NextDouble() * 6);
        c.Speed = 0.28f + (float)(_random.NextDouble() * 0.12); // cells per second
        c.WalkPhase = (float)(_random.NextDouble() * Math.PI * 2);
        c.Facing = _random.Next(2) == 0 ? -1 : 1;
    }

    /// <summary>Puts <paramref name="p"/> in <paramref name="area"/> at the nearest spot it may stand on.</summary>
    private void Place(Pet p, Area area, double x, double y)
    {
        p.Area = area;
        (p.X, p.Y) = Clamp(p, area, x, y);
        p.TargetX = p.X;
        p.TargetY = p.Y;
    }

    /// <summary>Where a pet may stand in an area: clear of the walls, on a thin strip of floor.</summary>
    public static (double MinX, double MaxX, double MinY, double MaxY) Room(Pet p, Area a)
    {
        var margin = p.Size * 0.62 + Wall;
        var minX = a.Rect.X + margin;
        var maxX = a.Rect.Right - margin;
        if (minX > maxX) minX = maxX = a.Rect.X + a.Rect.W / 2;
        var maxY = a.Floor - 0.05;
        var minY = maxY - Math.Min(0.14, a.Rect.H * 0.12);
        return (minX, maxX, minY, maxY);
    }

    private static (double, double) Clamp(Pet p, Area a, double x, double y)
    {
        var (minX, maxX, minY, maxY) = Room(p, a);
        return (Math.Clamp(x, minX, maxX), Math.Clamp(y, minY, maxY));
    }

    private (double, double) RandomSpot(Pet p, Area a)
    {
        var (minX, maxX, minY, maxY) = Room(p, a);
        return (minX + _random.NextDouble() * (maxX - minX), minY + _random.NextDouble() * (maxY - minY));
    }

    /// <summary>Marks the pets of the album (or the song) playing; null keys = none.</summary>
    public void SetNowPlaying(string? residentKey, string? trackId, bool playing)
    {
        foreach (var p in _pets)
        {
            var c = p.Creature;
            var now = playing && (p.IsSong ? trackId is not null && p.TrackId == trackId : residentKey is not null && p.ResidentKey == residentKey);
            if (now == c.IsPlaying) continue;
            c.IsPlaying = now;
            if (now)
            {
                EndTalk(p);
                p.Goal = null;
                c.State = CreatureState.Dance;
                c.Bubble = null;
            }
            else if (c.State == CreatureState.Dance) ToIdle(p);
        }
    }

    /// <summary>Click: the pet stops, waves and says hello.</summary>
    public void Wave(Pet p)
    {
        EndTalk(p);
        var c = p.Creature;
        c.State = CreatureState.Wave;
        c.StateTimer = 1.8f;
        c.Bubble = TalkLines.Hello(c.Album, Now, _random, Lyric);
        c.Gaze = new Vector2(0, 1);
    }

    /// <summary>The pet whose body is under (<paramref name="x"/>, <paramref name="y"/>) in cells, front-most first.</summary>
    public Pet? HitTest(double x, double y, Pet? prefer = null)
    {
        if (prefer is not null && _pets.Contains(prefer) && InTarget(prefer, x, y)) return prefer;
        Pet? best = null;
        foreach (var p in _pets)
            if (InTarget(p, x, y) && (best is null || p.Y > best.Y)) best = p;
        return best;
    }

    private static bool InTarget(Pet p, double x, double y)
    {
        var s = p.Size;
        var dx = x - p.X;
        var dy = y - (p.Y - s * 0.8);
        return Math.Abs(dx) <= s * 0.75 && dy >= -s * 0.75 && dy <= s * 0.9;
    }

    /// <summary>Advances life by <paramref name="dt"/> seconds (clamped to 0.1 s); <paramref name="pulse"/> is the beat.</summary>
    public void Step(float dt, float pulse = 0)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        for (var i = 0; i < _pets.Count; i++)
        {
            var p = _pets[i];
            var c = p.Creature;
            c.Age += dt;
            c.TalkCooldown -= dt;
            c.BlinkTimer -= dt;
            if (p.Visiting) p.VisitTimer -= dt;
            if (c.BlinkTimer < -0.12f) c.BlinkTimer = 1.5f + (float)(_random.NextDouble() * 4);
            if (ReferenceEquals(p, Held) && c.State is CreatureState.Idle or CreatureState.Wander)
            {
                c.State = CreatureState.Idle;
                c.StateTimer = Math.Max(c.StateTimer, 0.5f);
            }
            switch (c.State)
            {
                case CreatureState.Idle:
                case CreatureState.LookAtCursor:
                    StepIdle(p, dt);
                    break;
                case CreatureState.Wander: StepWalk(p, dt); break;
                case CreatureState.Wave: StepTimed(p, dt); break;
                case CreatureState.Talk: StepTalk(p, dt); break;
                case CreatureState.Dance:
                    c.Pulse = pulse;
                    c.WalkPhase += dt * (4f + 6f * pulse);
                    c.Gaze = new Vector2(0, 1);
                    break;
            }
            if (c.State != CreatureState.Wander) c.Stride = Approach(c.Stride, 0, dt * 4);
        }
        Separate(dt);
    }

    private void StepIdle(Pet p, float dt)
    {
        var c = p.Creature;
        c.StateTimer -= dt;
        if (c.StateTimer > 0 || ReferenceEquals(p, Held)) return;
        if (p.Visiting && p.VisitTimer <= 0 && p.Return is { } home)
        {
            Head(p, home);
            return;
        }
        // A visitor greets whoever lives there (they need not be in the mood).
        if (Chatty && c.TalkCooldown <= 0 && TryStartTalk(p, ignoreCooldown: p.Visiting)) return;
        if (!p.Visiting && _random.NextDouble() < Wanderlust)
        {
            var exits = Map.Exits(p.Area);
            if (exits.Count > 0)
            {
                var exit = exits[_random.Next(exits.Count)];
                // Album pets only leave for a visit; song pets roam their own apartment too.
                Head(p, exit);
                return;
            }
        }
        var (x, y) = RandomSpot(p, p.Area);
        p.TargetX = x;
        p.TargetY = y;
        p.Goal = null;
        c.State = CreatureState.Wander;
    }

    private static void Head(Pet p, Portal portal)
    {
        p.Goal = portal;
        p.Leg = 0;
        p.TargetX = portal.FromPoint.X;
        p.TargetY = portal.FromPoint.Y - 0.05;
        p.Creature.State = CreatureState.Wander;
    }

    private void StepWalk(Pet p, float dt)
    {
        var c = p.Creature;
        var dx = p.TargetX - p.X;
        var dy = p.TargetY - p.Y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        if (dist < 0.01)
        {
            if (p.Goal is { } goal) NextLeg(p, goal);
            else ToIdle(p);
            return;
        }
        // Up and down ladders a little slower than walking.
        var step = Math.Min(dist, c.Speed * dt * (p.Climbing ? 0.7f : 1f));
        p.X += dx / dist * step;
        p.Y += dy / dist * step;
        if (Math.Abs(dx) / dist > 0.15) c.Facing = Math.Sign(dx);
        c.Gaze = Vector2.Lerp(c.Gaze, new Vector2((float)(dx / dist), (float)(dy / dist)), Math.Min(1, dt * 6));
        c.Stride = Approach(c.Stride, 1, dt * 4);
        c.WalkPhase += dt * 9.5f;
    }

    /// <summary>Reached a point of the way through a door, ladder or stairs: on to the next one,
    /// into the other area at <see cref="Portal.CrossAt"/>, a few steps in at the end.</summary>
    private void NextLeg(Pet p, Portal portal)
    {
        var path = portal.Path;
        p.Leg++;
        if (p.Leg == portal.CrossAt) Enter(p, portal);
        if (p.Leg < path.Count)
        {
            p.TargetX = path[p.Leg].X;
            p.TargetY = path[p.Leg].Y - 0.05;
            return;
        }
        Arrive(p, portal);
    }

    /// <summary>Through the door (or the floor, or onto the other floor): now in the other area.</summary>
    private void Enter(Pet p, Portal portal)
    {
        var from = p.Area;
        p.Area = portal.To;
        if (portal.BetweenRooms)
        {
            if (ReferenceEquals(portal.To.Room, p.Home))
            {
                p.Return = null;
            }
            else
            {
                p.VisitTimer = 7 + (float)(_random.NextDouble() * 6);
                p.Return = null;
                var exits = Map.Exits(portal.To);
                for (var i = 0; i < exits.Count; i++)
                    if (ReferenceEquals(exits[i].To, from)) { p.Return = exits[i]; break; }
            }
        }
    }

    /// <summary>Out on the other side: a few steps into the room, then look for someone to greet.</summary>
    private void Arrive(Pet p, Portal portal)
    {
        p.Goal = null;
        p.Leg = 0;
        var (minX, maxX, minY, maxY) = Room(p, p.Area);
        var inward = portal.Kind is PortalKind.Door or PortalKind.Stairs ? Math.Sign(p.Area.Rect.X + p.Area.Rect.W / 2 - p.X) * (0.25 + _random.NextDouble() * 0.3) : 0;
        p.TargetX = Math.Clamp(p.X + inward, minX, maxX);
        p.TargetY = minY + _random.NextDouble() * (maxY - minY);
        p.Creature.State = CreatureState.Wander;
        if (p.Visiting && Chatty) p.Creature.TalkCooldown = 0;
    }

    private void StepTimed(Pet p, float dt)
    {
        var c = p.Creature;
        c.StateTimer -= dt;
        c.WalkPhase += dt * 9;
        if (c.StateTimer <= 0)
        {
            c.Bubble = null;
            ToIdle(p);
        }
    }

    private void StepTalk(Pet p, float dt)
    {
        var c = p.Creature;
        c.StateTimer -= dt;
        if (c.Partner is not { } partner || partner.State != CreatureState.Talk)
        {
            EndTalk(p);
            return;
        }
        if (c.Chat is { } chat)
        {
            var elapsed = 2 * LineSeconds + 0.6f - c.StateTimer;
            c.Bubble = c.Opened
                ? (elapsed < LineSeconds ? chat.Opener : null)
                : (elapsed >= LineSeconds && elapsed < 2 * LineSeconds ? chat.Reply : null);
        }
        if (c.StateTimer <= 0) EndTalk(p);
    }

    /// <summary>Starts a chat with an idle pet in the same area. A visitor greets whoever lives there.</summary>
    private bool TryStartTalk(Pet p, bool ignoreCooldown)
    {
        Pet? best = null;
        var bestDist = double.MaxValue;
        foreach (var o in _pets)
        {
            var oc = o.Creature;
            // Not one on its way through a door or up a ladder: stopping it there left it hanging between floors.
            if (ReferenceEquals(o, p) || !ReferenceEquals(o.Area, p.Area) || ReferenceEquals(o, Held) || o.Goal is not null) continue;
            if (oc.State is not (CreatureState.Idle or CreatureState.Wander) || (!ignoreCooldown && oc.TalkCooldown > 0)) continue;
            var d = Math.Abs(o.X - p.X);
            if (d < bestDist) { best = o; bestDist = d; }
        }
        if (best is null) return false;
        var chat = TalkLines.Meet(p.Album, best.Album, Now, _random, Lyric);
        var seconds = 2 * LineSeconds + 0.6f;
        BeginTalk(p, best, opened: true, chat, seconds);
        BeginTalk(best, p, opened: false, chat, seconds);
        return true;
    }

    private static void BeginTalk(Pet who, Pet partner, bool opened, Chat chat, float seconds)
    {
        var c = who.Creature;
        c.State = CreatureState.Talk;
        c.Partner = partner.Creature;
        c.Opened = opened;
        c.Chat = chat;
        c.StateTimer = seconds;
        who.Goal = null;
        who.TargetX = who.X;
        who.TargetY = who.Y;
        var d = partner.X - who.X;
        if (Math.Abs(d) > 0.01) c.Facing = Math.Sign(d);
        c.Gaze = new Vector2(Math.Sign(d), 0.2f);
    }

    private void EndTalk(Pet p)
    {
        var c = p.Creature;
        if (c.Partner is { } partner)
        {
            c.Partner = null;
            if (ReferenceEquals(partner.Partner, c) && Owner(partner) is { } other) EndTalk(other);
        }
        c.Chat = null;
        c.Opened = false;
        if (c.State == CreatureState.Talk)
        {
            c.Bubble = null;
            c.TalkCooldown = 6 + (float)(_random.NextDouble() * 7);
            ToIdle(p);
        }
    }

    private void ToIdle(Pet p)
    {
        var c = p.Creature;
        p.Goal = null;
        c.State = c.IsPlaying ? CreatureState.Dance : CreatureState.Idle;
        c.StateTimer = 1.2f + (float)(_random.NextDouble() * 3.2);
    }

    /// <summary>Pets sharing an area step apart sideways (talkers and the held one stay put).</summary>
    private void Separate(float dt)
    {
        for (var i = 0; i < _pets.Count; i++)
        {
            var a = _pets[i];
            for (var j = i + 1; j < _pets.Count; j++)
            {
                var b = _pets[j];
                if (!ReferenceEquals(a.Area, b.Area)) continue;
                var min = (a.Size + b.Size) * 0.62;
                var d = b.X - a.X;
                var len = Math.Abs(d);
                if (len >= min) continue;
                var push = len < 0.001 ? 1 : Math.Sign(d);
                var amount = (min - len) * Math.Min(1, dt * 6) * 0.5;
                if (Free(a)) a.X -= push * amount;
                if (Free(b)) b.X += push * amount;
            }
        }
        for (var i = 0; i < _pets.Count; i++)
        {
            var p = _pets[i];
            if (p.Goal is not null || !Free(p)) continue; // walking to a door may pass the wall margin
            (p.X, p.Y) = Clamp(p, p.Area, p.X, p.Y);
        }
    }

    // Walkers pass through the others: two pets walking toward each other in a narrow room pushed
    // each other back forever and stood stuck (Discord, Jafezy 11-02). Only pets standing about
    // step aside; talkers and the held one stay put.
    private bool Free(Pet p) => p.Creature.State is not (CreatureState.Talk or CreatureState.Wander) && !ReferenceEquals(p, Held) && p.Goal is null;

    private static float Approach(float value, float target, float delta)
        => value < target ? Math.Min(target, value + delta) : Math.Max(target, value - delta);
}
