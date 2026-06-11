using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class ListenLaterServiceTests : IDisposable
{
    private readonly string _file;

    public ListenLaterServiceTests()
    {
        _file = Path.Combine(Path.GetTempPath(), $"noctis_listenlater_{Guid.NewGuid():N}.json");
    }

    public void Dispose()
    {
        try { File.Delete(_file); } catch { }
    }

    private ListenLaterService NewService() => new(_file);

    [Fact]
    public void AddTrack_Album_Artist_AllAppear()
    {
        var svc = NewService();
        svc.AddTrack(new Track { Title = "Song", Artist = "X" });
        svc.AddAlbum(new Album { Id = Guid.NewGuid(), Name = "Album", Artist = "Y" });
        svc.AddArtist("Nightwish");

        Assert.Equal(3, svc.Items.Count);
        Assert.True(svc.ContainsArtist("nightwish")); // case-insensitive
    }

    [Fact]
    public void DuplicateAdds_AreIgnored()
    {
        var svc = NewService();
        var track = new Track { Title = "Song" };
        svc.AddTrack(track);
        svc.AddTrack(track);
        svc.AddArtist("Epica");
        svc.AddArtist("EPICA");

        Assert.Equal(2, svc.Items.Count);
    }

    [Fact]
    public void Remove_DeletesOnlyThatItem()
    {
        var svc = NewService();
        svc.AddArtist("A");
        svc.AddArtist("B");
        var toRemove = svc.Items.First(i => i.Name == "A");

        svc.Remove(toRemove.Id);

        Assert.Single(svc.Items);
        Assert.False(svc.ContainsArtist("A"));
        Assert.True(svc.ContainsArtist("B"));
    }

    [Fact]
    public void Changed_FiresOnMutations()
    {
        var svc = NewService();
        int fired = 0;
        svc.Changed += (_, _) => fired++;

        svc.AddArtist("A");   // +1
        svc.AddArtist("A");   // duplicate: no event
        svc.Clear();          // +1
        svc.Clear();          // already empty: no event

        Assert.Equal(2, fired);
    }

    [Fact]
    public void Items_PersistAcrossInstances()
    {
        var first = NewService();
        var track = new Track { Title = "Persisted", Artist = "X" };
        first.AddTrack(track);
        first.FlushForTests();

        var second = NewService();
        Assert.Single(second.Items);
        Assert.Equal("Persisted", second.Items[0].Name);
        Assert.True(second.ContainsTrack(track.Id));
    }
}
