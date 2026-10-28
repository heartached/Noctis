namespace Noctis.Models;

/// <summary>
/// "Pick Up on Computer": the queue a phone was playing, as it told the desktop (the Subsonic
/// savePlayQueue call). Only the desktop's own songs travel, by their desktop ids.
/// </summary>
public sealed class PhoneHandoff
{
    /// <summary>The most songs one handoff carries; the rest of a longer queue is cut.</summary>
    public const int MaxTracks = 1000;

    /// <summary>The longest a phone's "how long ago I captured this" is believed.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(7);

    /// <summary>The whole queue in order: what played, the song playing, what is next.</summary>
    public List<Guid> TrackIds { get; set; } = new();

    /// <summary>The song playing (or paused); always one of <see cref="TrackIds"/>.</summary>
    public Guid CurrentId { get; set; }

    public long PositionMs { get; set; }

    /// <summary>When the phone's state was captured, on the desktop's clock.</summary>
    public DateTime SavedUtc { get; set; }

    /// <summary>The phone's name, for the prompt and the log.</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>
    /// The call's parameters made a handoff, or null when there is nothing to hand over. The
    /// capture time is the desktop's clock minus <paramref name="ageMs"/> (the phone says how long
    /// ago, not what its clock read: the two clocks differ), never in the future. A current song
    /// not in the list starts the list, at its start.
    /// </summary>
    public static PhoneHandoff? FromWire(IEnumerable<Guid> ids, Guid? current, long positionMs, long ageMs,
        DateTime nowUtc, string deviceName)
    {
        var list = ids.Where(g => g != Guid.Empty).Take(MaxTracks).ToList();
        if (list.Count == 0) return null;
        var index = current is { } c ? list.IndexOf(c) : -1;
        if (index < 0)
        {
            index = 0;
            positionMs = 0;
        }
        var age = TimeSpan.FromMilliseconds(Math.Clamp(ageMs, 0, (long)MaxAge.TotalMilliseconds));
        return new PhoneHandoff
        {
            TrackIds = list,
            CurrentId = list[index],
            PositionMs = Math.Max(0, positionMs),
            SavedUtc = nowUtc - age,
            DeviceName = deviceName,
        };
    }
}
