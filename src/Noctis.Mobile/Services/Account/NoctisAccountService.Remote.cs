namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The remote for the computer's own player (Account › Remote): straight calls through the
/// signed-in client, nothing cached and nothing running between them. The page that asks decides
/// how often (only while it shows).
/// </summary>
public sealed partial class NoctisAccountService
{
    /// <summary>An ask that cannot reach the computer tries its other addresses first, as a sync
    /// does (Discord beta 11-22: Remote kept asking a dead address until a sync or a new sign-in).
    /// Presses do not: one that did reach the old address would run twice.</summary>
    public Task<ComputerPlayback> GetComputerPlaybackAsync(bool withQueue, CancellationToken ct = default)
        => RemoteCallAsync(c => c.GetComputerPlaybackAsync(withQueue, ct), mayMove: true, ct);

    public Task<ComputerPlayback> ControlComputerAsync(ComputerCommand command, bool withQueue, CancellationToken ct = default)
        => RemoteCallAsync(c => c.ControlComputerAsync(command, withQueue, ct), mayMove: false, ct);

    /// <summary>A key the computer rejected forgets the account here too, as a sync, upload or
    /// download does: the Account page said signed in while Remote said signed out.</summary>
    private async Task<ComputerPlayback> RemoteCallAsync(Func<NoctisServerClient, Task<ComputerPlayback>> call, bool mayMove,
        CancellationToken ct)
    {
        try
        {
            try
            {
                return await call(GetClient()).ConfigureAwait(false);
            }
            catch (NoctisServerException ex) when (mayMove && ex.Kind is NoctisErrorKind.Unreachable or NoctisErrorKind.CertificateChanged)
            {
                if (!await TryOtherAddressAsync(ct).ConfigureAwait(false)) throw;
                return await call(GetClient()).ConfigureAwait(false);
            }
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.SignedOut)
        {
            ForgetAccountLocally();
            throw;
        }
    }

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
