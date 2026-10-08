using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Find Metadata tool (Settings → Find Metadata). Its behaviour — identify all, per-row
/// tick, apply only the ticked rows, roll a row back when its tag write fails — and, since
/// owner 10-08 (same UI + animation for search metadata), the pill dialog shell it shares with
/// the Metadata editor: every close path animates out once.
/// </summary>
public class MetadataFinderDialogTests
{
    private readonly ITestOutputHelper _o;
    public MetadataFinderDialogTests(ITestOutputHelper o) => _o = o;

    private static Track T(string title, string artist, string album) => new()
    {
        Id = Guid.NewGuid(), Title = title, Artist = artist, Album = album, AlbumArtist = "",
        FilePath = TestPaths.Primary("Music", Guid.NewGuid().ToString("N") + ".flac"),
    };

    /// <summary>Answers every track with one suggestion, synchronously (as a cache hit would).</summary>
    private sealed class SyncFinder : IMetadataFinderService
    {
        public Func<Track, TagSuggestion?> Answer { get; set; } =
            t => new TagSuggestion("Fixed " + t.Title, "Bad Bunny", "nadie sabe", 2023, 0.9, "Deezer");
        public int Calls;
        public Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            ct.ThrowIfCancellationRequested();
            var s = Answer(track);
            return Task.FromResult<IReadOnlyList<TagSuggestion>>(s is null ? Array.Empty<TagSuggestion>() : new[] { s });
        }
    }

    /// <summary>Never answers until cancelled, so an identify run stays busy.</summary>
    private sealed class HangingFinder : IMetadataFinderService
    {
        public CancellationToken Seen;
        public async Task<IReadOnlyList<TagSuggestion>> IdentifyAsync(Track track, CancellationToken ct = default)
        {
            Seen = ct;
            await Task.Delay(Timeout.Infinite, ct);
            return Array.Empty<TagSuggestion>();
        }
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    [AvaloniaFact]
    public async Task IdentifyAll_CountsEveryMatch_AndTicksConfidentOnes()
    {
        var finder = new SyncFinder();
        finder.Answer = t => t.Title == "b"
            ? new TagSuggestion("B", "X", "Y", null, 0.5, "MusicBrainz")   // below auto-tick
            : t.Title == "c" ? null
            : new TagSuggestion("Fixed " + t.Title, "Bad Bunny", "nadie sabe", 2023, 0.9, "Deezer");
        var vm = new MetadataFinderViewModel(new[] { T("a", "", ""), T("b", "", ""), T("c", "", ""), T("d", "", "") },
            finder, new SearchPopupsShotsTests.OkTags(), new FakeLibraryService());

        await vm.IdentifyAllCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(4, finder.Calls);
        Assert.Equal(new[] { true, false, false, true }, vm.Rows.Select(r => r.Apply));
        Assert.Equal(new[] { true, true, false, true }, vm.Rows.Select(r => r.HasProposal));
        Assert.Equal("No match", vm.Rows[2].Status);
        Assert.Equal("Review", vm.Rows[1].Status);
        // Every match counts, including the last row's (its result used to land after the summary).
        Assert.Equal("Identified 3 of 4", vm.StatusMessage);
        Assert.True(vm.HasSelection);
        Assert.False(vm.IsBusy);
    }

    [AvaloniaFact]
    public async Task ApplySelected_WritesOnlyTickedRows_AndRollsBackAFailedWrite()
    {
        var tags = new SearchPopupsShotsTests.OkTags();
        var library = new FakeLibraryService();
        var vm = new MetadataFinderViewModel(new[] { T("a", "Unknown Artist", "Unknown Album"), T("b", "", ""), T("c", "", "") },
            new SyncFinder(), tags, library);
        await vm.IdentifyAllCommand.ExecuteAsync(null);
        Dispatcher.UIThread.RunJobs();

        vm.Rows[2].Apply = false;             // per-row tick: c stays as it is
        tags.Write = t => t.Title != "Fixed b"; // b's file is locked
        await vm.ApplySelectedCommand.ExecuteAsync(null);

        Assert.Equal("Fixed a", vm.Rows[0].Track.Title);
        Assert.Equal("Bad Bunny", vm.Rows[0].Track.Artist);
        Assert.Equal("Bad Bunny", vm.Rows[0].Track.AlbumArtist);
        Assert.Equal(2023, vm.Rows[0].Track.Year);
        Assert.Equal("Applied", vm.Rows[0].Status);
        // The failed write left the library on the file's real tags.
        Assert.Equal("b", vm.Rows[1].Track.Title);
        Assert.Equal("", vm.Rows[1].Track.Artist);
        Assert.Equal("Write failed", vm.Rows[1].Status);
        Assert.Equal("c", vm.Rows[2].Track.Title);
        Assert.Equal("Applied 1 track, 1 failed", vm.StatusMessage);
    }

    // ── The pill dialog shell (owner 10-08: same UI + animation for search metadata) ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }
}
