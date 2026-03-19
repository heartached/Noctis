namespace Noctis.Models;

/// <summary>
/// Lightweight DTO carrying only the fields needed for a Discord Rich Presence update.
/// </summary>
public record DiscordPresenceTrack(
    string Title,
    string Artist,
    string? Album,
    TimeSpan Duration,
    string? ArtworkUrl = null);
