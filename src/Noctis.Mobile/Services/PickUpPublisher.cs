using Noctis.Services;

namespace Noctis.Mobile.Services;

/// <summary>The phone's queue as the computer's "Pick Up on Computer" takes it: the desktop songs
/// only (desktop ids), the playing one first, then what is next.</summary>
public sealed record PlayQueueShare(IReadOnlyList<Guid> Ids, Guid Current, long PositionMs)
{
    /// <summary>The same queue at the same spot (a second of play apart counts as the same).</summary>
    public bool SameSpot(PlayQueueShare? other) =>
        other != null && other.Current == Current && Math.Abs(other.PositionMs - PositionMs) < 1000
        && other.Ids.SequenceEqual(Ids);
}

/// <summary>
/// Tells the computer where the phone is while "Pick Up on Computer" is on: at once when asked
/// (<see cref="Flush"/>: a song or play/pause change, a finished sync) and every
/// <see cref="Interval"/> while playing (<see cref="Tick"/>, from the position updates). One send
/// runs at a time; a request that arrives meanwhile sends again when it ends. A failed send is
/// dropped (the computer is off, another network): the next tick tries again.
/// </summary>
public sealed class PickUpPublisher
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(30);

    private readonly Func<bool> _enabled;
    private readonly Func<PlayQueueShare?> _capture;
    private readonly Func<PlayQueueShare, Task> _send;
    private readonly Func<DateTime> _utcNow;
    private readonly object _gate = new();
    private DateTime _lastUtc = DateTime.MinValue;
    private PlayQueueShare? _lastSent;
    private bool _busy;
    private bool _again;

    public PickUpPublisher(Func<bool> enabled, Func<PlayQueueShare?> capture, Func<PlayQueueShare, Task> send,
        Func<DateTime>? utcNow = null)
    {
        _enabled = enabled;
        _capture = capture;
        _send = send;
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
    }

    /// <summary>The tail of the last send; tests await it.</summary>
    internal Task Pending { get; private set; } = Task.CompletedTask;

    /// <summary>A position update while playing: sends when <see cref="Interval"/> has passed.</summary>
    public void Tick()
    {
        if (!_enabled()) return;
        lock (_gate)
        {
            if (_utcNow() - _lastUtc < Interval) return;
        }
        Flush();
    }

    /// <summary>Sends the current queue and spot now, unless it is the one already sent.</summary>
    public void Flush()
    {
        if (!_enabled()) return;
        lock (_gate)
        {
            if (_busy)
            {
                _again = true;
                return;
            }
            var share = _capture();
            if (share == null || share.SameSpot(_lastSent)) return;
            _busy = true;
            _lastUtc = _utcNow();
            Pending = SendAsync(share);
        }
    }

    private async Task SendAsync(PlayQueueShare share)
    {
        try
        {
            await _send(share).ConfigureAwait(false);
            lock (_gate) _lastSent = share;
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"Pick Up send failed: {ex.Message}");
        }
        bool again;
        lock (_gate)
        {
            _busy = false;
            again = _again;
            _again = false;
        }
        if (again) Flush();
    }
}
