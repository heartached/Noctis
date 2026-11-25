using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Keep Shuffle On (Settings › Playback, Discord 11-23): starting an album, playlist or song list
/// turned Shuffle off (ReplaceQueueAndPlay started every new queue unshuffled). With the setting
/// on and Shuffle on, a clicked song plays first and the rest follow shuffled, a Play button
/// starts on a random song, and Shuffle stays lit. Off (the default) keeps the old behaviour.
/// </summary>
public class KeepShuffleOnTests
{
    private static PlayerViewModel CreateVm() =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    private static List<Track> Album(string name, int count) => Enumerable.Range(1, count).Select(n => new Track
    {
        Id = Guid.NewGuid(),
        Title = $"{name} {n}",
        Artist = "A",
        Album = name,
        TrackNumber = n,
        DiscNumber = 1,
        FilePath = TestPaths.Primary("Music", name, $"{n:00}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    }).ToList();

    private static List<Guid> WholeQueue(PlayerViewModel vm)
    {
        var ids = new List<Guid> { vm.CurrentTrack!.Id };
        ids.AddRange(vm.UpNext.Select(t => t.Id));
        return ids;
    }

    private static PlayerViewModel ShufflingVm(bool keepShuffleOn)
    {
        var vm = CreateVm();
        vm.KeepShuffleOn = keepShuffleOn;
        vm.PlayShuffled(Album("First", 10));
        Assert.True(vm.IsShuffleEnabled);
        return vm;
    }

    [Fact]
    public void Off_StartingAnotherList_TurnsShuffleOff_AsBefore()
    {
        var vm = ShufflingVm(keepShuffleOn: false);
        var album = Album("Second", 20);

        vm.ReplaceQueueAndPlay(album, 4);

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(album[4].Id, vm.CurrentTrack!.Id);
        Assert.Equal(album.Skip(5).Select(t => t.Id), vm.UpNext.Select(t => t.Id));
    }

    [Fact]
    public void On_ClickedSong_PlaysFirst_RestShuffled_ShuffleStaysOn()
    {
        var vm = ShufflingVm(keepShuffleOn: true);
        var album = Album("Second", 20);

        vm.ReplaceQueueAndPlay(album, 4);

        Assert.True(vm.IsShuffleEnabled);
        Assert.Equal(album[4].Id, vm.CurrentTrack!.Id);
        // Every song of the list is queued once (the songs above the clicked one too).
        Assert.Equal(album.Select(t => t.Id).OrderBy(g => g), WholeQueue(vm).OrderBy(g => g));
        Assert.NotEqual(album.Skip(5).Select(t => t.Id), vm.UpNext.Take(15).Select(t => t.Id));
    }

    [Fact]
    public void On_ShuffleOffAfterwards_ContinuesTheListsOwnOrder()
    {
        var vm = ShufflingVm(keepShuffleOn: true);
        var album = Album("Second", 12);
        vm.ReplaceQueueAndPlay(album, 3);

        vm.ToggleShuffleCommand.Execute(null);

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(album[3].Id, vm.CurrentTrack!.Id);
        // The island's Shuffle button still turns it off: the list's order from the playing song.
        Assert.Equal(album[4].Id, vm.UpNext[0].Id);
    }

    [Fact]
    public void On_PlayButton_StartsShuffled_OnAnySong()
    {
        var starts = new HashSet<Guid>();
        var album = Album("Second", 20);
        for (var i = 0; i < 12; i++)
        {
            var vm = ShufflingVm(keepShuffleOn: true);
            vm.PlayAll(album);
            Assert.True(vm.IsShuffleEnabled);
            Assert.Equal(album.Select(t => t.Id).OrderBy(g => g), WholeQueue(vm).OrderBy(g => g));
            starts.Add(vm.CurrentTrack!.Id);
        }
        // A random start, not always track 1 (12 tries all landing on track 1: 1 in 20^11).
        Assert.True(starts.Count > 1);
    }

    [Fact]
    public void PlayButton_WithShuffleOff_PlaysInOrder_FromTrackOne()
    {
        var vm = CreateVm();
        vm.KeepShuffleOn = true;
        var album = Album("Second", 8);

        vm.PlayAll(album);

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(album[0].Id, vm.CurrentTrack!.Id);
        Assert.Equal(album.Skip(1).Select(t => t.Id), vm.UpNext.Select(t => t.Id));
    }

    [Fact]
    public void Off_PlayButton_PlaysInOrder_AndTurnsShuffleOff()
    {
        var vm = ShufflingVm(keepShuffleOn: false);
        var album = Album("Second", 8);

        vm.PlayAll(album);

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(album[0].Id, vm.CurrentTrack!.Id);
    }

    [Fact]
    public void On_TrackRadio_StillStartsUnshuffled()
    {
        var vm = ShufflingVm(keepShuffleOn: true);
        var seed = Album("Second", 1)[0];

        vm.StartRadioCommand.Execute(seed);

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(seed.Id, vm.CurrentTrack!.Id);
    }

    [Fact]
    public void On_RepeatAllCycle_IsTheWholeList()
    {
        var vm = ShufflingVm(keepShuffleOn: true);
        var album = Album("Second", 5);
        vm.ReplaceQueueAndPlay(album, 2);
        vm.RepeatMode = RepeatMode.All;

        // Play past the end: the wrap queues the whole list again, still shuffled.
        for (var i = 0; i < 5; i++) vm.NextCommand.Execute(null);

        Assert.True(vm.IsShuffleEnabled);
        Assert.Equal(album.Select(t => t.Id).OrderBy(g => g),
            WholeQueue(vm).Take(5).OrderBy(g => g));
    }

    [Fact]
    public void On_PlayAll_OnAListThatShuffleSkipsEntirely_StillPlaysIt_InOrder()
    {
        var vm = ShufflingVm(keepShuffleOn: true);
        var book = Album("Audiobook", 6);
        foreach (var t in book) t.SkipWhenShuffling = true;

        vm.PlayAll(book);

        Assert.Equal(book[0].Id, vm.CurrentTrack!.Id);
        Assert.Equal(book.Skip(1).Select(t => t.Id), vm.UpNext.Select(t => t.Id));
    }
}
