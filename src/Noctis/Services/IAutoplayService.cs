using Noctis.Models;

namespace Noctis.Services;

public interface IAutoplayService
{
    /// <summary>Picks up to count library tracks similar to seed for Autoplay: same
    /// genre first, same primary artist only when the genre tier yields nothing.
    /// Excludes seed, disliked, snoozed, and ids in exclude. Returns an empty list
    /// when neither tier has candidates — never degrades to random library picks.</summary>
    IReadOnlyList<Track> PickSimilar(Track seed, IReadOnlyList<Track> library, int count, ISet<Guid> exclude);
}
