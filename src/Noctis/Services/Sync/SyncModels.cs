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

/// <summary>Per-track user state as it travels between devices.</summary>
public sealed record TrackSyncState(
    [property: JsonPropertyName("favorite")] bool Favorite,
    [property: JsonPropertyName("rating")] int Rating,
    [property: JsonPropertyName("disliked")] bool Disliked,
    [property: JsonPropertyName("playCount")] int PlayCount,
    [property: JsonPropertyName("lastPlayed")] DateTime? LastPlayed,
    [property: JsonPropertyName("favoritedAt")] DateTime? FavoritedAt)
{
    public static TrackSyncState From(Track t) =>
        new(t.IsFavorite, t.Rating, t.IsDisliked, t.PlayCount, t.LastPlayed, t.FavoritedAt);
}

/// <summary>A playlist as it travels between devices. <see cref="Deleted"/> is the tombstone.</summary>
public sealed record PlaylistSyncState(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("description")] string Description,
    [property: JsonPropertyName("color")] string Color,
    [property: JsonPropertyName("trackIds")] List<Guid> TrackIds,
    [property: JsonPropertyName("modifiedAt")] DateTime ModifiedAt,
    [property: JsonPropertyName("deleted")] bool Deleted)
{
    public static PlaylistSyncState From(Playlist p) =>
        new(p.Name, p.Description, p.Color, p.TrackIds.ToList(), p.ModifiedAt, Deleted: false);

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
