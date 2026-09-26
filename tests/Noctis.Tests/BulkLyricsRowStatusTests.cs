using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Bulk Lyrics dialog reads each row's lyrics state (a lyrics-store file probe per track)
/// off the UI thread, so a big selection does not freeze the window before the dialog opens.
/// </summary>
public class BulkLyricsRowStatusTests
{
    private static Track[] Tracks() => new[]
    {
        new Track { Title = "synced", SyncedLyrics = "[00:01.00]la" },
        new Track { Title = "plain", Lyrics = "la la" },
        new Track { Title = "none" },
    };

    [AvaloniaFact]
    public async Task Ctor_LeavesLyricsStateToTheBackground_ThenRowsShowIt()
    {
        var vm = new BulkLyricsViewModel(Tracks(), new NoopService(), remove: false);

        // The ctor ran on the UI thread without reading any row's lyrics.
        Assert.All(vm.Rows, r => Assert.Equal(string.Empty, r.Status));

        await vm.RowStatusesLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { "synced", "plain lyrics", "no lyrics" }, vm.Rows.Select(r => r.Status));
    }

    [AvaloniaFact]
    public async Task LateLyricsState_DoesNotOverwriteARowARunAlreadyReported()
    {
        var vm = new BulkLyricsViewModel(Tracks(), new NoopService(), remove: true);
        vm.Rows[0].Status = "removed";
        vm.Rows[0].Done = true;

        await vm.RowStatusesLoaded;
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("removed", vm.Rows[0].Status);
        Assert.Equal("plain lyrics", vm.Rows[1].Status);
    }

    private sealed class NoopService : ILyricsBulkService
    {
        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();

        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
