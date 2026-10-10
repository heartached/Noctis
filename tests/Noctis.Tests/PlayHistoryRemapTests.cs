using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A track's id is a hash of its path, so Organize Files and a Metadata rename give moved
/// songs new ids. The playlists were remapped (SidebarViewModel.ApplyTrackIdRemapAsync) but
/// the play log kept the old ids, so every moved song's plays lost their album, genre and
/// length in the Statistics page and the Wrap (found 10-09: 8,410 of 8,470 logged plays in
/// the owner's dev profile pointed at ids no longer in the library).
/// </summary>
public class PlayHistoryRemapTests
{
    [Fact]
    public async Task RemapTrackIds_PointsLoggedPlaysAtTheNewIds_AndPersists()
    {
        var oldId = Guid.NewGuid();
        var newId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var history = new PlayHistoryService();
        history.RecordPlay(new Track { Id = oldId, Title = "Moved", Artist = "Artist" });
        history.RecordPlay(new Track { Id = other, Title = "Stayed", Artist = "Artist" });

        history.RemapTrackIds(new Dictionary<Guid, Guid> { [oldId] = newId });

        Assert.DoesNotContain(history.Events, e => e.TrackId == oldId);
        var moved = Assert.Single(history.Events, e => e.TrackId == newId);
        Assert.Equal("Moved", moved.Title);
        Assert.Single(history.Events, e => e.TrackId == other);

        await history.FlushAsync();
        var reloaded = new PlayHistoryService();
        Assert.DoesNotContain(reloaded.Events, e => e.TrackId == oldId);
        Assert.Contains(reloaded.Events, e => e.TrackId == newId && e.Title == "Moved");
    }
}
