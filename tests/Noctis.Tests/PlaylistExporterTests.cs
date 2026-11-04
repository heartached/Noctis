using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Export Playlist from the playlist page's "…" menu (Discord, DendyAly 11-04): the file
/// holds the whole playlist in its saved order, not what the search box or sort shows.
/// </summary>
public class PlaylistExporterTests
{
    [Fact]
    public void TracksOf_ManualPlaylist_KeepsTheSavedOrderAndSkipsMissingSongs()
    {
        var library = new FakeLibraryService();
        var a = new Track { Title = "A", FilePath = @"C:\m\a.mp3" };
        var b = new Track { Title = "B", FilePath = @"C:\m\b.mp3" };
        var c = new Track { Title = "C", FilePath = @"C:\m\c.mp3" };
        library.TrackList.AddRange(new[] { a, b, c });
        var playlist = new Playlist { Name = "Mix" };
        playlist.TrackIds.AddRange(new[] { c.Id, Guid.NewGuid(), a.Id, b.Id });

        var tracks = PlaylistExporter.TracksOf(playlist, library);

        Assert.Equal(new[] { "C", "A", "B" }, tracks.Select(t => t.Title));
    }

    [Theory]
    [InlineData("Road / Trip: 2026?", "Road _ Trip_ 2026_")]
    [InlineData("   ", "Playlist")]
    public void SanitizeFileName_ReplacesCharactersAFileNameCannotHold(string name, string expected)
    {
        var result = PlaylistExporter.SanitizeFileName(name);
        if (OperatingSystem.IsWindows()) Assert.Equal(expected, result);
        else Assert.DoesNotContain('/', result);
    }
}
