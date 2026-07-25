using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Manages Discord Rich Presence for the current playback session.
/// All methods are safe to call from any thread and never throw.
/// </summary>
public interface IDiscordPresenceService : IDisposable
{
    /// <summary>Whether the Discord RPC client is currently connected.</summary>
    bool IsConnected { get; }

    /// <summary>
    /// Predicate the background reconnect consults; when it returns false the retry loop
    /// stops. Set by the owner so retries end as soon as the user disables the feature.
    /// </summary>
    Func<bool>? IsEnabled { get; set; }

    /// <summary>Initializes and connects the Discord RPC client.</summary>
    Task<bool> ConnectAsync(CancellationToken ct = default);

    /// <summary>Disconnects and tears down the Discord RPC client.</summary>
    Task DisconnectAsync();

    /// <summary>Publishes or updates the Rich Presence with the given track info.</summary>
    Task UpdateAsync(DiscordPresenceTrack track, TimeSpan position, TimeSpan? duration, bool isPlaying);

    /// <summary>Clears the current Rich Presence (shows no activity).</summary>
    Task ClearAsync();

    /// <summary>
    /// Forgets any cached artwork key. Call when the artwork relay drops — its reconnect
    /// rotates the clientId, so URLs minted under the old one no longer resolve.
    /// </summary>
    void InvalidateArtworkCache();
}
