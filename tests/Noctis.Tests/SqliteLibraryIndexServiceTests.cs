using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class SqliteLibraryIndexServiceTests
{
    [Fact]
    public async Task MigrateAndUpsert_Tracks_UpdatesCount()
    {
        using var persistence = new TestPersistenceService();
        var index = new SqliteLibraryIndexService(persistence);

        var id = Guid.NewGuid();
        var track = new Track
        {
            Id = id,
            FilePath = @"C:\Music\song.flac",
            Title = "Song",
            Artist = "Artist",
            Album = "Album",
            AlbumArtist = "Artist",
            Genre = "Pop",
            Duration = TimeSpan.FromSeconds(123),
            FileSize = 1000,
            LastModified = DateTime.UtcNow,
            DateAdded = DateTime.UtcNow,
            SourceType = SourceType.Local
        };

        await index.InitializeAsync();
        Assert.Equal(0, await index.CountAsync());

        await index.MigrateFromJsonIfEmptyAsync(new[] { track });
        Assert.Equal(1, await index.CountAsync());

        track.Title = "Song Updated";
        await index.UpsertTracksAsync(new[] { track });
        Assert.Equal(1, await index.CountAsync());

        await index.DeleteTracksAsync(new[] { id });
        Assert.Equal(0, await index.CountAsync());
    }
}
