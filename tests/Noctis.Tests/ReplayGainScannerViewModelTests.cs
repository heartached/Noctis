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
        public bool IsAvailable => true;
        public Task<ScanSummary> ScanAsync(IReadOnlyList<Track> tracks, bool albumMode, IProgress<ScanProgress> progress, CancellationToken ct)
        {
            Runs++;
            return Task.FromResult(new ScanSummary { Scanned = tracks.Count });
        }
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
