namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>An artist's home: <see cref="Units"/> wide, on <see cref="Floor"/> from the top, starting at <see cref="Column"/>.</summary>
public sealed record Apartment(string Artist, IReadOnlyList<TownAlbum> Albums, int Units, int Floor, int Column, string Tier);

public sealed record Building(IReadOnlyList<Apartment> Apartments, int UnitsPerFloor, int Floors);

/// <summary>
/// The Apartments building: every artist gets an apartment, and the more albums they have the
/// bigger it is (Jafezy: "the more you add the bigger the apartment becomes"). Apartments are
/// packed into floors left to right, biggest households on the top floors.
/// </summary>
public static class Apartments
{
    public const int DefaultUnitsPerFloor = 12;

    /// <summary>Width in units for an artist with <paramref name="albums"/> albums: 1, 2, 3, 4,
    /// then 6 for the penthouse.</summary>
    public static int UnitsFor(int albums) => albums switch
    {
        <= 1 => 1,
        <= 3 => 2,
        <= 6 => 3,
        <= 11 => 4,
        _ => 6,
    };

    public static string TierFor(int albums) => albums switch
    {
        <= 1 => "Studio",
        <= 3 => "Flat",
        <= 6 => "Family home",
        <= 11 => "Loft",
        _ => "Penthouse",
    };

    /// <summary>How many residents fit in a room of <paramref name="units"/> (drawn as a little grid).</summary>
    public static int Capacity(int units) => units * 4;

    public static Building Build(IReadOnlyList<TownAlbum> town, int unitsPerFloor = DefaultUnitsPerFloor)
    {
        unitsPerFloor = Math.Max(UnitsFor(int.MaxValue), unitsPerFloor);
        var households = town
            .GroupBy(a => a.Artist.Length == 0 ? "Unknown artist" : a.Artist, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Artist: g.First().Artist.Length == 0 ? "Unknown artist" : g.First().Artist,
                          Albums: (IReadOnlyList<TownAlbum>)g.OrderBy(a => a.Year == 0 ? int.MaxValue : a.Year).ThenBy(a => a.Title, StringComparer.OrdinalIgnoreCase).ToList()))
            .OrderByDescending(h => h.Albums.Count)
            .ThenBy(h => h.Artist, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = new List<Apartment>(households.Count);
        var floor = 0;
        var column = 0;
        foreach (var (artist, albums) in households)
        {
            var units = UnitsFor(albums.Count);
            if (column + units > unitsPerFloor)
            {
                floor++;
                column = 0;
            }
            result.Add(new Apartment(artist, albums, units, floor, column, TierFor(albums.Count)));
            column += units;
        }
        return new Building(result, unitsPerFloor, result.Count == 0 ? 0 : floor + 1);
    }
}
