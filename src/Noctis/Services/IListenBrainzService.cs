using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Lightweight scrobbling client for ListenBrainz (https://listenbrainz.org).
/// Mirrors the surface of <see cref="ILastFmService"/> so the main window can
/// fan listens out to both providers in parallel without provider-specific code.
/// </summary>
public interface IListenBrainzService
{
    bool IsAuthenticated { get; }
    string? Username { get; }

    /// <summary>Stores the user token (does NOT validate it). Pair with <see cref="ValidateTokenAsync"/>.</summary>
    void Configure(string? userToken);

    /// <summary>POSTs the token to /1/validate-token. Returns the resolved user name on success, null otherwise.</summary>
    Task<string?> ValidateTokenAsync(CancellationToken ct = default);

    /// <summary>Clears in-memory auth state. Settings layer is responsible for persisting the empty token.</summary>
    void Logout();

    /// <summary>Submits a "listen" once playback completed enough of the track (≥50% or ≥4 minutes).</summary>
    Task ScrobbleAsync(Track track, DateTime startedAt);

    /// <summary>Submits a "playing_now" ping at track start. Best-effort, fire-and-forget.</summary>
    Task UpdateNowPlayingAsync(Track track);
}
