namespace Noctis.Models;

/// <summary>
/// Lightweight DTO carrying only the fields needed for a Discord Rich Presence update.
/// Duration is not here on purpose: it is passed to UpdateAsync alongside the live
/// position, because the presence timestamps need both together.
/// </summary>
public record DiscordPresenceTrack(
    string Title,
    string Artist,
    string? Album,
    string? ArtworkUrl = null,
    bool ShowAlbum = true);
