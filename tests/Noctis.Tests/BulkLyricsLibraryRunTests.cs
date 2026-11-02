using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The bulk lyrics dialog's options, summary, background run and Lyrics Studio offer
/// (Discord, Cosmo 11-02), and the animated artwork dialog's summary.
/// </summary>
public class BulkLyricsLibraryRunTests
{
    private sealed class Service : ILyricsBulkService
    {
        public LyricsBulkOptions? Options;
        public LyricsBulkSummary Result = new(0, 0, 0, 0, 0);
        public TaskCompletionSource? Gate;
        public CancellationToken Token;
        public LyricsBulkProgress? Report;

        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException("the dialog passes options");

        public async Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, LyricsBulkOptions options, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
        {
            Options = options;
            Token = ct;
            if (Report is not null) progress?.Report(Report);
            if (Gate is not null) await Gate.Task;
            return Result;
        }

        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct) => Task.FromResult(0);
    }

    private static Track[] Tracks() => new[] { new Track { Title = "a" }, new Track { Title = "b" } };

    [AvaloniaFact]
    public async Task Start_PassesTheOptions()
    {
        var service = new Service();
        var vm = new BulkLyricsViewModel(Tracks(), service, remove: false, library: true) { UpgradeExisting = true, Recheck = true };
        Assert.Equal("Find lyrics for your library (2 songs)", vm.TitleText);
        Assert.True(vm.CanChangeOptions);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(new LyricsBulkOptions(Upgrade: true, Recheck: true), service.Options);
        Assert.False(vm.CanChangeOptions);
    }

    [AvaloniaFact]
    public async Task Hide_LeavesTheRunGoing_StopCancelsIt()
    {
        var service = new Service { Gate = new TaskCompletionSource() };
        var vm = new BulkLyricsViewModel(Tracks(), service, remove: false);
        var closed = 0;
        vm.Closed += (_, _) => closed++;
        var run = vm.StartCommand.ExecuteAsync(null);
        Assert.True(vm.IsRunning);
        Assert.Equal("Hide", vm.CloseText);

        vm.CancelCommand.Execute(null);
        Assert.Equal(1, closed);
        Assert.False(service.Token.IsCancellationRequested);

        vm.StopCommand.Execute(null);
        Assert.True(service.Token.IsCancellationRequested);
        service.Gate.SetResult();
        await run;
        Assert.StartsWith("Stopped", vm.StatusMessage);
        Assert.Equal("Close", vm.CloseText);
    }

    [AvaloniaFact]
    public async Task PlainOnlySongs_AreOfferedToLyricsStudio_AtMostABatch()
    {
        var plain = Enumerable.Range(0, 45).Select(i => new Track { Title = "p" + i }).ToList();
        var service = new Service { Result = new LyricsBulkSummary(0, 45, 0, 0, 0) { PlainOnlyTracks = plain } };
        IReadOnlyList<Track>? opened = null;
        var vm = new BulkLyricsViewModel(Tracks(), service, remove: false, openStudio: t => { opened = t; return Task.CompletedTask; });
        await vm.StartCommand.ExecuteAsync(null);

        Assert.True(vm.CanTimeInStudio);
        Assert.Contains("45 songs have plain lyrics only", vm.StudioOfferText);
        Assert.Contains("a minute or more per song", vm.StudioOfferText);
        Assert.Contains("The first 40 open at a time", vm.StudioOfferText);

        await vm.OpenStudioCommand.ExecuteAsync(null);
        Assert.Equal(BulkLyricsViewModel.StudioBatch, opened!.Count);
    }

    [AvaloniaFact]
    public async Task NoStudioHook_NoOffer()
    {
        var service = new Service { Result = new LyricsBulkSummary(0, 1, 0, 0, 0) { PlainOnlyTracks = new[] { new Track() } } };
        var vm = new BulkLyricsViewModel(Tracks(), service, remove: false);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(vm.CanTimeInStudio);
    }

    [Fact]
    public void Summary_NamesWhatHappened()
    {
        var s = new LyricsBulkSummary(Synced: 5, PlainOnly: 1, NotFound: 2, Skipped: 4, Failed: 0)
        {
            WordSynced = 3, Upgraded = 2, CheckedBefore = 1, KeptOwnFile = 1, BusySources = new[] { "lrc.red" },
        };
        Assert.Equal("3 word-synced · 2 line-synced · 1 plain only · 2 upgraded · 2 not found · 3 kept as they were · "
                     + "1 looked up before · 1 kept your own lyrics file · lrc.red busy, try again later",
            BulkLyricsViewModel.Summarize(s));
        Assert.Equal("nothing to do", BulkLyricsViewModel.Summarize(new LyricsBulkSummary(0, 0, 0, 0, 0)));
    }

    [Fact]
    public void ArtworkSummary_NamesWhatHappened()
    {
        var s = new AnimatedArtworkBulkSummary(Found: 4, NotFound: 3, Skipped: 5, Failed: 1) { CheckedBefore = 2 };
        Assert.Equal("4 saved · 3 with no clear match · 3 already had one · 2 looked up before · 1 failed",
            BulkAnimatedArtworkViewModel.Summarize(s));
    }

    [AvaloniaFact]
    public async Task ProgressRow_IsFoundById_EvenWithTheSameTitle()
    {
        var a = new Track { Title = "Same" };
        var b = new Track { Title = "Same" };
        var service = new Service { Report = new LyricsBulkProgress(1, 2, "Same", "word-synced") { TrackId = b.Id } };
        var vm = new BulkLyricsViewModel(new[] { a, b }, service, remove: false);
        await vm.StartCommand.ExecuteAsync(null);
        for (var i = 0; i < 5; i++) Dispatcher.UIThread.RunJobs();
        Assert.False(vm.Rows[0].Done);
        Assert.True(vm.Rows[1].Done);
        Assert.Equal("word-synced", vm.Rows[1].Status);
    }
}
