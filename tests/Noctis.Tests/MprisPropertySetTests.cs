using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A34: MPRIS Shuffle/LoopStatus writes (GNOME/KDE widget, `playerctl shuffle On`)
/// only flipped the properties, so Shuffle lit the indicator while Up Next stayed
/// in album order. They now go through the same commands as a click.
/// </summary>
public class MprisPropertySetTests
{
    private static PlayerViewModel CreateVm() => new(
        new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    private static Track Trk(string name) => new()
    {
        Id = Guid.NewGuid(),
        Title = name,
        Artist = "A",
        FilePath = $"C:/t/{name}.mp3",
        Duration = TimeSpan.FromMinutes(3),
    };

    [Fact]
    public void ShuffleOn_ReordersUpNext_AndShuffleOff_RestoresIt()
    {
        var vm = CreateVm();
        var tracks = Enumerable.Range(0, 20).Select(i => Trk($"t{i}")).ToList();
        vm.ReplaceQueueAndPlay(tracks, 0);
        var albumOrder = tracks.Skip(1).Select(t => t.Id).ToList();

        MprisService.ApplyShuffle(vm, true);

        Assert.True(vm.IsShuffleEnabled);
        Assert.Equal(albumOrder.OrderBy(g => g), vm.UpNext.Select(t => t.Id).OrderBy(g => g));
        // Album order left in place with Shuffle lit (1 in 19! by chance).
        Assert.NotEqual(albumOrder, vm.UpNext.Select(t => t.Id).ToList());

        MprisService.ApplyShuffle(vm, false);

        Assert.False(vm.IsShuffleEnabled);
        Assert.Equal(albumOrder, vm.UpNext.Select(t => t.Id).ToList());
    }

    [Fact]
    public void ShuffleOn_WhenAlreadyOn_KeepsTheShuffledOrder()
    {
        var vm = CreateVm();
        vm.ReplaceQueueAndPlay(Enumerable.Range(0, 20).Select(i => Trk($"t{i}")).ToList(), 0);
        MprisService.ApplyShuffle(vm, true);
        var shuffled = vm.UpNext.Select(t => t.Id).ToList();

        MprisService.ApplyShuffle(vm, true);

        Assert.True(vm.IsShuffleEnabled);
        Assert.Equal(shuffled, vm.UpNext.Select(t => t.Id).ToList());
    }

    [Theory]
    [InlineData("None", RepeatMode.Off, RepeatMode.Off)]
    [InlineData("Playlist", RepeatMode.Off, RepeatMode.All)]
    [InlineData("Track", RepeatMode.Off, RepeatMode.One)]
    [InlineData("None", RepeatMode.One, RepeatMode.Off)]
    [InlineData("Playlist", RepeatMode.One, RepeatMode.All)]
    [InlineData("bogus", RepeatMode.All, RepeatMode.Off)]
    public void LoopStatus_SetsTheRepeatMode(string loopStatus, RepeatMode from, RepeatMode expected)
    {
        var vm = CreateVm();
        vm.RepeatMode = from;

        MprisService.ApplyLoopStatus(vm, loopStatus);

        Assert.Equal(expected, vm.RepeatMode);
    }
}
