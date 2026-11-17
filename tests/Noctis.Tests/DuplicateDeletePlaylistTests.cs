using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Deleting duplicates removed the deleted copies from the library and left their ids in every
/// playlist: dead entries the playlist pages skip and sync sends to the phone, whose Playlists
/// row counted them (Discord #mobile-beta 11-16: "Calm 9 songs" over a page of 4). A playlist
/// that held both copies kept a dead id next to the live one; one that held only the deleted
/// copy lost the song. The deleted copy's places now go to the copy that was kept.
/// </summary>
public class DuplicateDeletePlaylistTests
{
    private static Track Copy(string file) => new() { Title = "Calm Song", Artist = "Band", FilePath = "C:/Music/" + file };

    private sealed class PlaylistPersistence : TestPersistenceService
    {
        private List<Playlist> _stored = new();
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(_stored.ToList());
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            _stored = playlists.ToList();
            return Task.CompletedTask;
        }
    }

    private sealed class StubDuplicates(IReadOnlyList<DuplicateGroup> groups) : IDuplicateFinderService
    {
        public List<Guid> Deleted { get; } = new();
        public Task<IReadOnlyList<DuplicateGroup>> FindAsync(int durationToleranceSeconds = DuplicateMatcher.DefaultDurationToleranceSeconds,
            CancellationToken ct = default) => Task.FromResult(groups);
        public Task<int> DeleteAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
        {
            Deleted.AddRange(trackIds);
            return Task.FromResult(trackIds.Count);
        }
    }

    [Fact]
    public async Task DeletingACopy_HandsItsPlaylistPlacesToTheKeptCopy()
    {
        var flac = Copy("song.flac");
        var mp3 = Copy("song.mp3");
        var other = Copy("other.flac");
        var other2 = Copy("other.mp3");
        var service = new StubDuplicates(new[]
        {
            new DuplicateGroup(new[] { flac, mp3 }, flac.Id),
            new DuplicateGroup(new[] { other, other2 }, other.Id),
        });
        var vm = new DuplicateFinderViewModel(service) { Confirm = _ => Task.FromResult(true) };
        IReadOnlyDictionary<Guid, Guid>? handed = null;
        vm.RepointPlaylists = remap => { handed = remap; return Task.CompletedTask; };
        // The second group: the user keeps the MP3 instead of the suggested FLAC.
        var second = vm.Groups[1].Rows;
        second.Single(r => r.TrackId == other.Id).Keep = false;
        second.Single(r => r.TrackId == other2.Id).Keep = true;

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.Equal(new HashSet<Guid> { mp3.Id, other.Id }, service.Deleted.ToHashSet());
        Assert.NotNull(handed);
        Assert.Equal(flac.Id, handed![mp3.Id]);
        Assert.Equal(other2.Id, handed[other.Id]);
        Assert.Equal(2, handed.Count);
    }

    [Fact]
    public async Task DeletingEveryCopy_RepointsNothing()
    {
        var a = Copy("a.flac");
        var b = Copy("a.mp3");
        var vm = new DuplicateFinderViewModel(new StubDuplicates(new[] { new DuplicateGroup(new[] { a, b }, a.Id) }))
        {
            Confirm = _ => Task.FromResult(true),
        };
        IReadOnlyDictionary<Guid, Guid>? handed = null;
        vm.RepointPlaylists = remap => { handed = remap; return Task.CompletedTask; };
        vm.Groups[0].Rows.Single(r => r.TrackId == a.Id).Keep = false;

        await vm.DeleteSelectedCommand.ExecuteAsync(null);

        Assert.True(handed is null || handed.Count == 0);
    }

    /// <summary>The remap a playlist gets: the deleted copy's entry becomes the kept copy, and
    /// where the playlist already lists the kept copy the dead entry goes instead of listing the
    /// same song twice.</summary>
    [Fact]
    public async Task Remap_OntoASongThePlaylistAlreadyHolds_DropsTheOldEntry()
    {
        using var persistence = new PlaylistPersistence();
        var dead = Guid.NewGuid();
        var live = Guid.NewGuid();
        var x = Guid.NewGuid();
        await persistence.SavePlaylistsAsync(new List<Playlist>
        {
            new() { Name = "Both", TrackIds = { dead, x, live } },
            new() { Name = "OnlyDeleted", TrackIds = { x, dead } },
            new() { Name = "Twice", TrackIds = { dead, dead } },
        });
        var sidebar = new SidebarViewModel(persistence, new FakeLibraryService());
        await sidebar.LoadPlaylistsAsync();

        await sidebar.ApplyTrackIdRemapAsync(new Dictionary<Guid, Guid> { [dead] = live });

        Assert.Equal(new[] { x, live }, sidebar.Playlists.Single(p => p.Name == "Both").TrackIds);
        Assert.Equal(new[] { x, live }, sidebar.Playlists.Single(p => p.Name == "OnlyDeleted").TrackIds);
        Assert.Equal(new[] { live, live }, sidebar.Playlists.Single(p => p.Name == "Twice").TrackIds);
    }
}
