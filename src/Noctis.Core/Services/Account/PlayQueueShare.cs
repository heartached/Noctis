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
