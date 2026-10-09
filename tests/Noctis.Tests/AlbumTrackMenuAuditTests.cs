using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Album page track menu (audit 10-09). The Songs and playlist pages act on the whole
/// Ctrl-selection when the clicked row is part of it; the album page rated / favorited only
/// the clicked row. And an album mixing untagged (disc 0) and disc-1 tracks showed a
/// "Disc 0" group above disc 1 while playback ran in disc-1 order, so Play on a row
/// there never reached the rows shown below it.
/// </summary>
public class AlbumTrackMenuAuditTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"noctis-albummenu-{Guid.NewGuid():N}");

    public AlbumTrackMenuAuditTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

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

    private Track NewTrack(string title, int number, int disc = 1)
    {
        var path = Path.Combine(_dir, title + ".mp3");
        File.WriteAllBytes(path, new byte[] { 0 });
        return new Track
        {
            Id = Guid.NewGuid(), Title = title, Artist = "B", Album = "A",
            TrackNumber = number, DiscNumber = disc, FilePath = path, Duration = TimeSpan.FromMinutes(3),
        };
    }

    private static (AlbumDetailViewModel Vm, PlayerViewModel Player) OpenPage(params Track[] tracks)
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Tracks = tracks.ToList() };
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        Dispatcher.UIThread.RunJobs();
        return (vm, player);
    }

    [AvaloniaFact]
    public async Task Rate_OnARowInTheSelection_RatesTheWholeSelection()
    {
        var (t1, t2, t3) = (NewTrack("One", 1), NewTrack("Two", 2), NewTrack("Three", 3));
        var (vm, _) = OpenPage(t1, t2, t3);
        vm.CtrlSelectedTracks = new List<Track> { t1, t2 };

        await vm.RateTrackCommand.ExecuteAsync(new RateRequest(t1, 4));

        Assert.Equal(new[] { 4, 4, 0 }, new[] { t1.Rating, t2.Rating, t3.Rating });
    }

    [AvaloniaFact]
    public async Task Rate_OnARowOutsideTheSelection_RatesThatRowOnly()
    {
        var (t1, t2, t3) = (NewTrack("One", 1), NewTrack("Two", 2), NewTrack("Three", 3));
        var (vm, _) = OpenPage(t1, t2, t3);
        vm.CtrlSelectedTracks = new List<Track> { t1, t2 };

        await vm.RateTrackCommand.ExecuteAsync(new RateRequest(t3, 5));

        Assert.Equal(new[] { 0, 0, 5 }, new[] { t1.Rating, t2.Rating, t3.Rating });
    }

    [AvaloniaFact]
    public async Task Favorite_OnARowInTheSelection_SetsTheWholeSelectionTheWayTheEntryReads()
    {
        var (t1, t2, t3) = (NewTrack("One", 1), NewTrack("Two", 2), NewTrack("Three", 3));
        t2.IsFavorite = true; // mixed selection: the clicked row's "Favorites" favorites both
        var (vm, _) = OpenPage(t1, t2, t3);
        vm.CtrlSelectedTracks = new List<Track> { t1, t2 };

        await vm.ToggleFavoriteCommand.ExecuteAsync(t1);

        Assert.True(t1.IsFavorite);
        Assert.True(t2.IsFavorite);
        Assert.False(t3.IsFavorite);
        Assert.Empty(vm.CtrlSelectedTracks);
    }

    [AvaloniaFact]
    public async Task Favorite_WithNoSelection_TogglesTheClickedRow()
    {
        var (t1, t2) = (NewTrack("One", 1), NewTrack("Two", 2));
        var (vm, _) = OpenPage(t1, t2);

        await vm.ToggleFavoriteCommand.ExecuteAsync(t2);
        Assert.True(t2.IsFavorite);
        Assert.False(t1.IsFavorite);

        await vm.ToggleFavoriteCommand.ExecuteAsync(t2);
        Assert.False(t2.IsFavorite);
    }

    [AvaloniaFact]
    public void UntaggedDisc_GroupsWithDiscOne_InPlayOrder()
    {
        // Library order (disc 0 counts as disc 1): One (disc 1, #1), Two (no disc, #2).
        var one = NewTrack("One", 1, disc: 1);
        var two = NewTrack("Two", 2, disc: 0);
        var (vm, player) = OpenPage(one, two);

        var shown = vm.DiscGroups.SelectMany(g => g.Tracks).ToList();
        Assert.Single(vm.DiscGroups);
        Assert.False(vm.HasMultipleDiscs);
        Assert.Equal(new[] { one, two }, shown);

        // Play on the first row shown queues every row shown below it.
        vm.PlayFromCommand.Execute(shown[0]);
        Assert.Equal(shown.Skip(1), player.UpNext);
    }
}
