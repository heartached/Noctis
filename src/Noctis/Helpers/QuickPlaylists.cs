using Noctis.Models;

namespace Noctis.Helpers;

/// <summary>
/// GitHub #186: the player island's Quick Add to Playlist button. Settings → Player keeps up to
/// <see cref="MaxSlots"/> favourite playlists by id, so a rename shows the new name and a deleted
/// playlist simply drops out of the pop-up.
/// </summary>
public static class QuickPlaylists
{
    public const int MaxSlots = 3;

    /// <summary>
    /// The stored slots as live playlists, in slot order: empty slots, ids of playlists that no
    /// longer exist, smart playlists (their rules fill them, an added song would never show)
    /// and repeats are left out.
    /// </summary>
    public static IReadOnlyList<Playlist> Resolve(IEnumerable<Guid>? slotIds, IEnumerable<Playlist>? playlists)
    {
        if (slotIds == null || playlists == null) return Array.Empty<Playlist>();
        var byId = new Dictionary<Guid, Playlist>();
        foreach (var playlist in playlists)
            if (playlist is { IsSmartPlaylist: false })
                byId.TryAdd(playlist.Id, playlist);

        var seen = new HashSet<Guid>();
        var result = new List<Playlist>(MaxSlots);
        foreach (var id in slotIds.Take(MaxSlots))
            if (id != Guid.Empty && seen.Add(id) && byId.TryGetValue(id, out var playlist))
                result.Add(playlist);
        return result;
    }

    /// <summary>Exactly <see cref="MaxSlots"/> slots from a stored list (a short or missing
    /// list pads with <see cref="Guid.Empty"/>, a long one is cut).</summary>
    public static Guid[] Slots(IEnumerable<Guid>? stored)
    {
        var slots = new Guid[MaxSlots];
        if (stored == null) return slots;
        var i = 0;
        foreach (var id in stored)
        {
            if (i == MaxSlots) break;
            slots[i++] = id;
        }
        return slots;
    }
}
