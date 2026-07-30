using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Songs view used to rebuild its full filtered/sorted list synchronously on the UI
/// thread on every navigation AND on every LibraryUpdated event — 30-250 ms stalls at
/// 40k-100k tracks, repeating every ~1.5 s through a scan even while the view was hidden.
/// These pin the replacement behavior: off-thread rebuilds with a generation guard, and
/// LibraryUpdated-while-hidden deferring to a single catch-up rebuild on activation.
/// Search-key caching on Track (normalization allocations per keystroke) is pinned too.
/// </summary>
public class LibrarySongsRefreshTests
{
    private static (LibrarySongsViewModel vm, FakeLibraryService lib) CreateVm()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var vm = new LibrarySongsViewModel(lib, player, sidebar, persistence);
        return (vm, lib);
    }

    private static Track Trk(string title, string artist = "Artist") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        FilePath = TestPaths.Primary("t", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    /// <summary>Pumps the headless dispatcher until the condition holds (or 5 s).</summary>
    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>Pumps for a fixed window — for asserting that nothing happens.</summary>
    private static async Task PumpFor(int ms)
    {
        var deadline = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task Activation_RebuildsOffThread()
    {
        var (vm, lib) = CreateVm();
        lib.TrackList.Add(Trk("Alpha"));
        lib.TrackList.Add(Trk("Beta"));

        vm.IsActive = true; // catch-up path: VM starts dirty

        await PumpUntil(() => vm.FilteredTracks.Count == 2);
        Assert.Equal(2, vm.FilteredTracks.Count);
    }

    [AvaloniaFact]
    public async Task LibraryUpdated_WhileHidden_DefersToOneRebuildOnActivation()
    {
        var (vm, lib) = CreateVm();
        lib.TrackList.Add(Trk("Alpha"));

        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredTracks.Count == 1);

        vm.IsActive = false;
        var rebuilds = 0;
        vm.FilteredTracks.CollectionChanged += (_, _) => rebuilds++;

        // Simulate a scan's progressive publisher: three updates while hidden.
        lib.TrackList.Add(Trk("Beta"));
        lib.RaiseLibraryUpdated();
        lib.TrackList.Add(Trk("Gamma"));
        lib.RaiseLibraryUpdated();
        lib.TrackList.Add(Trk("Delta"));
        lib.RaiseLibraryUpdated();

        await PumpFor(300);
        Assert.Equal(0, rebuilds);              // no work while hidden
        Assert.Single(vm.FilteredTracks);       // still the old list

        vm.IsActive = true;                     // dirty flag catches up once
        await PumpUntil(() => vm.FilteredTracks.Count == 4);

        Assert.Equal(4, vm.FilteredTracks.Count);
        Assert.Equal(1, rebuilds);              // N deferred events -> exactly one rebuild
    }

    [AvaloniaFact]
    public async Task StaleRefresh_NeverOverwritesNewerFilter()
    {
        var (vm, lib) = CreateVm();
        lib.TrackList.Add(Trk("Alpha"));
        lib.TrackList.Add(Trk("Beta"));
        lib.TrackList.Add(Trk("Gamma"));

        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredTracks.Count == 3);

        // Older full rebuild racing a newer filter: the generation guard must let
        // only the filter's result land, regardless of completion order.
        vm.MarkDirty();
        vm.Refresh();
        vm.ApplyFilter("beta");

        await PumpUntil(() =>
            vm.FilteredTracks.Count == 1 && vm.FilteredTracks[0].Title == "Beta");
        // Both operations have had time to finish; a late stale result must not revert it.
        await PumpFor(300);

        var titles = vm.FilteredTracks.Select(t => t.Title).ToList();
        Assert.Equal(new[] { "Beta" }, titles);
    }

    [AvaloniaFact]
    public async Task NormalizedSearch_MatchesThroughCachedKeys()
    {
        var (vm, lib) = CreateVm();
        lib.TrackList.Add(Trk("Don't Stop Me Now", "Queen"));
        lib.TrackList.Add(Trk("Somebody to Love", "Queen"));

        vm.IsActive = true;
        await PumpUntil(() => vm.FilteredTracks.Count == 2);

        vm.ApplyFilter("dont stop"); // apostrophe/space-insensitive via normalized keys
        await PumpUntil(() => vm.FilteredTracks.Count == 1);

        Assert.Equal("Don't Stop Me Now", Assert.Single(vm.FilteredTracks).Title);
    }

    [Fact]
    public void SearchKeys_AreCached_AndInvalidatedByMetadataEdits()
    {
        var track = new Track { Title = "Don't Stop", Artist = "Mötley Crüe", Album = "Theatre of Pain" };

        Assert.Equal("dontstop", track.SearchTitleKey);
        Assert.Equal("motleycrue", track.SearchArtistKey);
        Assert.Equal("theatreofpain", track.SearchAlbumKey);

        // Cached: repeated reads return the same instance, no re-normalization.
        Assert.Same(track.SearchTitleKey, track.SearchTitleKey);
        Assert.Same(track.SearchArtistKey, track.SearchArtistKey);
        Assert.Same(track.SearchAlbumKey, track.SearchAlbumKey);

        // Metadata edits go through the property setters, which invalidate the keys.
        track.Title = "Kickstart My Heart";
        track.Artist = "Sixx:A.M.";
        track.Album = "The Heroin Diaries";

        Assert.Equal("kickstartmyheart", track.SearchTitleKey);
        Assert.Equal("sixxam", track.SearchArtistKey);
        Assert.Equal("theheroindiaries", track.SearchAlbumKey);
    }
}
