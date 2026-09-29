using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (roge, 08-19): after a scan finishes the footer still showed "Scan", which
/// reads as "nothing happened yet" and re-clicking it silently rescans everything.
/// The primary button must turn into "Done" that closes the dialog.
/// </summary>
public class ReplayGainScannerViewModelTests
{
    private sealed class FakeScanner : IReplayGainScannerService
    {
        public int Runs;
        public IReadOnlyList<Track> LastTracks = Array.Empty<Track>();
        public bool IsAvailable => true;
        public Task<ScanSummary> ScanAsync(IReadOnlyList<Track> tracks, bool albumMode, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            Runs++;
            LastTracks = tracks;
            return Task.FromResult(new ScanSummary { Scanned = tracks.Count });
        }
    }

    // ── GitHub #105: pick which files to scan, add files outside the library ──

    [Fact]
    public async Task UntickedRows_AreLeftOutOfTheScan()
    {
        var scanner = new FakeScanner();
        var vm = Make(scanner);
        vm.Jobs[1].IsIncluded = false;
        Assert.Equal("Scan ReplayGain · 1 track", vm.TitleText);

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "a.flac" }, scanner.LastTracks.Select(t => t.FilePath));
        Assert.Equal("Skipped", vm.Jobs[1].Status);
    }

    [Fact]
    public async Task NothingTicked_DoesNotScan()
    {
        var scanner = new FakeScanner();
        var vm = Make(scanner);
        foreach (var row in vm.Jobs) row.IsIncluded = false;

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(0, scanner.Runs);
        Assert.False(vm.HasFinished);
    }

    [Fact]
    public void QueuedTwice_ListedOnce()
    {
        var t = new Track { FilePath = "a.mp3", Title = "A" };
        var vm = new ReplayGainScannerViewModel(new List<Track> { t, t }, new FakeScanner(), new FakeLibraryService());
        Assert.Single(vm.Jobs);
    }

    [Fact]
    public async Task AddFiles_AppendsResolvedFiles_SkippingKnownAndUnreadable()
    {
        var tracks = new List<Track> { new() { FilePath = "a.flac", Title = "A" } };
        var vm = new ReplayGainScannerViewModel(tracks, new FakeScanner(), new FakeLibraryService(),
            path => path == "bad.mp3" ? null : new Track { FilePath = path, Title = path });
        Assert.True(vm.CanAddFiles);

        await vm.AddFilesAsync(new[] { "A.FLAC", "x.mp3", "bad.mp3" });

        Assert.Equal(new[] { "a.flac", "x.mp3" }, vm.Jobs.Select(j => j.Track.FilePath));
        Assert.Equal("Scan ReplayGain · 2 tracks", vm.TitleText);
    }

    [Fact]
    public async Task ScanCompleted_Raised_WhenScanFinishes()
    {
        var vm = Make(new FakeScanner());
        var raised = 0;
        vm.ScanCompleted += (_, _) => raised++;
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, raised);
    }

    private static ReplayGainScannerViewModel Make(FakeScanner scanner)
    {
        var tracks = new List<Track> { new() { FilePath = "a.flac", Title = "A" }, new() { FilePath = "b.flac", Title = "B" } };
        return new ReplayGainScannerViewModel(tracks, scanner, new FakeLibraryService());
    }

    [Fact]
    public void PrimaryButton_ReadsScan_BeforeAnyRun()
    {
        var vm = Make(new FakeScanner());
        Assert.Equal("Scan", vm.PrimaryButtonText);
        Assert.False(vm.HasFinished);
    }

    [Fact]
    public async Task PrimaryButton_BecomesDone_AfterScanFinishes()
    {
        var vm = Make(new FakeScanner());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.HasFinished);
        Assert.Equal("Done", vm.PrimaryButtonText);
        Assert.StartsWith("Finished · 2 scanned", vm.StatusMessage);
    }

    [Fact]
    public async Task Done_ClosesDialog_WithoutRescanning()
    {
        var scanner = new FakeScanner();
        var vm = Make(scanner);
        var closed = 0;
        vm.Closed += (_, _) => closed++;

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(1, scanner.Runs);

        await vm.StartCommand.ExecuteAsync(null); // the button now reads "Done"
        Assert.Equal(1, scanner.Runs);
        Assert.Equal(1, closed);
    }
}
