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

    /// <summary>Normalized API root every request goes to (official server unless overridden).</summary>
    string ApiUrl { get; }

    /// <summary>Why the last <see cref="ValidateTokenAsync"/> returned null.</summary>
    ListenBrainzValidationError LastValidationError { get; }

    /// <summary>Stores the user token (does NOT validate it). Pair with <see cref="ValidateTokenAsync"/>.</summary>
    void Configure(string? userToken);

    /// <summary>Points the client at a ListenBrainz-compatible server (Koito, Maloja, …). Blank or invalid = official server.</summary>
    void SetApiUrl(string? apiUrl);

    /// <summary>GETs /1/validate-token. Returns the resolved user name on success, null otherwise (see <see cref="LastValidationError"/>).</summary>
    Task<string?> ValidateTokenAsync(CancellationToken ct = default);

    /// <summary>Clears in-memory auth state. Settings layer is responsible for persisting the empty token.</summary>
    void Logout();

    /// <summary>Submits a "listen" once playback completed enough of the track (≥50% or ≥4 minutes).</summary>
    Task ScrobbleAsync(Track track, DateTime startedAt);

    /// <summary>Submits a "playing_now" ping at track start. Best-effort, fire-and-forget.</summary>
    Task UpdateNowPlayingAsync(Track track);
}

public enum ListenBrainzValidationError
{
    None,
    /// <summary>The server answered but rejected the token.</summary>
    InvalidToken,
    /// <summary>Network failure, timeout or a 5xx from the server.</summary>
    Unreachable,
    /// <summary>The URL answered, but not with a ListenBrainz validate-token response.</summary>
    NotCompatible,
}
