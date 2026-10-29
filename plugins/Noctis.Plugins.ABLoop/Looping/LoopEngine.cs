namespace Noctis.Plugins.ABLoop.Looping;

/// <summary>The player as the loop sees it. <see cref="PluginLoopPlayer"/> is the real one; tests use a simulated player.</summary>
public interface ILoopPlayer
{
    /// <summary>Playback position as the host reports it (it moves in steps, about every 100 ms).</summary>
    TimeSpan Position { get; }
    bool IsPlaying { get; }
    /// <summary>Length of the current song (zero when unknown).</summary>
    TimeSpan Duration { get; }
    void Seek(TimeSpan position);
    void Play();
    void Pause();
}

/// <summary>
/// The A-B loop. The host's position moves in steps (about every 100 ms), far too coarse to
/// loop on, so <see cref="Tick"/> (every ~15 ms) predicts where playback is between steps: the
/// last reported value plus the time since it was first seen. When the prediction reaches B
/// (half a tick early, so the error is centred on zero) it seeks back to A, after an optional
/// gap, and counts the pass; with a count set it lets the song play on after the last one.
/// The jump itself is the host's ordinary seek: how fast the audio follows is up to the engine
/// (docs/PLUGINS.md, A-B Loop).
/// </summary>
public sealed class LoopEngine
{
    /// <summary>Shortest part that can be looped.</summary>
    public static readonly TimeSpan MinLength = TimeSpan.FromMilliseconds(100);
    /// <summary>How far past the last report the prediction may run (a stall must not run away).</summary>
    public static readonly TimeSpan MaxExtrapolation = TimeSpan.FromMilliseconds(1200);
    /// <summary>B is only acted on when the prediction is at most this far past it: a jump far
    /// past B (the user seeking) is not pulled back.</summary>
    public static readonly TimeSpan CrossWindow = TimeSpan.FromMilliseconds(400);
    /// <summary>After a jump, reports that cannot be from after it are ignored for this long.</summary>
    public static readonly TimeSpan SettleWindow = TimeSpan.FromMilliseconds(700);

    private readonly ILoopPlayer _player;
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _anchorPos;
    private TimeSpan _anchorTime;
    private TimeSpan _lastObserved = TimeSpan.MinValue;
    private TimeSpan _lastPoll = TimeSpan.MinValue;
    private TimeSpan _lastTick = TimeSpan.MinValue;
    private bool _wasPlaying;
    private TimeSpan _seekTarget;
    private TimeSpan _settleUntil = TimeSpan.MinValue;
    private TimeSpan? _gapUntil;
    private TimeSpan? _a;
    private TimeSpan? _b;
    private bool _enabled;
    private double _tickMs = 15;

    public LoopEngine(ILoopPlayer player, Func<TimeSpan> clock)
    {
        _player = player;
        _clock = clock;
    }

    public TimeSpan? A => _a;
    public TimeSpan? B => _b;

    /// <summary>Both points set and at least <see cref="MinLength"/> apart.</summary>
    public bool HasPart => _a is { } a && _b is { } b && b - a >= MinLength;

    /// <summary>Looping is on (only does anything with <see cref="HasPart"/>).</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;
            Passes = 0;
            if (!value && _gapUntil is not null)
            {
                _gapUntil = null;
                _player.Play();
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>How many times the part plays before the song plays on; 0 = until turned off.</summary>
    public int Count { get; set; }

    /// <summary>Passes through B since the loop was turned on (or the part changed).</summary>
    public int Passes { get; private set; }

    /// <summary>A pause at B before each repeat, 0 to 2 s.</summary>
    public TimeSpan Gap { get; set; }

    /// <summary>True while the gap before a repeat runs.</summary>
    public bool InGap => _gapUntil is not null;

    /// <summary>Jumps back made (tests and the measurement).</summary>
    public int Loops { get; private set; }

    /// <summary>The predicted position when the last jump was made, minus B: the loop-back timing
    /// error as the plugin sees it (negative = early).</summary>
    public TimeSpan LastJumpError { get; private set; }

    /// <summary>Average time between <see cref="Tick"/>s, in ms (the timer's real rate).</summary>
    public double TickMs => _tickMs;

    /// <summary>A, B, the loop or the count changed.</summary>
    public event EventHandler? Changed;
    /// <summary>The count was reached: the loop turned itself off and the song plays on.</summary>
    public event EventHandler? Finished;

    /// <summary>Where playback is now, between the host's position steps.</summary>
    public TimeSpan Predicted
    {
        get
        {
            Observe();
            return Predict(_clock());
        }
    }

    /// <summary>Sets A to where playback is (B moves out of the way if it is no longer after A).</summary>
    public void CaptureA() => SetA(Predicted);

    public void CaptureB() => SetB(Predicted);

    public void SetA(TimeSpan at)
    {
        at = Clamp(at);
        _a = at;
        if (_b is { } b && b - at < MinLength) _b = null;
        Passes = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetB(TimeSpan at)
    {
        at = Clamp(at);
        if (_a is { } a && at - a < MinLength)
        {
            // B before A: the two swap, so a B pressed first still makes a part.
            if (a - at >= MinLength) { _b = a; _a = at; }
            else return;
        }
        else _b = at;
        Passes = 0;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Sets both points (a saved loop). False when they do not make a part.</summary>
    public bool SetPart(TimeSpan a, TimeSpan b)
    {
        a = Clamp(a);
        b = Clamp(b);
        if (b - a < MinLength) return false;
        _a = a;
        _b = b;
        Passes = 0;
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    /// <summary>Moves A (or B) by <paramref name="delta"/>, keeping A before B by <see cref="MinLength"/>.</summary>
    public void Nudge(bool a, TimeSpan delta)
    {
        if (a && _a is { } pa)
        {
            var to = Clamp(pa + delta);
            if (_b is { } b && b - to < MinLength) to = b - MinLength;
            _a = to < TimeSpan.Zero ? TimeSpan.Zero : to;
        }
        else if (!a && _b is { } pb)
        {
            var to = Clamp(pb + delta);
            if (_a is { } aa && to - aa < MinLength) to = aa + MinLength;
            _b = Clamp(to);
        }
        else return;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Clears A, B and the loop (the song changed).</summary>
    public void Clear()
    {
        _a = _b = null;
        _enabled = false;
        _gapUntil = null;
        Passes = 0;
        _lastObserved = TimeSpan.MinValue;
        _settleUntil = TimeSpan.MinValue;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Seeks the song (a click on the strip), keeping the prediction in step.</summary>
    public void SeekHost(TimeSpan at) => SeekTo(Clamp(at), _clock());

    /// <summary>Jumps to A now (a saved loop was picked).</summary>
    public void JumpToA()
    {
        if (_a is not { } a) return;
        SeekTo(a, _clock());
    }

    /// <summary>
    /// Reads the host's position. A new value re-anchors the prediction at the moment it most
    /// likely arrived (halfway between this poll and the last one, so the error is centred).
    /// </summary>
    public void Observe()
    {
        var now = _clock();
        var pos = _player.Position;
        var playing = _player.IsPlaying;
        if (playing != _wasPlaying)
        {
            // Pause/play: the prediction holds still from here (or starts moving again).
            _anchorPos = Predict(now);
            _anchorTime = now;
            _wasPlaying = playing;
        }
        if (pos != _lastObserved)
        {
            var seenAt = _lastPoll == TimeSpan.MinValue ? now : now - (now - _lastPoll) / 2;
            _lastObserved = pos;
            if (now < _settleUntil)
            {
                // Right after a jump: the host's echo of the seek target is not news, and a report
                // from before the jump (far from A) is stale.
                var sinceJump = now - (_settleUntil - SettleWindow);
                var stale = pos < _seekTarget - TimeSpan.FromMilliseconds(300) || pos > _seekTarget + sinceJump + TimeSpan.FromMilliseconds(500);
                var echo = (pos - _seekTarget).Duration() < TimeSpan.FromMilliseconds(20);
                if (stale || echo) { _lastPoll = now; return; }
            }
            _anchorPos = pos;
            _anchorTime = playing ? seenAt : now;
        }
        _lastPoll = now;
    }

    /// <summary>One timer tick (~15 ms): read the position, end a gap, or loop back at B.</summary>
    public void Tick()
    {
        var now = _clock();
        if (_lastTick != TimeSpan.MinValue)
        {
            var dt = (now - _lastTick).TotalMilliseconds;
            if (dt > 0 && dt < 200) _tickMs += (dt - _tickMs) * 0.1;
        }
        _lastTick = now;
        Observe();
        if (_gapUntil is { } until)
        {
            if (now < until) return;
            _gapUntil = null;
            _anchorTime = now;
            _player.Play();
            _wasPlaying = true;
            return;
        }
        if (!_enabled || !_wasPlaying || _a is not { } a || _b is not { } b || b - a < MinLength) return;
        var p = Predict(now);
        var lead = TimeSpan.FromMilliseconds(Math.Clamp(_tickMs / 2, 0.5, 25));
        if (p + lead < b || p > b + CrossWindow || p < a - TimeSpan.FromMilliseconds(250)) return;

        Passes++;
        LastJumpError = p - b;
        if (Count > 0 && Passes >= Count)
        {
            // Played the part Count times: the song plays on.
            _enabled = false;
            Passes = 0;
            Changed?.Invoke(this, EventArgs.Empty);
            Finished?.Invoke(this, EventArgs.Empty);
            return;
        }
        Loops++;
        if (Gap > TimeSpan.Zero)
        {
            _player.Pause();
            _wasPlaying = false;
            SeekTo(a, now);
            _gapUntil = now + Gap;
        }
        else SeekTo(a, now);
    }

    private void SeekTo(TimeSpan a, TimeSpan now)
    {
        _player.Seek(a);
        _seekTarget = a;
        _anchorPos = a;
        _anchorTime = now;
        _settleUntil = now + SettleWindow;
        _lastObserved = _player.Position;
    }

    private TimeSpan Predict(TimeSpan now)
    {
        if (!_wasPlaying) return _anchorPos;
        var run = now - _anchorTime;
        if (run < TimeSpan.Zero) run = TimeSpan.Zero;
        if (run > MaxExtrapolation) run = MaxExtrapolation;
        return _anchorPos + run;
    }

    private TimeSpan Clamp(TimeSpan t)
    {
        if (t < TimeSpan.Zero) return TimeSpan.Zero;
        var d = _player.Duration;
        return d > TimeSpan.Zero && t > d ? d : t;
    }
}
