namespace Noctis.Mobile.Services.Account;

/// <summary>
/// The computer's animated covers for Now Playing (AnimatedCoverSource decides when, and keeps
/// them in AnimatedCoverCache): a straight call through the signed-in client, as the Remote's
/// covers are. A key the computer rejected forgets the account here too, as a download does.
/// </summary>
public sealed partial class NoctisAccountService
{
    public async Task<AnimatedCoverDownload> FetchAnimatedCoverAsync(Guid albumId, string destination, string? etag, CancellationToken ct)
    {
        NoctisServerClient client;
        lock (_gate)
        {
            if (_account is null) return new AnimatedCoverDownload(AnimatedCoverFetch.None, null);
            client = GetClientLocked();
        }
        try
        {
            return await client.DownloadAnimatedCoverAsync(albumId, destination, etag, ct).ConfigureAwait(false);
        }
        catch (NoctisServerException ex) when (ex.Kind == NoctisErrorKind.SignedOut)
        {
            ForgetAccountLocally();
            throw;
        }
    }
}
