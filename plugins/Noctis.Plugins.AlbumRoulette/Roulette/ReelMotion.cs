namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>
/// The reel's motion, apart from any drawing: a strip of album slots (indices into the
/// candidates) scrolls past the stage, fast at first, then slows down and lands on the pick
/// (<see cref="TargetSlot"/>; a couple of covers follow it, so the reel still shows neighbours
/// when it stops). <see cref="Offset"/> is the slot at the centre (fractional while moving).
/// The strip lives in a fixed array, so a frame (<see cref="Step"/>) allocates nothing.
/// </summary>
public sealed class ReelMotion
{
    /// <summary>Most slots a strip holds.</summary>
    public const int MaxSlots = 64;

    /// <summary>Full: about 30 covers flick past in 3.2 s.</summary>
    public const int FullSlots = 30;
    public const double FullSeconds = 3.2;
    /// <summary>Reduced: a short, gentle move over a few covers.</summary>
    public const int ReducedSlots = 4;
    public const double ReducedSeconds = 0.7;
    /// <summary>Covers after the pick on the strip.</summary>
    public const int Tail = 2;
    /// <summary>The landing: the cover pops a little and the glow swells.</summary>
    public const double LandSeconds = 0.45;

    private readonly int[] _slots = new int[MaxSlots];
    private double _elapsed;
    private double _duration;
    private double _landElapsed = LandSeconds;

    /// <summary>How many slots the current strip has (0 before the first spin).</summary>
    public int Length { get; private set; }

    /// <summary>The slot at the centre of the stage; <see cref="TargetSlot"/> when landed.</summary>
    public double Offset { get; private set; }

    /// <summary>The pick's slot.</summary>
    public int TargetSlot { get; private set; }

    public bool IsSpinning { get; private set; }

    /// <summary>0..1 while the landing pop plays, else 1.</summary>
    public double Landing => Math.Clamp(_landElapsed / LandSeconds, 0, 1);

    /// <summary>True while anything still moves (spin or landing).</summary>
    public bool IsMoving => IsSpinning || _landElapsed < LandSeconds;

    /// <summary>The candidate index in slot <paramref name="slot"/>.</summary>
    public int SlotAt(int slot) => _slots[Math.Clamp(slot, 0, Math.Max(0, Length - 1))];

    /// <summary>The candidate the reel shows (or lands on).</summary>
    public int Current => Length == 0 ? -1 : SlotAt((int)Math.Round(Offset));

    /// <summary>
    /// Fills a strip of <paramref name="slots"/> covers chosen with <paramref name="random"/> from
    /// <paramref name="candidateCount"/> candidates, ending on <paramref name="target"/> (then
    /// <see cref="Tail"/> more), and starts
    /// the spin. The strip keeps the slot showing now first, so the reel starts where it stopped.
    /// Neighbouring slots never repeat a cover when there is more than one candidate.
    /// </summary>
    public void Start(int candidateCount, int target, int slots, double seconds, Random random, int? showingNow = null)
    {
        if (candidateCount <= 0) throw new ArgumentOutOfRangeException(nameof(candidateCount));
        slots = Math.Clamp(slots, 2, MaxSlots - Tail);
        _slots[0] = showingNow is { } s && s >= 0 && s < candidateCount ? s : random.Next(candidateCount);
        for (var i = 1; i < slots + Tail; i++)
        {
            var pick = random.Next(candidateCount);
            if (candidateCount > 1 && pick == _slots[i - 1]) pick = (pick + 1) % candidateCount;
            _slots[i] = pick;
        }
        _slots[slots - 1] = target;
        if (candidateCount > 2 && _slots[slots - 2] == target) _slots[slots - 2] = (target + 1) % candidateCount;
        if (candidateCount > 2 && _slots[slots] == target) _slots[slots] = (target + 1) % candidateCount;
        TargetSlot = slots - 1;
        Length = slots + Tail;
        Offset = 0;
        _elapsed = 0;
        _duration = Math.Max(0.05, seconds);
        _landElapsed = LandSeconds;
        IsSpinning = true;
    }

    /// <summary>Shows <paramref name="candidate"/> without a spin (Album of the Day, a filter change).</summary>
    public void Show(int candidate)
    {
        _slots[0] = candidate;
        Length = 1;
        TargetSlot = 0;
        Offset = 0;
        IsSpinning = false;
        _landElapsed = LandSeconds;
    }

    /// <summary>Clears the stage.</summary>
    public void Reset()
    {
        Length = 0;
        TargetSlot = 0;
        Offset = 0;
        IsSpinning = false;
        _landElapsed = LandSeconds;
    }

    /// <summary>Advances by <paramref name="dt"/> seconds. True when the reel landed this step.</summary>
    public bool Step(double dt)
    {
        if (dt < 0 || double.IsNaN(dt)) dt = 0;
        dt = Math.Min(dt, 0.1); // a stalled frame does not skip the slow-down
        if (!IsSpinning)
        {
            if (_landElapsed < LandSeconds) _landElapsed += dt;
            return false;
        }
        _elapsed += dt;
        var u = Math.Clamp(_elapsed / _duration, 0, 1);
        Offset = TargetSlot * Ease(u);
        if (u < 1) return false;
        Offset = TargetSlot;
        IsSpinning = false;
        _landElapsed = 0;
        return true;
    }

    /// <summary>Finishes the spin now (the window was hidden, or the user asked again).</summary>
    public void Finish()
    {
        if (!IsSpinning) return;
        Offset = TargetSlot;
        IsSpinning = false;
        _landElapsed = LandSeconds;
    }

    /// <summary>Ease-out quart: full speed at the start, a long slow-down, zero speed on landing.</summary>
    public static double Ease(double u)
    {
        var v = 1 - u;
        return 1 - v * v * v * v;
    }

    /// <summary>The landing pop: 1 at rest, up to ~1.05 right after the reel stops.</summary>
    public double PopScale
    {
        get
        {
            var t = Landing;
            return t >= 1 ? 1 : 1 + 0.05 * Math.Sin(Math.PI * t) * (1 - t * 0.5);
        }
    }
}
