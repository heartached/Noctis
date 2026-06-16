using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Noctis.Services;

/// <summary>
/// Album-artwork search surface used by <see cref="AutoMatchCoordinator"/>. Extracted from
/// <see cref="ITunesArtworkService"/> so the coordinator can be unit-tested with a fake.
/// </summary>
public interface IAlbumArtworkSearch
{
    Task<IReadOnlyList<ITunesArtworkService.ArtworkCandidate>> SearchAlbumsAsync(
        string artist, string album, int limit = 8, CancellationToken ct = default);
}
