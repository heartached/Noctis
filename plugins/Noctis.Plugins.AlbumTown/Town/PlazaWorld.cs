using System.Numerics;

namespace Noctis.Plugins.AlbumTown.Town;

public enum CreatureState { Idle, Wander, LookAtCursor, Wave, Talk, Dance, Leave }

/// <summary>One creature in the plaza. Plain data the world steps and the canvas draws.</summary>
public sealed class Creature
{
    public Creature(CastMember member, Vector2 position)
    {
        Member = member;
        Position = position;
        Target = position;
    }

    public CastMember Member { get; }
    public TownAlbum Album => Member.Album;

    public Vector2 Position;
    public Vector2 Target;
    /// <summary>Where the eyes look (unit-ish vector).</summary>
    public Vector2 Gaze = new(0, 1);
    /// <summary>-1 facing left, +1 facing right.</summary>
    public float Facing = 1;
    public CreatureState State { get; internal set; } = CreatureState.Idle;
    /// <summary>Seconds left in the current state (Idle, Wave, Talk).</summary>
    public float StateTimer;
    /// <summary>Walk cycle phase (radians); advances while moving.</summary>
    public float WalkPhase;
    /// <summary>0..1, eased toward 1 while walking (limbs swing with it).</summary>
    public float Stride;
    public float TalkCooldown;
    public Creature? Partner { get; internal set; }
    /// <summary>True for the half of a chat that opened it.</summary>
    public bool Opened { get; internal set; }
    public Chat? Chat { get; internal set; }
    /// <summary>The line in the bubble right now, or null.</summary>
    public string? Bubble { get; internal set; }
    /// <summary>Seconds until the next blink.</summary>
    public float BlinkTimer;
    public bool IsPlaying { get; internal set; }
    /// <summary>Beat pulse (0..1) for the dancer.</summary>
    public float Pulse;
    /// <summary>Seconds the creature has been in the plaza (fades in on arrival).</summary>
    public float Age;
    /// <summary>Speed in px/s at <see cref="PlazaWorld.BaseSize"/>, a little different per creature
    /// (the world scales it with the body size so big creatures do not crawl).</summary>
    public float Speed = 42;
    /// <summary>Seconds left for a line said on the way (arriving, leaving), or 0.</summary>
    public float SayTimer;
    /// <summary>Walking in from outside the plaza (not held inside its edges yet).</summary>
    public bool Entering { get; internal set; }
}

/// <summary>
/// The plaza simulation: a small state machine per creature plus simple steering. No drawing,
/// no Avalonia: <see cref="Step"/> advances time, the canvas reads the creatures.
/// Idle → Wander (to a random spot) → Idle; two idle neighbours may stop and Talk (two lines);
/// an idle creature near the cursor turns to look at it; a click makes it Wave; the album that
/// is playing Dances in place to the beat. Distances (reach, strolls, spacing, margins, speed)
/// are tuned at <see cref="BaseSize"/> and scale with <see cref="BodySize"/>, so a zoomed-in
/// plaza behaves the same, only bigger. The creature under the pointer (<see cref="Held"/>)
/// stops and looks at it, so it is easy to click.
/// </summary>
public sealed class PlazaWorld
{
    /// <summary>The body size the distances below are tuned for.</summary>
    public const float BaseSize = 36;
    private const float TalkReach = 90;
    private const float LookReach = 110;
    private const float LineSeconds = 2.6f;

    private readonly Random _random;
    private readonly List<Creature> _creatures = new();

    public PlazaWorld(float width, float height, Random random)
    {
        _random = random;
        Resize(width, height);
    }

    public float Width { get; private set; }
    public float Height { get; private set; }
    public IReadOnlyList<Creature> Creatures => _creatures;
    /// <summary>Bumped whenever the crowd changes (someone arrives or leaves).</summary>
    public int Version { get; private set; }
    public bool Chatty { get; set; } = true;
    /// <summary>Pointer position in plaza coordinates, or null when it is outside.</summary>
    public Vector2? Cursor { get; set; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    /// <summary>A lyric line of the album's songs to quote, or null (the view knows the lyrics).</summary>
    public Func<TownAlbum, string?>? Lyric { get; set; }

    /// <summary>Width of a creature's cover face in px (the canvas sets it from the window and zoom).</summary>
    public float BodySize
    {
        get => _bodySize;
        set
        {
            value = Math.Max(8, value);
            if (value == _bodySize) return;
            _bodySize = value;
            foreach (var c in _creatures)
            {
                c.Position = Clamp(c.Position);
                c.Target = Clamp(c.Target);
            }
        }
    }
    private float _bodySize = BaseSize;

    /// <summary><see cref="BodySize"/> relative to <see cref="BaseSize"/>.</summary>
    public float Scale => _bodySize / BaseSize;
    /// <summary>The creature under the pointer: it holds still and looks at the pointer.</summary>
    public Creature? Held { get; set; }
    /// <summary>Margin kept free at the edges (bubbles and names need room).</summary>
    public float Margin => 28 * Scale;
    /// <summary>Extra room at the top for the head and a speech bubble above it.</summary>
    public float TopMargin => Margin + 24 * Scale;

    /// <summary>New plaza size: everyone keeps their relative spot (a crowd placed before the
    /// window had its size spreads out instead of bunching in the old corner).</summary>
    public void Resize(float width, float height)
    {
        width = Math.Max(120, width);
        height = Math.Max(120, height);
        if (width == Width && height == Height) return;
        var sx = Width > 0 ? width / Width : 1;
        var sy = Height > 0 ? height / Height : 1;
        Width = width;
        Height = height;
        foreach (var c in _creatures)
        {
            c.Position = Clamp(new Vector2(c.Position.X * sx, c.Position.Y * sy));
            c.Target = Clamp(new Vector2(c.Target.X * sx, c.Target.Y * sy));
        }
    }

    /// <summary>Replaces the crowd. Creatures already present stay where they are.</summary>
    public void SetCast(IReadOnlyList<CastMember> cast)
    {
        var old = _creatures.ToDictionary(c => c.Album.Key, StringComparer.Ordinal);
        _creatures.Clear();
        foreach (var m in cast)
        {
            if (old.TryGetValue(m.Album.Key, out var keep) && keep.Member == m) { _creatures.Add(keep); continue; }
            var c = old.TryGetValue(m.Album.Key, out var prev)
                ? new Creature(m, prev.Position)
                : new Creature(m, RandomSpot());
            Prime(c);
            _creatures.Add(c);
        }
        foreach (var c in _creatures)
            if (c.Partner is { } p && !_creatures.Contains(p)) EndTalk(c);
        Version++;
    }

    /// <summary>
    /// Adds one resident (a called album): it walks in from outside the top, bottom, left or
    /// right edge and says hello to everyone on the way (Discord, Jafezy 11-02).
    /// </summary>
    public Creature Call(CastMember member)
    {
        var existing = _creatures.FirstOrDefault(c => c.Album.Key == member.Album.Key && c.State != CreatureState.Leave);
        if (existing is not null) { Wave(existing); return existing; }
        var spot = RandomSpot();
        var off = _bodySize * 1.2f;
        var start = _random.Next(4) switch
        {
            0 => new Vector2(spot.X, -off),
            1 => new Vector2(spot.X, Height + off),
            2 => new Vector2(-off, spot.Y),
            _ => new Vector2(Width + off, spot.Y),
        };
        var c = new Creature(member, start);
        Prime(c);
        c.Target = spot;
        c.State = CreatureState.Wander;
        c.Entering = true;
        c.Bubble = TalkLines.Arrival(member.Album);
        c.SayTimer = 3.2f;
        c.TalkCooldown = Math.Max(c.TalkCooldown, 4);
        _creatures.Add(c);
        Version++;
        return c;
    }

    /// <summary>The resident of <paramref name="key"/> says bye and walks out of the nearest edge,
    /// then leaves the plaza (a called album took its place). False when it is not here.</summary>
    public bool SendAway(string key)
    {
        var c = _creatures.FirstOrDefault(x => x.Album.Key == key && x.State != CreatureState.Leave);
        if (c is null) return false;
        EndTalk(c);
        if (ReferenceEquals(Held, c)) Held = null;
        var p = c.Position;
        var off = _bodySize * 1.5f;
        var (left, right, top, bottom) = (p.X, Width - p.X, p.Y, Height - p.Y);
        var nearest = Math.Min(Math.Min(left, right), Math.Min(top, bottom));
        c.Target = nearest == left ? new Vector2(-off, p.Y)
            : nearest == right ? new Vector2(Width + off, p.Y)
            : nearest == top ? new Vector2(p.X, -off)
            : new Vector2(p.X, Height + off);
        c.State = CreatureState.Leave;
        c.IsPlaying = false;
        c.Bubble = TalkLines.Leaving;
        c.SayTimer = 1.8f;
        Version++;
        return true;
    }

    /// <summary>Residents on their way out (still drawn until they are gone).</summary>
    public int LeavingCount
    {
        get
        {
            var n = 0;
            foreach (var c in _creatures)
                if (c.State == CreatureState.Leave) n++;
            return n;
        }
    }

    private void Prime(Creature c)
    {
        c.StateTimer = (float)(_random.NextDouble() * 2.5);
        c.BlinkTimer = 1 + (float)(_random.NextDouble() * 4);
        c.TalkCooldown = 2 + (float)(_random.NextDouble() * 6);
        c.Speed = 34 + (float)(_random.NextDouble() * 18);
        c.WalkPhase = (float)(_random.NextDouble() * Math.PI * 2);
        c.Facing = _random.Next(2) == 0 ? -1 : 1;
    }

    /// <summary>Marks the creature of <paramref name="key"/> as playing (null: none).</summary>
    public void SetNowPlaying(string? key, bool playing)
    {
        foreach (var c in _creatures)
        {
            var now = playing && key is not null && c.Album.Key == key && c.State != CreatureState.Leave;
            if (now == c.IsPlaying) continue;
            c.IsPlaying = now;
            if (now)
            {
                EndTalk(c);
                c.State = CreatureState.Dance;
                c.Bubble = null;
            }
            else if (c.State == CreatureState.Dance) ToIdle(c);
        }
    }

    /// <summary>
    /// The creature under <paramref name="point"/>, or null. The target is generous (the cover
    /// plus the hands, feet and a little air around them): the front-most creature whose cover
    /// is under the point wins, otherwise the nearest one whose wider target is.
    /// <paramref name="prefer"/> (the one already hovered) keeps winning while the point is
    /// anywhere on its target, so overlapping neighbours do not flicker.
    /// </summary>
    public Creature? HitTest(Vector2 point, Creature? prefer = null)
    {
        var s = _bodySize;
        if (prefer is not null && _creatures.Contains(prefer) && InTarget(prefer, point, s)) return prefer;
        Creature? onCover = null;
        Creature? near = null;
        var nearDist = float.MaxValue;
        foreach (var c in _creatures)
        {
            var d = point - BodyCenter(c, s);
            if (Math.Abs(d.X) <= s * 0.5f && Math.Abs(d.Y) <= s * 0.5f)
            {
                if (onCover is null || c.Position.Y > onCover.Position.Y) onCover = c;
            }
            else if (onCover is null && InTarget(c, point, s) && d.LengthSquared() < nearDist)
            {
                near = c;
                nearDist = d.LengthSquared();
            }
        }
        return onCover ?? near;
    }

    /// <summary>Middle of the cover face (it floats a little above the feet).</summary>
    public static Vector2 BodyCenter(Creature c, float size) => c.Position - new Vector2(0, size * 0.8f);

    private static bool InTarget(Creature c, Vector2 point, float s)
    {
        var d = point - BodyCenter(c, s);
        return Math.Abs(d.X) <= s * 0.8f && d.Y >= -s * 0.75f && d.Y <= s * 0.95f;
    }

    /// <summary>
    /// True when nothing in the plaza moves or speaks: everyone stands idle (no walk still easing
    /// out, no bubble, no dancer, nobody fading in). Until <see cref="NextChangeIn"/> the picture
    /// stays the same, so the canvas can stop drawing frames.
    /// </summary>
    public bool IsQuiet()
    {
        foreach (var c in _creatures)
        {
            if (c.State is not (CreatureState.Idle or CreatureState.LookAtCursor) || c.SayTimer > 0) return false;
            if (c.Stride > 0.001f || c.Bubble is not null || c.IsPlaying || c.Age < 0.5f) return false;
        }
        return true;
    }

    /// <summary>Seconds until a quiet plaza changes by itself (the first idle creature sets off).</summary>
    public float NextChangeIn()
    {
        var next = float.MaxValue;
        foreach (var c in _creatures)
            if (c.State == CreatureState.Idle) next = Math.Min(next, Math.Max(0, c.StateTimer));
        return next;
    }

    /// <summary>Time passed while the plaza slept (nothing moved): runs the clocks forward, so the
    /// first creature sets off when it would have. Eyes stay open.</summary>
    public void Advance(float seconds)
    {
        if (seconds <= 0) return;
        foreach (var c in _creatures)
        {
            c.StateTimer -= seconds;
            c.TalkCooldown -= seconds;
            c.Age += seconds;
            if (c.BlinkTimer < 0.2f) c.BlinkTimer = 0.2f + (float)_random.NextDouble() * 2;
        }
    }

    /// <summary>Everyone stops where they are and stands idle for <paramref name="seconds"/>
    /// (no chats, no bubbles; the dancer keeps dancing). Tests and the benchmark use it.</summary>
    public void HoldStill(float seconds)
    {
        foreach (var c in _creatures)
        {
            EndTalk(c);
            c.Bubble = null;
            c.Stride = 0;
            c.Target = c.Position;
            c.State = c.IsPlaying ? CreatureState.Dance : CreatureState.Idle;
            c.StateTimer = seconds;
            if (c.Age < 0.5f) c.Age = 0.5f;
        }
    }

    /// <summary>Click: the creature stops, waves and says hello.</summary>
    public void Wave(Creature c)
    {
        if (c.State == CreatureState.Leave) return; // on its way out
        EndTalk(c);
        c.State = CreatureState.Wave;
        c.StateTimer = 1.8f;
        c.Bubble = TalkLines.Hello(c.Album, Now, _random, Lyric);
        if (Cursor is { } cur) FaceToward(c, cur);
        else c.Gaze = new Vector2(0, 1);
    }

    /// <summary>Advances the plaza by <paramref name="dt"/> seconds (clamped to 0.1 s so a
    /// stall never teleports anyone). <paramref name="pulse"/> is the beat (0..1).</summary>
    public void Step(float dt, float pulse = 0)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        var gone = false;
        foreach (var c in _creatures)
        {
            c.Age += dt;
            if (c.SayTimer > 0)
            {
                c.SayTimer -= dt;
                if (c.SayTimer <= 0 && c.State is not (CreatureState.Talk or CreatureState.Wave)) c.Bubble = null;
            }
            c.TalkCooldown -= dt;
            c.BlinkTimer -= dt;
            if (c.BlinkTimer < -0.12f) c.BlinkTimer = 1.5f + (float)(_random.NextDouble() * 4);
            if (ReferenceEquals(c, Held) && c.State is CreatureState.Idle or CreatureState.Wander or CreatureState.LookAtCursor)
            {
                // Under the pointer: stop and look at it, so it is easy to click.
                c.State = CreatureState.LookAtCursor;
                c.Target = c.Position;
            }
            switch (c.State)
            {
                case CreatureState.Idle: StepIdle(c, dt); break;
                case CreatureState.Wander: StepWander(c, dt); break;
                case CreatureState.LookAtCursor: StepLook(c, dt); break;
                case CreatureState.Wave: StepTimed(c, dt); break;
                case CreatureState.Talk: StepTalk(c, dt); break;
                case CreatureState.Dance:
                    c.Pulse = pulse;
                    c.WalkPhase += dt * (4f + 6f * pulse);
                    c.Stride = Approach(c.Stride, 0, dt * 4);
                    c.Gaze = new Vector2(0, 1);
                    break;
                case CreatureState.Leave: gone |= StepLeave(c, dt); break;
            }
            if (c.State is not (CreatureState.Wander or CreatureState.Leave)) c.Stride = Approach(c.Stride, 0, dt * 4);
        }
        if (gone)
        {
            _creatures.RemoveAll(c => c.State == CreatureState.Leave && c.Target == c.Position);
            Version++;
        }
        Separate(dt);
    }

    /// <summary>Walks out; true once it is past the edge (then it is removed).</summary>
    private bool StepLeave(Creature c, float dt)
    {
        var to = c.Target - c.Position;
        var dist = to.Length();
        if (dist < 2)
        {
            c.Position = c.Target;
            return true;
        }
        var dir = to / dist;
        c.Position += dir * Math.Min(dist, c.Speed * 1.3f * Scale * dt);
        if (Math.Abs(dir.X) > 0.15f) c.Facing = Math.Sign(dir.X);
        c.Gaze = Vector2.Lerp(c.Gaze, dir, Math.Min(1, dt * 6));
        c.Stride = Approach(c.Stride, 1, dt * 4);
        c.WalkPhase += dt * c.Speed * 0.28f;
        return false;
    }

    private void StepIdle(Creature c, float dt)
    {
        if (Cursor is { } cur && Vector2.Distance(cur, c.Position) < LookReach * Scale)
        {
            c.State = CreatureState.LookAtCursor;
            return;
        }
        c.StateTimer -= dt;
        if (c.StateTimer > 0) return;
        if (Chatty && c.TalkCooldown <= 0 && TryStartTalk(c)) return;
        c.Target = WanderTarget(c);
        c.State = CreatureState.Wander;
    }

    private void StepWander(Creature c, float dt)
    {
        var to = c.Target - c.Position;
        var dist = to.Length();
        if (dist < 2)
        {
            ToIdle(c);
            return;
        }
        var step = Math.Min(dist, c.Speed * Scale * dt);
        var dir = to / dist;
        c.Position += dir * step;
        if (c.Entering && Clamp(c.Position) == c.Position) c.Entering = false;
        if (Math.Abs(dir.X) > 0.15f) c.Facing = Math.Sign(dir.X);
        c.Gaze = Vector2.Lerp(c.Gaze, dir, Math.Min(1, dt * 6));
        c.Stride = Approach(c.Stride, 1, dt * 4);
        c.WalkPhase += dt * c.Speed * 0.22f;
        // A walker passing a free neighbour may stop to chat.
        if (Chatty && c.TalkCooldown <= 0 && _random.NextDouble() < dt * 1.2) TryStartTalk(c);
    }

    private void StepLook(Creature c, float dt)
    {
        var held = ReferenceEquals(c, Held);
        if (Cursor is not { } cur || (!held && Vector2.Distance(cur, c.Position) > LookReach * Scale * 1.15f))
        {
            ToIdle(c);
            return;
        }
        FaceToward(c, cur);
    }

    private void StepTimed(Creature c, float dt)
    {
        c.StateTimer -= dt;
        c.WalkPhase += dt * 9; // the waving arm
        if (c.StateTimer <= 0)
        {
            c.Bubble = null;
            ToIdle(c);
        }
    }

    private void StepTalk(Creature c, float dt)
    {
        c.StateTimer -= dt;
        if (c.Partner is not { } p || p.State != CreatureState.Talk)
        {
            EndTalk(c);
            return;
        }
        FaceToward(c, p.Position);
        if (c.Chat is { } chat)
        {
            // Opener speaks first, the partner answers; then both go quiet a moment.
            var elapsed = 2 * LineSeconds + 0.6f - c.StateTimer;
            c.Bubble = c.Opened
                ? (elapsed < LineSeconds ? chat.Opener : null)
                : (elapsed >= LineSeconds && elapsed < 2 * LineSeconds ? chat.Reply : null);
        }
        if (c.StateTimer <= 0) EndTalk(c);
    }

    private bool TryStartTalk(Creature c)
    {
        Creature? best = null;
        var bestDist = TalkReach * Scale;
        foreach (var o in _creatures)
        {
            if (ReferenceEquals(o, c) || ReferenceEquals(o, Held) || o.TalkCooldown > 0 || o.State is not (CreatureState.Idle or CreatureState.Wander)) continue;
            var d = Vector2.Distance(o.Position, c.Position);
            if (d < bestDist) { best = o; bestDist = d; }
        }
        if (best is null) return false;
        var chat = TalkLines.Meet(c.Album, best.Album, Now, _random, Lyric);
        var seconds = 2 * LineSeconds + 0.6f;
        BeginTalk(c, best, opened: true, chat, seconds);
        BeginTalk(best, c, opened: false, chat, seconds);
        return true;
    }

    private static void BeginTalk(Creature who, Creature partner, bool opened, Chat chat, float seconds)
    {
        who.State = CreatureState.Talk;
        who.Partner = partner;
        who.Opened = opened;
        who.Chat = chat;
        who.StateTimer = seconds;
        who.Target = who.Position;
        FaceToward(who, partner.Position);
    }

    private void EndTalk(Creature c)
    {
        if (c.Partner is { } p)
        {
            c.Partner = null;
            if (ReferenceEquals(p.Partner, c)) EndTalk(p);
        }
        c.Chat = null;
        c.Opened = false;
        if (c.State == CreatureState.Talk)
        {
            c.Bubble = null;
            c.TalkCooldown = 6 + (float)(_random.NextDouble() * 7);
            ToIdle(c);
        }
    }

    private void ToIdle(Creature c)
    {
        c.State = c.IsPlaying ? CreatureState.Dance : CreatureState.Idle;
        c.StateTimer = 0.8f + (float)(_random.NextDouble() * 2.8);
    }

    private static void FaceToward(Creature c, Vector2 point)
    {
        var d = point - c.Position;
        var len = d.Length();
        if (len < 0.001f) return;
        c.Gaze = d / len;
        if (Math.Abs(d.X) > 4) c.Facing = Math.Sign(d.X);
    }

    /// <summary>Pushes overlapping creatures apart (O(n²), fine for 60). Talkers and the one
    /// under the pointer stand their ground.</summary>
    private void Separate(float dt)
    {
        var min = _bodySize * 1.35f; // the tile plus its hands
        for (var i = 0; i < _creatures.Count; i++)
        {
            var a = _creatures[i];
            if (a.State == CreatureState.Leave || a.Entering) continue; // walking in or out: off the plaza's edges
            for (var j = i + 1; j < _creatures.Count; j++)
            {
                var b = _creatures[j];
                if (b.State == CreatureState.Leave || b.Entering) continue;
                var d = b.Position - a.Position;
                var len = d.Length();
                if (len >= min) continue;
                var push = len < 0.01f ? new Vector2(1, 0) : d / len;
                var amount = (min - len) * Math.Min(1, dt * 8) * 0.5f;
                if (a.State != CreatureState.Talk && !ReferenceEquals(a, Held)) a.Position -= push * amount;
                if (b.State != CreatureState.Talk && !ReferenceEquals(b, Held)) b.Position += push * amount;
            }
            a.Position = Clamp(a.Position);
        }
    }

    private Vector2 WanderTarget(Creature c)
    {
        // Mostly short strolls, sometimes across the plaza.
        if (_random.NextDouble() < 0.75)
        {
            var angle = _random.NextDouble() * Math.PI * 2;
            var r = (40 + _random.NextDouble() * 110) * Scale;
            return Clamp(c.Position + new Vector2((float)(Math.Cos(angle) * r), (float)(Math.Sin(angle) * r * 0.7)));
        }
        return RandomSpot();
    }

    private Vector2 RandomSpot()
        => new(Margin + (float)_random.NextDouble() * Math.Max(0, Width - 2 * Margin),
            TopMargin + (float)_random.NextDouble() * Math.Max(0, Height - Margin - TopMargin));

    private Vector2 Clamp(Vector2 p)
        => new(Math.Clamp(p.X, Margin, Math.Max(Margin, Width - Margin)), Math.Clamp(p.Y, TopMargin, Math.Max(TopMargin, Height - Margin)));

    private static float Approach(float value, float target, float delta)
        => value < target ? Math.Min(target, value + delta) : Math.Max(target, value - delta);
}
