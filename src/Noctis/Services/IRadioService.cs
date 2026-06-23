using Noctis.Models;

namespace Noctis.Services;

public interface IRadioService
{
    /// <summary>Returns up to count tracks similar to seed from library, ranked by
    /// genre/artist/year/BPM/key similarity. Excludes seed, disliked, snoozed, and ids in exclude.</summary>
    IReadOnlyList<Track> BuildSimilar(Track seed, IEnumerable<Track> library, int count, ISet<Guid> exclude);
}
