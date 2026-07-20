namespace Noctis.Models;

/// <summary>
/// Display row for the Home tab's ranked "Most Listened To" list:
/// a track plus its 1-based rank by play count.
/// </summary>
public sealed class TopSongRow
{
    public required Track Track { get; init; }

    /// <summary>1-based rank by play count.</summary>
    public int Rank { get; init; }

    /// <summary>True for the #1 track — used to tint its rank numeral with the accent.</summary>
    public bool IsTop => Rank == 1;
}
