using System;

namespace Noctis.Controls;

/// <summary>
/// How a music video streamed from YouTube follows the song (11-25 follow-ups), kept apart from
/// LibVLC so it can be tested with a fake clock and a fake decoder.
/// <para>
/// Seeks: a seek over the network takes a moment before its first frame shows, and the clock
/// reads behind meanwhile; correcting on that lag made the stream stutter from seek to seek.
/// The first version answered with a fixed 2.5 s pause after every seek, which also swallowed
/// the song's own seeks: scrub, and the video stayed on the first scrub position for up to
/// 2.5 s. Now one seek is in flight at a time and it ends as soon as the decoder shows a frame
/// at its target (or after <see cref="SeekCapMs"/>, e.g. while paused, when no frame comes);
/// requests meanwhile only update the target, so a scrub costs the first seek and the final
/// one. The song's own seeks (<see cref="JumpMs"/>) always go through; drift corrections wait
/// <see cref="SettleMs"/> after a landing. A seek aims <see cref="LeadMs"/> ahead (the measured
/// time a seek takes) so the clip lands where the song is by then.
/// </para>
/// <para>
/// Failures: no frame for <see cref="StallMs"/> while the song plays (and the clip has not
/// reached its end) is a stalled stream; see also <see cref="IsPrematureEnd"/>. That detector
/// is armed only once the stream shows its first frame: until then (start-up), and after a
/// seek until a frame shows at (or after) it, the wait is <see cref="StartupCapMs"/>. VLC's
/// buffering progress counts as activity in both phases: a slow connection that is still
/// filling its cache is not a stalled one (11-25: a stream that took over 12 s to start was
/// reported as broken and cost one of the song's retries).
/// </para>
/// Thread-safe: called from the UI thread, the thread pool and VLC's threads.
/// </summary>
internal sealed class StreamSyncGate
{
    /// <summary>Drift a stream may have before it is corrected (its clock is coarser than a file's).</summary>
    public const int ToleranceMs = 900;
    /// <summary>After a seek lands, drift corrections wait this long (the song's seeks do not).</summary>
    public const int SettleMs = 600;
    /// <summary>A target this far from where the song should be by now: the song was sought.</summary>
    public const int JumpMs = 1500;
    /// <summary>A seek with no frame at its target this long is over anyway (paused, slow network).</summary>
    public const int SeekCapMs = 1500;
    /// <summary>A frame this close to the seek's target means the seek landed.</summary>
    public const int LandSlackMs = 1000;
    public const int InitialLeadMs = 250;
    public const int MaxLeadMs = 1500;
    /// <summary>No frame this long while the song plays: the stream broke.</summary>
    public const int StallMs = 12000;
    /// <summary>Start-up (no frame yet) or a seek still waiting for its first frame: the stream
    /// broke only after this long without a frame or buffering progress.</summary>
    public const int StartupCapMs = 30000;
    /// <summary>Within this of the clip's end, ending or showing no frames is normal.</summary>
    public const int EndSlackMs = 5000;

    private readonly object _lock = new();
    private readonly Func<long> _now;

    private long _wanted;
    private bool _dirty, _jumped;
    private bool _hasLast;
    private long _lastTarget, _lastTargetAt;

    private volatile bool _seeking;
    private long _seekTo, _seekAt;
    private long _settleUntil = long.MinValue;
    private long _leadMs = InitialLeadMs;
    private long _activeSince;
    // True until the stream shows its first frame, and again from a seek until a frame shows
    // after it: the long start-up cap applies instead of the stall detector.
    private bool _awaitingFrame = true;
    private float _lastBuffering = -1;

    public StreamSyncGate(Func<long>? now = null)
    {
        _now = now ?? (() => Environment.TickCount64);
        _activeSince = _now();
    }

    /// <summary>A seek is in flight (cheap; read on every decoded frame).</summary>
    public bool IsSeeking => _seeking;

    public long LeadMs { get { lock (_lock) return _leadMs; } }

    /// <summary>The song's latest position handed to <see cref="Want"/>.</summary>
    public long LastTargetMs { get { lock (_lock) return _lastTarget; } }

    /// <summary>A request is waiting and no seek is in flight.</summary>
    public bool HasWork { get { lock (_lock) return _dirty && !_seeking; } }

    /// <summary>The song is at <paramref name="targetMs"/> (each position tick; forced once at start).</summary>
    public void Want(long targetMs, bool force, bool playing, double rate)
    {
        lock (_lock)
        {
            var now = _now();
            if (force) _jumped = true;
            else if (_hasLast)
            {
                var r = double.IsNaN(rate) || rate <= 0 ? 1.0 : rate;
                var expected = _lastTarget + (playing ? (long)((now - _lastTargetAt) * r) : 0);
                if (Math.Abs(targetMs - expected) > JumpMs) _jumped = true;
            }
            _hasLast = true;
            _lastTarget = targetMs;
            _lastTargetAt = now;
            _wanted = targetMs;
            _dirty = true;
        }
    }

    /// <summary>Takes the latest request, unless none waits or a seek is still in flight
    /// (it stays waiting: the landing hands it on).</summary>
    public bool TryTake(out long targetMs, out bool jumped)
    {
        lock (_lock)
        {
            targetMs = _wanted;
            jumped = _jumped;
            if (!_dirty || _seeking) return false;
            _dirty = false;
            _jumped = false;
            return true;
        }
    }

    /// <summary>What to do for a taken request, against the decoder's state.</summary>
    public (VideoBackdrop.SyncAction Action, long SeekToMs) Plan(long targetMs, bool jumped, bool ended, long videoMs, long lengthMs, bool playing, double rate)
    {
        lock (_lock)
        {
            if (ended)
            {
                var action = VideoBackdrop.PlanSync(true, videoMs, targetMs, lengthMs, ToleranceMs, false);
                return action == VideoBackdrop.SyncAction.Restart
                    ? (action, SeekPoint(targetMs, lengthMs, playing, rate))
                    : (VideoBackdrop.SyncAction.None, 0);
            }
            if (!jumped)
            {
                if (_now() < _settleUntil) return (VideoBackdrop.SyncAction.None, 0);
                if (Math.Abs(videoMs - targetMs) <= ToleranceMs) return (VideoBackdrop.SyncAction.None, 0);
            }
            return (VideoBackdrop.SyncAction.Seek, SeekPoint(targetMs, lengthMs, playing, rate));
        }
    }

    private long SeekPoint(long targetMs, long lengthMs, bool playing, double rate)
    {
        var r = double.IsNaN(rate) || rate <= 0 ? 1.0 : rate;
        var to = targetMs + (playing ? (long)(_leadMs * r) : 0);
        if (lengthMs > 0) to = Math.Min(to, Math.Max(0, lengthMs - 500));
        return Math.Max(0, to);
    }

    public void SeekIssued(long seekToMs)
    {
        lock (_lock)
        {
            var now = _now();
            _seeking = true;
            _seekTo = seekToMs;
            _seekAt = now;
            _activeSince = now;
            _awaitingFrame = true;
        }
    }

    /// <summary>Waiting for the stream's first frame, or for the first one after a seek.</summary>
    public bool IsAwaitingFrame { get { lock (_lock) return _awaitingFrame; } }

    /// <summary>A frame was shown (every frame; feeds the stall check). Outside a seek it arms
    /// the stall detector; during one, frames can still be the old position's, so only the
    /// landing (<see cref="OnFrame"/>) or the first frame after the seek's cap does.</summary>
    public void NoteFrame()
    {
        lock (_lock)
        {
            _activeSince = _now();
            if (!_seeking) _awaitingFrame = false;
        }
    }

    /// <summary>
    /// VLC reported buffering (<paramref name="percent"/> of its cache). A changed value means
    /// data is still arriving: the stream is slow, not stalled.
    /// </summary>
    public void NoteBuffering(float percent)
    {
        lock (_lock)
        {
            if (percent == _lastBuffering) return;
            _lastBuffering = percent;
            _activeSince = _now();
        }
    }

    /// <summary>
    /// A frame was shown while a seek was in flight, the decoder's clock at
    /// <paramref name="videoMs"/>. At the target, the seek has landed: true when a newer
    /// request waits (the caller runs it now).
    /// </summary>
    public bool OnFrame(long videoMs)
    {
        lock (_lock)
        {
            var now = _now();
            _activeSince = now;
            if (!_seeking || Math.Abs(videoMs - _seekTo) > LandSlackMs) return false;
            _leadMs = Math.Clamp((_leadMs + (now - _seekAt)) / 2, 0, MaxLeadMs);
            _awaitingFrame = false;
            Land(now);
            return _dirty;
        }
    }

    /// <summary>The safety cap: a seek that never showed a frame is over. True when a request waits.</summary>
    public bool OnCapElapsed()
    {
        lock (_lock)
        {
            var now = _now();
            if (!_seeking || now - _seekAt < SeekCapMs) return false;
            Land(now);
            return _dirty;
        }
    }

    private void Land(long now)
    {
        _seeking = false;
        _settleUntil = now + SettleMs;
    }

    /// <summary>Playback resumed (unpaused, window restored): no frames while it was held, and
    /// the song's clock did not move.</summary>
    public void Resumed()
    {
        lock (_lock)
        {
            var now = _now();
            _activeSince = now;
            if (_hasLast) _lastTargetAt = now;
        }
    }

    /// <summary>Called while the song plays: no frame (or buffering progress) for
    /// <see cref="StallMs"/> before the clip's end, or <see cref="StartupCapMs"/> while the
    /// stream has not shown its first frame (start-up, or since a seek).</summary>
    public bool IsStalled(long targetMs, long lengthMs)
    {
        lock (_lock)
        {
            if (lengthMs > 0 && targetMs >= lengthMs - EndSlackMs) return false;
            return _now() - _activeSince > (_awaitingFrame ? StartupCapMs : StallMs);
        }
    }

    /// <summary>The stream ended while the song was still well before the clip's end (or its
    /// length is unknown): the connection dropped, not the end of the video.</summary>
    public static bool IsPrematureEnd(long lastTargetMs, long lengthMs) =>
        lengthMs <= 0 || lastTargetMs < lengthMs - EndSlackMs;
}
