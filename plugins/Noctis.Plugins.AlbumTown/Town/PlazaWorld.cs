using System.Numerics;

namespace Noctis.Plugins.AlbumTown.Town;

public enum CreatureState { Idle, Wander, LookAtCursor, Wave, Talk, Dance }

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
    /// <summary>Speed in px/s, a little different per creature.</summary>
    public float Speed = 42;
}

/// <summary>
/// The plaza simulation: a small state machine per creature plus simple steering. No drawing,
/// no Avalonia: <see cref="Step"/> advances time, the canvas reads the creatures.
/// Idle → Wander (to a random spot) → Idle; two idle neighbours may stop and Talk (two lines);
/// an idle creature near the cursor turns to look at it; a click makes it Wave; the album that
/// is playing Dances in place to the beat.
/// </summary>
public sealed class PlazaWorld
{
    public const float CreatureRadius = 18;
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
    public bool Chatty { get; set; } = true;
    /// <summary>Pointer position in plaza coordinates, or null when it is outside.</summary>
    public Vector2? Cursor { get; set; }
    public DateTimeOffset Now { get; set; } = DateTimeOffset.Now;
    /// <summary>Margin kept free at the edges (bubbles and names need room).</summary>
    public float Margin => 28;

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
    }

    /// <summary>Adds one resident (a called album) walking in from the nearest edge.</summary>
    public Creature Call(CastMember member)
    {
        var existing = _creatures.FirstOrDefault(c => c.Album.Key == member.Album.Key);
        if (existing is not null) { Wave(existing); return existing; }
        var spot = RandomSpot();
        var edge = new Vector2(spot.X, _random.Next(2) == 0 ? Margin + 24 : Height - Margin);
        var c = new Creature(member, edge);
        Prime(c);
        c.Target = spot;
        c.State = CreatureState.Wander;
        _creatures.Add(c);
        return c;
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
            var now = playing && key is not null && c.Album.Key == key;
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

    /// <summary>The topmost creature under <paramref name="point"/>, or null.</summary>
    public Creature? HitTest(Vector2 point)
    {
        Creature? best = null;
        foreach (var c in _creatures)
        {
            var d = point - (c.Position - new Vector2(0, CreatureRadius));
            if (Math.Abs(d.X) <= CreatureRadius + 4 && Math.Abs(d.Y) <= CreatureRadius + 10
                && (best is null || c.Position.Y > best.Position.Y))
                best = c;
        }
        return best;
    }

    /// <summary>Click: the creature stops, waves and says hello.</summary>
    public void Wave(Creature c)
    {
        EndTalk(c);
        c.State = CreatureState.Wave;
        c.StateTimer = 1.8f;
        c.Bubble = TalkLines.Greeting(c.Album, Now);
        if (Cursor is { } cur) FaceToward(c, cur);
        else c.Gaze = new Vector2(0, 1);
    }

    /// <summary>Advances the plaza by <paramref name="dt"/> seconds (clamped to 0.1 s so a
    /// stall never teleports anyone). <paramref name="pulse"/> is the beat (0..1).</summary>
    public void Step(float dt, float pulse = 0)
    {
        dt = Math.Clamp(dt, 0, 0.1f);
        foreach (var c in _creatures)
        {
            c.Age += dt;
            c.TalkCooldown -= dt;
            c.BlinkTimer -= dt;
            if (c.BlinkTimer < -0.12f) c.BlinkTimer = 1.5f + (float)(_random.NextDouble() * 4);
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
            }
            if (c.State != CreatureState.Wander) c.Stride = Approach(c.Stride, 0, dt * 4);
        }
        Separate(dt);
    }

    private void StepIdle(Creature c, float dt)
    {
        if (Cursor is { } cur && Vector2.Distance(cur, c.Position) < LookReach)
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
        var step = Math.Min(dist, c.Speed * dt);
        var dir = to / dist;
        c.Position += dir * step;
        if (Math.Abs(dir.X) > 0.15f) c.Facing = Math.Sign(dir.X);
        c.Gaze = Vector2.Lerp(c.Gaze, dir, Math.Min(1, dt * 6));
        c.Stride = Approach(c.Stride, 1, dt * 4);
        c.WalkPhase += dt * c.Speed * 0.22f;
        // A walker passing a free neighbour may stop to chat.
        if (Chatty && c.TalkCooldown <= 0 && _random.NextDouble() < dt * 1.2) TryStartTalk(c);
    }

    private void StepLook(Creature c, float dt)
    {
        if (Cursor is not { } cur || Vector2.Distance(cur, c.Position) > LookReach * 1.15f)
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
        var bestDist = TalkReach;
        foreach (var o in _creatures)
        {
            if (ReferenceEquals(o, c) || o.TalkCooldown > 0 || o.State is not (CreatureState.Idle or CreatureState.Wander)) continue;
            var d = Vector2.Distance(o.Position, c.Position);
            if (d < bestDist) { best = o; bestDist = d; }
        }
        if (best is null) return false;
        var chat = TalkLines.Between(c.Album, best.Album, Now, _random);
        var seconds = 2 * LineSeconds + 0.6f;
        foreach (var (who, partner, opened) in new[] { (c, best, true), (best, c, false) })
        {
            who.State = CreatureState.Talk;
            who.Partner = partner;
            who.Opened = opened;
            who.Chat = chat;
            who.StateTimer = seconds;
            who.Target = who.Position;
            FaceToward(who, partner.Position);
        }
        return true;
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

    /// <summary>Pushes overlapping creatures apart (O(n²), fine for 60).</summary>
    private void Separate(float dt)
    {
        const float min = CreatureRadius * 2 + 4;
        for (var i = 0; i < _creatures.Count; i++)
        {
            var a = _creatures[i];
            for (var j = i + 1; j < _creatures.Count; j++)
            {
                var b = _creatures[j];
                var d = b.Position - a.Position;
                var len = d.Length();
                if (len >= min) continue;
                var push = len < 0.01f ? new Vector2(1, 0) : d / len;
                var amount = (min - len) * Math.Min(1, dt * 8) * 0.5f;
                if (a.State != CreatureState.Talk) a.Position -= push * amount;
                if (b.State != CreatureState.Talk) b.Position += push * amount;
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
            var r = 40 + _random.NextDouble() * 110;
            return Clamp(c.Position + new Vector2((float)(Math.Cos(angle) * r), (float)(Math.Sin(angle) * r * 0.7)));
        }
        return RandomSpot();
    }

    private Vector2 RandomSpot()
        => new(Margin + (float)_random.NextDouble() * (Width - 2 * Margin), Margin + 24 + (float)_random.NextDouble() * (Height - 2 * Margin - 24));

    private Vector2 Clamp(Vector2 p)
        => new(Math.Clamp(p.X, Margin, Width - Margin), Math.Clamp(p.Y, Margin + 24, Height - Margin));

    private static float Approach(float value, float target, float delta)
        => value < target ? Math.Min(target, value + delta) : Math.Max(target, value - delta);
}
