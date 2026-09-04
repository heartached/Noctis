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

    /// <summary>Podium tints for the rank numeral (Home, Albums artist rows, Artist page):
    /// #1 gold, #2 silver, #3 bronze; everything below stays the dim default.</summary>
    public bool IsTop => Rank == 1;
    public bool IsSecond => Rank == 2;
    public bool IsThird => Rank == 3;
}
