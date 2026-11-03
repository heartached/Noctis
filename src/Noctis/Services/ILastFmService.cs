using Noctis.Models;

namespace Noctis.Services;

public interface ILastFmService
{
    bool IsAuthenticated { get; }
    string? Username { get; }

    /// <summary>Raised on a worker thread when <see cref="Configure"/>'s session check has
    /// resolved the account name.</summary>
    event EventHandler<string>? UsernameResolved { add { } remove { } }

    void Configure(string? sessionKey);
    Task<string> GetAuthUrlAsync();
    Task<bool> CompleteAuthAsync();
    string? GetSessionKey();
    void Logout();

    Task ScrobbleAsync(Track track, DateTime startedAt);
    Task UpdateNowPlayingAsync(Track track);
    Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default);
    Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default);
    Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default);
    Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default);
}
