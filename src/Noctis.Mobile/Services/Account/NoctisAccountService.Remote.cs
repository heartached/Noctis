namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The remote for the computer's own player (Account › Remote): straight calls through the
/// signed-in client, nothing cached and nothing running between them. The page that asks decides
/// how often (only while it shows).
/// </summary>
public sealed partial class NoctisAccountService
{
    public Task<ComputerPlayback> GetComputerPlaybackAsync(bool withQueue, CancellationToken ct = default)
        => GetClient().GetComputerPlaybackAsync(withQueue, ct);

    public Task<ComputerPlayback> ControlComputerAsync(ComputerCommand command, bool withQueue, CancellationToken ct = default)
        => GetClient().ControlComputerAsync(command, withQueue, ct);

    public async Task<string?> GetComputerCoverAsync(Guid albumId, CancellationToken ct = default)
    {
        // The sync keeps the computer's covers by album id: a song from the synced catalog has its
        // cover here already, and one fetched now lands where the next sync would put it.
        var path = _persistence.GetArtworkPath(albumId);
        if (File.Exists(path)) return path;
        if (FreeSpace(Path.GetDirectoryName(path)!) < StorageReserveBytes) return null;
        try
        {
            return await GetClient().DownloadCoverAsync(albumId, path, ct).ConfigureAwait(false) ? path : null;
        }
        catch (NoctisServerException)
        {
            return null; // no cover is not worth a message: the remote shows the placeholder
        }
    }
}
