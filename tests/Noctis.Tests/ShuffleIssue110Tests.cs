using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #110: every Shuffle button starts the same shuffle mode the bar's toggle turns on
/// (the toggle lights up and can turn it off again), and Shuffle off continues the list's
/// own order from the playing song — Song 31 → 32 — instead of jumping back to the first
/// song not yet played. Songs before it that never played follow at the end; none is lost.
/// </summary>
public class ShuffleIssue110Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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

    private static PlayerViewModel CreateVm(FakeLibraryService? library = null) =>
        new(new FakeAudioPlayer(), library ?? new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    private static Track Song(int number, bool skipWhenShuffling = false) => new()
    {
        Id = Guid.NewGuid(),
        Title = $"Song {number}",
        Artist = "A",
        Album = "Album",
        TrackNumber = number,
        DiscNumber = 1,
        FilePath = TestPaths.Primary("Music", "Album", $"{number:00}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
        SkipWhenShuffling = skipWhenShuffling,
    };

    private static List<Track> Album(int count) => Enumerable.Range(1, count).Select(n => Song(n)).ToList();

    /// <summary>Skips until <paramref name="target"/> plays; returns the songs played before it.</summary>
    private static HashSet<Guid> SkipTo(PlayerViewModel vm, Track target)
    {
        var played = new HashSet<Guid>();
        while (vm.CurrentTrack!.Id != target.Id)
        {
            played.Add(vm.CurrentTrack.Id);
            vm.NextCommand.Execute(null);
        }
        return played;
    }

    /// <summary>The list's order after <paramref name="at"/>, wrapping round, minus what played.</summary>
    private static List<Guid> ContinuingAfter(IList<Track> list, int at, ISet<Guid> played) =>
        list.Skip(at + 1).Concat(list.Take(at)).Select(t => t.Id).Where(id => !played.Contains(id)).ToList();

    private static List<Guid> Ids(IEnumerable<Track> tracks) => tracks.Select(t => t.Id).ToList();

    [Fact]
    public void PlayShuffled_LightsTheBarShuffle_AndQueuesEverySong()
    {
        var vm = CreateVm();
        var album = Album(20);

        vm.PlayShuffled(album);

        Assert.True(vm.IsShuffleEnabled);
        var queued = new List<Guid> { vm.CurrentTrack!.Id };
        queued.AddRange(vm.UpNext.Select(t => t.Id));
        Assert.Equal(Ids(album).OrderBy(g => g), queued.OrderBy(g => g));
        // Shuffled, not album order (1 in 20! by chance).
        Assert.NotEqual(Ids(album), queued);
    }

    [Fact]
    public void PlayShuffled_ThenShuffleOff_OnSong31_NextIsSong32_AndTheEarlierSongsFollow()
    {
        var vm = CreateVm();
        var album = Album(50);
        vm.PlayShuffled(album, first: album[30]); // Song 31

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(album[31].Id, vm.UpNext[0].Id); // Song 32
        Assert.Equal(ContinuingAfter(album, 30, new HashSet<Guid>()), Ids(vm.UpNext));
    }

    [Fact]
    public void PlayShuffled_ShuffleOffMidAlbum_ContinuesAfterThePlayingSong_AndLosesNothing()
    {
        var vm = CreateVm();
        var album = Album(50);
        vm.PlayShuffled(album);
        var played = SkipTo(vm, album[30]);

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(ContinuingAfter(album, 30, played), Ids(vm.UpNext));
    }

    [Fact]
    public void PlayShuffled_LeavesOutSkipWhenShuffling_UntilShuffleOff()
    {
        var vm = CreateVm();
        var album = Album(6);
        var skipped = Song(7, skipWhenShuffling: true);
        album.Add(skipped);

        vm.PlayShuffled(album);
        Assert.NotEqual(skipped.Id, vm.CurrentTrack!.Id);
        Assert.DoesNotContain(vm.UpNext, t => t.Id == skipped.Id);

        vm.ToggleShuffleCommand.Execute(null); // off
        Assert.Contains(vm.UpNext, t => t.Id == skipped.Id);
    }

    [Fact]
    public void PlayShuffled_WithExplicitContentOff_NeverStartsOnOne_AndParksThemLikeTheToggle()
    {
        var vm = CreateVm();
        var album = Album(5);
        var x = Song(6);
        x.IsExplicit = true;
        album.Insert(0, x);
        vm.AllowExplicitContent = false;

        vm.PlayShuffled(album);
        Assert.NotEqual(x.Id, vm.CurrentTrack!.Id);
        Assert.DoesNotContain(vm.UpNext, t => t.Id == x.Id);

        vm.AllowExplicitContent = true; // the switch brings the parked track back
        Assert.Contains(vm.UpNext, t => t.Id == x.Id);
    }

    [Fact]
    public void LibraryShuffleFromAnEmptyPlayer_ShuffleOffContinuesInLibraryOrder()
    {
        var library = new FakeLibraryService();
        var songs = Album(10);
        library.TrackList.AddRange(songs);
        var vm = CreateVm(library);

        vm.PlayPauseCommand.Execute(null); // nothing loaded: shuffles the whole library
        Assert.True(vm.IsShuffleEnabled);
        var at = songs.FindIndex(t => t.Id == vm.CurrentTrack!.Id);

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(ContinuingAfter(songs, at, new HashSet<Guid>()), Ids(vm.UpNext));
    }

    [AvaloniaFact]
    public void AlbumPage_ShuffleButton_LightsTheBarShuffle_AndShuffleOffContinuesInAlbumOrder()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var songs = Album(12);
        var album = new Album { Id = Guid.NewGuid(), Name = "Album", Artist = "A", Tracks = songs };
        var page = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), new FakeLastFm());
        Dispatcher.UIThread.RunJobs();

        page.ShufflePlayCommand.Execute(null);
        Assert.True(player.IsShuffleEnabled);
        var played = SkipTo(player, songs[7]);

        player.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(ContinuingAfter(songs, 7, played), Ids(player.UpNext));
    }

    [Fact]
    public void BarShuffle_OnThenOffOnSong31_ContinuesWithSong32()
    {
        var vm = CreateVm();
        var album = Album(50);
        vm.ReplaceQueueAndPlay(album, 0); // in album order from Song 1
        vm.ToggleShuffleCommand.Execute(null); // on
        var played = SkipTo(vm, album[30]);

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(ContinuingAfter(album, 30, played), Ids(vm.UpNext));
    }

    [Fact]
    public void BarShuffle_OnThenStraightOff_GivesTheQueueBack_EvenWithARepeatedSong()
    {
        var vm = CreateVm();
        var (a, b, c, d) = (Song(1), Song(2), Song(3), Song(4));
        vm.ReplaceQueueAndPlay(new[] { a, b, c, a, d }, 0); // the first a plays, the second is queued

        vm.ToggleShuffleCommand.Execute(null); // on
        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(new[] { b.Id, c.Id, a.Id, d.Id }, Ids(vm.UpNext));
    }

    [Fact]
    public void ShuffleOff_KeepsASongQueuedWhileShuffled()
    {
        var vm = CreateVm();
        var album = Album(6);
        var extra = Song(99);
        vm.ReplaceQueueAndPlay(album, 0);
        vm.ToggleShuffleCommand.Execute(null); // on
        vm.AddToQueue(extra);

        vm.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(Ids(album.Skip(1)).Append(extra.Id), Ids(vm.UpNext));
    }

    [AvaloniaFact]
    public async Task ShuffleOff_AfterARestart_StillReturnsToTheAlbumOrder()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var library = new FakeLibraryService();
        var album = Album(8);
        library.TrackList.AddRange(album);

        var before = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        before.ReplaceQueueAndPlay(album, 2); // Song 3
        before.ToggleShuffleCommand.Execute(null); // on
        await Task.Delay(300); // let the track-change background snapshot land first
        await before.SaveQueueStateAsync();

        var after = new PlayerViewModel(new FakeAudioPlayer(), library, persistence, new FakeAnimatedCoverService());
        await after.RestoreQueueStateAsync();
        Assert.True(after.IsShuffleEnabled);

        after.ToggleShuffleCommand.Execute(null); // off

        Assert.Equal(Ids(album.Skip(3)), Ids(after.UpNext));
    }
}
