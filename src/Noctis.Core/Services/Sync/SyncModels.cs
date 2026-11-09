using System.Text.Json;
using System.Text.Json.Serialization;
using Noctis.Models;

namespace Noctis.Services.Sync;

/// <summary>Item kinds the sync store understands. Strings on the wire so the mobile app can add kinds later.</summary>
public static class SyncKinds
{
    public const string Track = "track";
    public const string Playlist = "playlist";
}

/// <summary>One synced item: a JSON payload for (kind, id) plus the last-writer stamp and the store's change sequence.</summary>
public sealed record SyncItem(string Kind, string Id, string Payload, DateTime UpdatedUtc, string Device, long Seq);

/// <summary>A device that has pushed or pulled through this server.</summary>
public sealed record SyncDevice(string Id, string Name, DateTime LastSeenUtc, long LastSeq);

/// <summary>
/// Per-track user state as it travels between devices. <see cref="SnoozedUntil"/> (Snooze for a
/// Month) is optional and backwards compatible: absent (null) means "this sender says nothing
/// about snooze" (an older app, or a song never snoozed) and leaves the receiver's as it is; a
/// date is the snooze's end, and a date already past means "not snoozed"
/// (<see cref="NotSnoozed"/>), which is how an Unsnooze travels. Older apps ignore the field.
/// </summary>
public sealed record TrackSyncState(
    [property: JsonPropertyName("favorite")] bool Favorite,
    [property: JsonPropertyName("rating")] int Rating,
    [property: JsonPropertyName("disliked")] bool Disliked,
    [property: JsonPropertyName("playCount")] int PlayCount,
    [property: JsonPropertyName("lastPlayed")] DateTime? LastPlayed,
    [property: JsonPropertyName("favoritedAt")] DateTime? FavoritedAt,
    [property: JsonPropertyName("snoozedUntil")] DateTime? SnoozedUntil = null)
{
    /// <summary>The wire's "not snoozed": a snooze that ended before any real one began.</summary>
    public static readonly DateTime NotSnoozed = DateTime.UnixEpoch;

    public static TrackSyncState From(Track t) =>
        new(t.IsFavorite, t.Rating, t.IsDisliked, t.PlayCount, t.LastPlayed, t.FavoritedAt, Utc(t.SnoozedUntil));

    /// <summary>
    /// <see cref="From"/> against what the other side last saw (<paramref name="previous"/>): a
    /// song whose snooze was cleared here (SnoozedUntil null after Unsnooze) but that went out
    /// snoozed says <see cref="NotSnoozed"/>, so the unsnooze reaches the other device instead of
    /// reading as "says nothing". A song never sent snoozed stays without the field, so its
    /// payload is the one older apps wrote and nothing is re-sent for it.
    /// </summary>
    public static TrackSyncState From(Track t, TrackSyncState? previous)
    {
        var state = From(t);
        return state.SnoozedUntil == null && previous?.SnoozedUntil != null
            ? state with { SnoozedUntil = NotSnoozed }
            : state;
    }

    /// <summary>The snooze end as UTC (Track.SnoozedUntil is UTC by convention; a value read as
    /// local or unspecified is converted or marked so the wire always says "Z").</summary>
    public static DateTime? Utc(DateTime? value) => value switch
    {
        null => null,
        { Kind: DateTimeKind.Local } v => v.ToUniversalTime(),
        { Kind: DateTimeKind.Unspecified } v => DateTime.SpecifyKind(v, DateTimeKind.Utc),
        var v => v,
    };
}

/// <summary>A playlist as it travels between devices. <see cref="Deleted"/> is the tombstone.
/// <see cref="Cover"/> is the custom cover (see <see cref="PlaylistCovers"/>): "" none, a hash that
/// picture, null (absent on the wire: an older app) says nothing and keeps the receiver's.</summary>
public sealed record PlaylistSyncState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("trackIds")] List<Guid> TrackIds,
    [property: JsonPropertyName("modifiedAt")] DateTime ModifiedAt,
    [property: JsonPropertyName("deleted")] bool Deleted,
    [property: JsonPropertyName("cover")] string? Cover = null)
{
    public static PlaylistSyncState From(Playlist p) =>
        new(p.Name, p.Description, p.Color, p.TrackIds.ToList(), p.ModifiedAt, Deleted: false, PlaylistCovers.CoverOf(p.CoverArtPath));

    public static PlaylistSyncState Tombstone(Guid id, DateTime at) =>
        new(string.Empty, string.Empty, string.Empty, new List<Guid>(), at, Deleted: true);
}

public static class SyncJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T? Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<T>(json, Options); }
        catch (JsonException) { return default; }
    }
}
