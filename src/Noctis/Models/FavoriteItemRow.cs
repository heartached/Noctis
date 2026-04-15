namespace Noctis.Models;

/// <summary>
/// A row of up to 5 favorite items for the virtualized favorites grid.
/// </summary>
public class FavoriteItemRow
{
    public List<FavoriteItem> Items { get; init; } = new();
}
