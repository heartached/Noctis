namespace Noctis.Models;

/// <summary>
/// A row of up to 6 albums for the virtualized album grid.
/// The outer ListBox virtualizes rows; each row uses a non-virtualizing
/// UniformGrid to lay out its albums horizontally.
/// </summary>
public class AlbumRow
{
    public List<Album> Albums { get; init; } = new();
}
