using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Every LibraryUpdated (scan publish, analysis pass, a save elsewhere) follows an index
/// rebuild that hands the album page a NEW Album holding the SAME Track instances. The page
/// used to Reset its track list and rebuild its disc groups each time, tearing down every
/// row: hover flickered and an open track menu closed with its owner row. It also rebuilt
/// the related carousels twice. The rows are rebuilt only when the track list changed.
/// </summary>
public class AlbumPageLibraryRefreshTests
{
    private sealed class FakeLastFm : ILastFmService
    {
        public bool IsAuthenticated => false;
        public string? Username => null;
        public void Configure(string? sessionKey) { }
        public Task<string> GetAuthUrlAsync() => Task.FromResult(string.Empty);
        public Task<bool> CompleteAuthAsync() => Task.FromResult(false);
        public string? GetSessionKey() => null;
        public void Logout() { }
        public Task ScrobbleAsync(Track track, DateTime startedAt) => Task.CompletedTask;
        public Task UpdateNowPlayingAsync(Track track) => Task.CompletedTask;
        public Task<string?> GetAlbumDescriptionAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<string?> GetAlbumDescriptionFullAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task SetAlbumDescriptionOverrideAsync(string artistName, string albumName, string? description, CancellationToken ct = default)
            => Task.CompletedTask;
        public Task ClearAlbumDescriptionOverrideAsync(string artistName, string albumName, CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private static Track NewTrack(string title, int number, int disc = 1) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        TrackNumber = number,
        DiscNumber = disc,
        FilePath = TestPaths.Primary("Music", "A", title + ".flac"),
    };

    private static Album NewAlbum(Guid id, List<Track> tracks) =>
        new() { Id = id, Name = "A", Artist = "B", Tracks = tracks };

    private static (AlbumDetailViewModel Vm, FakeLibraryService Library) OpenPage(Album album)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        Dispatcher.UIThread.RunJobs(); // the deferred related-sections build
        return (vm, lib);
    }

    /// <summary>Simulates an index rebuild: the library now holds <paramref name="rebuilt"/>.</summary>
    private static void Publish(FakeLibraryService lib, Album rebuilt)
    {
        var albums = (List<Album>)lib.Albums;
        albums.Clear();
        albums.Add(rebuilt);
        lib.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void IndexRebuild_WithTheSameTracks_KeepsTheRows()
    {
        var t1 = NewTrack("One", 1);
        var t2 = NewTrack("Two", 2);
        var album = NewAlbum(Guid.NewGuid(), new List<Track> { t1, t2 });
        var (vm, lib) = OpenPage(album);

        var group = vm.DiscGroups.Single();
        var trackResets = 0;
        var relatedResets = 0;
        vm.Tracks.CollectionChanged += (_, _) => trackResets++;
        vm.OtherVersions.CollectionChanged += (_, _) => relatedResets++;

        var rebuilt = NewAlbum(album.Id, new List<Track> { t1, t2 });
        rebuilt.Year = 2024;
        Publish(lib, rebuilt);

        Assert.Same(rebuilt, vm.Album);            // the header still gets the fresh album
        Assert.Equal(0, trackResets);              // no row teardown
        Assert.Same(group, vm.DiscGroups.Single());
        Assert.Equal(new[] { t1, t2 }, vm.DiscGroups.Single().Tracks);
        Assert.Equal(1, relatedResets);            // carousels rebuilt once, not twice
    }

    [AvaloniaFact]
    public void IndexRebuild_AfterAnInPlaceDiscEdit_Regroups()
    {
        var t1 = NewTrack("One", 1);
        var t2 = NewTrack("Two", 2);
        var album = NewAlbum(Guid.NewGuid(), new List<Track> { t1, t2 });
        var (vm, lib) = OpenPage(album);
        Assert.Single(vm.DiscGroups);

        // The metadata editor moves the last track to disc 2: same instances, same order.
        t2.DiscNumber = 2;
        Publish(lib, NewAlbum(album.Id, new List<Track> { t1, t2 }));

        Assert.Equal(new[] { 1, 2 }, vm.DiscGroups.Select(g => g.DiscNumber));
        Assert.True(vm.HasMultipleDiscs);
    }

    [AvaloniaFact]
    public void IndexRebuild_WithFreshTrackInstances_RebindsTheRows()
    {
        var t1 = NewTrack("One", 1);
        var album = NewAlbum(Guid.NewGuid(), new List<Track> { t1 });
        var (vm, lib) = OpenPage(album);

        // A rescan re-read the file: same Id, new instance with new metadata.
        var reread = NewTrack("One (Remastered)", 1);
        reread.Id = t1.Id;
        Publish(lib, NewAlbum(album.Id, new List<Track> { reread }));

        Assert.Same(reread, vm.Tracks.Single());
        Assert.Same(reread, vm.DiscGroups.Single().Tracks.Single());
    }
}
