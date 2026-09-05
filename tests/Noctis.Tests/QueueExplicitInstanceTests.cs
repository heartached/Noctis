using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Regression pin for a Discord report ("adding an explicit track to the Queue adds the
/// non-explicit one"). A library can hold a clean and an explicit file with identical
/// title/artist/album; every queue entry point must enqueue the exact instance the user
/// clicked, never a same-titled sibling.
/// </summary>
public class QueueExplicitInstanceTests
{
    private static (PlayerViewModel vm, FakeAudioPlayer player, FakeLibraryService library) CreateVm()
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var vm = new PlayerViewModel(player, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        return (vm, player, library);
    }

    private static (Track clean, Track dirty) Siblings()
    {
        Track Make(bool explicitTrack, string file) => new()
        {
            Id = Guid.NewGuid(),
            Title = "Same Song",
            Artist = "Same Artist",
            Album = "Same Album",
            IsExplicit = explicitTrack,
            FilePath = TestPaths.Primary("t", file),
            Duration = TimeSpan.FromMinutes(3),
        };
        return (Make(false, "clean.m4a"), Make(true, "explicit.m4a"));
    }

    [AvaloniaFact]
    public void AddToQueue_EnqueuesTheClickedInstance_NotTheCleanSibling()
    {
        var (vm, _, library) = CreateVm();
        var (clean, dirty) = Siblings();
        library.TrackList.Add(clean);
        library.TrackList.Add(dirty);

        vm.AddToQueue(dirty);

        Assert.Single(vm.UpNext);
        Assert.Same(dirty, vm.UpNext[0]);
        Assert.True(vm.UpNext[0].IsExplicit);
    }

    [AvaloniaFact]
    public void AddNext_EnqueuesTheClickedInstance_NotTheCleanSibling()
    {
        var (vm, _, library) = CreateVm();
        var (clean, dirty) = Siblings();
        library.TrackList.Add(clean);
        library.TrackList.Add(dirty);
        vm.AddToQueue(clean);

        vm.AddNext(dirty);

        Assert.Equal(2, vm.UpNext.Count);
        Assert.Same(dirty, vm.UpNext[0]);
        Assert.Same(clean, vm.UpNext[1]);
    }

    [AvaloniaFact]
    public void ReplaceQueueAndPlay_StartsOnTheExactExplicitInstance()
    {
        var (vm, player, library) = CreateVm();
        var (clean, dirty) = Siblings();
        library.TrackList.Add(clean);
        library.TrackList.Add(dirty);

        vm.ReplaceQueueAndPlay(new[] { clean, dirty }, 1);

        Assert.Same(dirty, vm.CurrentTrack);
        Assert.Equal(dirty.FilePath, player.PlayedPaths[^1]);
    }
}
