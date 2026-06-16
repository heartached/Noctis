using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Noctis.Services;

/// <summary>
/// Album-artwork search surface extracted from <see cref="ITunesArtworkService"/> so album-art
/// (and animated-artwork) lookups can be consumed and unit-tested against a small interface.
/// </summary>
public interface IAlbumArtworkSearch
{
    Task<IReadOnlyList<ITunesArtworkService.ArtworkCandidate>> SearchAlbumsAsync(
        string artist, string album, int limit = 8, CancellationToken ct = default);

    Task<IReadOnlyList<ITunesArtworkService.AnimatedArtworkVariant>> SearchAnimatedArtworkVariantsAsync(
        string albumViewUrl, CancellationToken ct = default);
}
