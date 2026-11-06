using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone lyrics, overlapping voices (a duet: the second singer starts while the first line is
/// still being sung). The page scrolls to <see cref="LyricsPageViewModel.ScrollLineIndex"/>:
/// the earliest line still being sung, so it never scrolls past a line before its end (beta
/// report, Kanye West "All Falls Down": the still-sung line was pushed up and out of focus the
/// moment the answering line began).
/// </summary>
public class MobileLyricsOverlapScrollTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    // Line 0 (voice 1) is sung 1.0-6.0 s; line 1 (voice 2) answers from 3.0 to 5.0 s, inside
    // it; line 2 follows alone at 7.0 s.
    private const string Duet = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xml:lang="en">
          <body><div>
            <p begin="1.000" end="6.000" ttm:agent="v1"><span begin="1.000" end="2.000">I'm</span> <span begin="2.000" end="3.500">tellin'</span> <span begin="3.500" end="5.000">you</span> <span begin="5.000" end="6.000">all</span></p>
            <p begin="3.000" end="5.000" ttm:agent="v2"><span begin="3.000" end="3.800">Uh</span> <span begin="3.800" end="4.400">Chi-Town</span> <span begin="4.400" end="5.000">stand up</span></p>
            <p begin="7.000" end="8.000" ttm:agent="v1"><span begin="7.000" end="8.000">Next</span></p>
          </div></body>
        </tt>
        """;

    private static Track NewTrack() =>
        new() { Id = Guid.NewGuid(), Title = "t", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(60) };

    private async Task<(LyricsPageViewModel Vm, FakeAudioPlayer Player, Action<double> At)> Open(string ttml)
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var np = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var files = new FakeTrackFiles();
        files.Sidecars[".ttml"] = ttml;
        var vm = new LyricsPageViewModel(player, np, files, persistence, work => Task.FromResult(work()));
        np.PlayTracks(new[] { NewTrack() }, 0);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!vm.HasLyrics && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(vm.IsSynced);
        // Paused-style seeks: the position is audible as given (no clock smoothing).
        void At(double seconds)
        {
            player.RaisePositionChanged(TimeSpan.FromSeconds(seconds));
            vm.OnFrame(seconds * 1000);
        }
        return (vm, player, At);
    }

    [Fact]
    public async Task ScrollStaysOnTheStillSungLine_WhileTheAnswerOverlaps()
    {
        var (vm, _, at) = await Open(Duet);
        at(2.0);
        Assert.Equal(0, vm.ActiveLineIndex);
        Assert.Equal(0, vm.ScrollLineIndex);

        at(4.0);   // the answering voice began at 3.0; line 0 is sung until 6.0
        Assert.Equal(1, vm.ActiveLineIndex);
        Assert.Equal(0, vm.ScrollLineIndex);

        at(5.5);   // line 1 ended at 5.0, line 0 still sung
        Assert.Equal(0, vm.ScrollLineIndex);
    }

    [Fact]
    public async Task ScrollMovesOn_OnceTheEarlierLineFinishes()
    {
        var (vm, _, at) = await Open(Duet);
        at(4.0);
        Assert.Equal(0, vm.ScrollLineIndex);

        at(6.5);   // line 0 sung out; the answer ended too, nothing overlaps
        Assert.Equal(1, vm.ScrollLineIndex);
        Assert.Equal(1, vm.ActiveLineIndex);

        at(7.2);
        Assert.Equal(2, vm.ScrollLineIndex);
        Assert.Equal(2, vm.ActiveLineIndex);
    }

    [Fact]
    public async Task ScrollFollowsTheActiveLine_WithoutOverlap()
    {
        var (vm, _, at) = await Open(MobileLyricsViewModelTests.IssueTtml);
        at(0.2);
        Assert.Equal(-1, vm.ScrollLineIndex);
        at(2.5);
        Assert.Equal(0, vm.ScrollLineIndex);
        at(5.5);
        Assert.Equal(1, vm.ScrollLineIndex);
        Assert.Equal(vm.ActiveLineIndex, vm.ScrollLineIndex);
    }

    [Fact]
    public async Task SeekingBackIntoTheOverlap_ScrollsToTheEarlierLine_AndBeforeTheLyricsResets()
    {
        var (vm, _, at) = await Open(Duet);
        at(7.2);
        Assert.Equal(2, vm.ScrollLineIndex);
        at(4.0);
        Assert.Equal(0, vm.ScrollLineIndex);
        at(0.2);
        Assert.Equal(-1, vm.ScrollLineIndex);
    }
}
