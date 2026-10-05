using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Artists grid laid out seven 198px portrait tiles per row at any width, so below
/// ~1390px the discs drew over each other. The row now holds as many as fit (max seven).
/// </summary>
public class ArtistGridColumnsTests
{
    [Theory]
    [InlineData(1400, 7)]
    [InlineData(1386, 7)]
    [InlineData(1385, 6)]
    [InlineData(1100, 5)]
    [InlineData(600, 3)]
    [InlineData(100, 1)]
    [InlineData(5000, 7)]
    public void ComputeColumns_FitsWholeTiles(double width, int expected)
        => Assert.Equal(expected, LibraryArtistsViewModel.ComputeColumns(width));

    [Fact]
    public void BuildRows_ChunksByTheGivenColumnCount()
    {
        var artists = Enumerable.Range(0, 11).Select(i => new Artist { Name = $"A{i:00}" }).ToList();

        var rows = LibraryArtistsViewModel.BuildRows(artists, string.Empty, "name", ascending: true, columns: 5);

        Assert.Equal(new[] { 5, 5, 1 }, rows.Select(r => r.Artists.Count));
    }
}
