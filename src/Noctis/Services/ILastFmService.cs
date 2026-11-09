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

    /// <summary>GitHub #197: sends up to <see cref="ScrobbleQueue.MaxBatch"/> queued plays in one
    /// track.scrobble call and says how it went, so the queue knows what to keep. The default (test
    /// doubles) sends nothing and keeps them.</summary>
    Task<ScrobbleSendResult> SendScrobblesAsync(IReadOnlyList<ScrobbleEntry> plays, CancellationToken ct = default)
        => Task.FromResult(ScrobbleSendResult.Retry);
    Task UpdateNowPlayingAsync(Track track);
    Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default);
    Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default);
    Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default);
    Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default);

    /// <summary>Forgets every cached album description and user override, in memory and on disk
    /// (Settings › Reset Everything). The default does nothing (test doubles).</summary>
    Task ClearAlbumDescriptionsAsync() => Task.CompletedTask;
}
