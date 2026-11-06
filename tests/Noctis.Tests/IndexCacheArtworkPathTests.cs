using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

[Collection("MetadataServiceStatics")]
public class IndexCacheArtworkPathTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "IndexCacheArt", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    /// <summary>
    /// indexes.json stored each album's cover as a full path, and the launch fast path restored
    /// it unchecked: with the data folder moved (or copied to another computer), every album
    /// showed no cover until something forced a rebuild.
    /// </summary>
    [Fact]
    public async Task IndexCacheRestore_PointsCoversAtTheCurrentDataFolder()
    {
        var oldRoot = Path.Combine(_root, "old");
        var newRoot = Path.Combine(_root, "new");
        var albumId = Guid.NewGuid();
        var tracks = Enumerable.Range(1, 2).Select(i => new Track
        {
            Id = Guid.NewGuid(),
            FilePath = Path.Combine(_root, "music", $"{i}.mp3"),
            Title = $"Song {i}",
            Artist = "Artist",
            AlbumArtist = "Artist",
            Album = "Album",
            AlbumId = albumId,
            TrackNumber = i,
        }).ToList();

        var old = new PersistenceService(oldRoot);
        await old.SaveLibraryAsync(tracks);
        old.SaveArtwork(albumId, new byte[] { 1, 2, 3 });
        var first = new LibraryService(new MetadataService(), old, new SqliteLibraryIndexService(old), new NoctisAccountLibraryTests.NoOpAudit());
        await first.LoadAsync();
        await first.BackgroundInit;
        Assert.Equal(old.GetArtworkPath(albumId), first.Albums.Single().ArtworkPath);
        var indexes = Path.Combine(oldRoot, "indexes.json");
        for (var i = 0; i < 50 && !File.Exists(indexes); i++) await Task.Delay(100);
        Assert.True(File.Exists(indexes));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        // The folder moves: library, index cache and covers, nothing else.
        foreach (var name in new[] { "library.json", "indexes.json" })
            Write(Path.Combine(newRoot, name), File.ReadAllText(Path.Combine(oldRoot, name)));
        Directory.CreateDirectory(Path.Combine(newRoot, "artwork"));
        File.Copy(old.GetArtworkPath(albumId), Path.Combine(newRoot, "artwork", $"{albumId}.jpg"));
        // (The old folder stays: the first library's background backfills still write there.)

        var moved = new PersistenceService(newRoot);
        var second = new LibraryService(new MetadataService(), moved, new SqliteLibraryIndexService(moved), new NoctisAccountLibraryTests.NoOpAudit());
        await second.LoadAsync();
        await second.BackgroundInit;

        var album = second.Albums.Single();
        Assert.Equal(moved.GetArtworkPath(albumId), album.ArtworkPath);
        Assert.True(File.Exists(album.ArtworkPath));
        Assert.All(album.Tracks, t => Assert.Equal(album.ArtworkPath, t.AlbumArtworkPath));
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }
}
