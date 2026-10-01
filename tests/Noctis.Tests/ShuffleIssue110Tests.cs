using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #110: Shuffle off continues the list's own order from the playing song — Song 31
/// → 32 — instead of jumping back to the first song not yet played. Songs before it that
/// never played follow at the end; none is lost.
/// </summary>
public class ShuffleIssue110Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

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
