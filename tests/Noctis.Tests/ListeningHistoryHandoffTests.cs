using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings → Listening History (the week's totals, Keep History, Clear, Share with Computer) and
/// Pick Up on Computer (the savePlayQueue payload, the offer's "newer than the desktop" rule, an
/// old peer on either side).
/// </summary>
public class ListeningHistoryHandoffTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "hist-" + Guid.NewGuid().ToString("N"));

    public ListeningHistoryHandoffTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static readonly TimeZoneInfo Tokyo = TimeZoneInfo.CreateCustomTimeZone("t+9", TimeSpan.FromHours(9), "t+9", "t+9");

    private static PlayHistoryEvent Play(Guid id, DateTime utc, bool skipped = false) => new()
    {
        TrackId = id, Title = "T", Artist = "A", PlayedAtUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc), Skipped = skipped,
    };

    private static Track RemoteSong(string title)
    {
        var t = MobileFixtures.Song(title);
        t.FilePath = "noctis-remote://tr-" + t.Id.ToString("N");
        return t;
    }

    // ── The week ──

    [Fact]
    public void Week_SplitsDaysAtTheUsersMidnight_NotUtcs()
    {
        var id = Guid.NewGuid();
        var now = new DateTime(2026, 10, 28, 1, 0, 0, DateTimeKind.Utc);   // Wed 10:00 in Tokyo
        var log = new[]
        {
            Play(id, new DateTime(2026, 10, 21, 14, 59, 0)),   // Tokyo Oct 21 23:59: before the window
            Play(id, new DateTime(2026, 10, 21, 15, 0, 0)),    // Tokyo Oct 22 00:00: the first day
            Play(id, new DateTime(2026, 10, 27, 14, 59, 0)),   // Tokyo Oct 27 23:59: yesterday
            Play(id, new DateTime(2026, 10, 27, 15, 0, 0)),    // Tokyo Oct 28 00:00: today
        };

        var tokyo = ListeningWeekCalculator.Compute(log, _ => TimeSpan.FromMinutes(3), now, Tokyo);

        Assert.Equal(new DateTime(2026, 10, 22), tokyo.Days[0]);
        Assert.Equal(new DateTime(2026, 10, 28), tokyo.Days[6]);
        Assert.Equal(3, tokyo.Plays);
        Assert.Equal(TimeSpan.FromMinutes(1.5).Ticks, tokyo.DayTicks[0]);
        Assert.Equal(TimeSpan.FromMinutes(1.5).Ticks, tokyo.DayTicks[5]);
        Assert.Equal(TimeSpan.FromMinutes(1.5).Ticks, tokyo.DayTicks[6]);
        Assert.Equal(TimeSpan.FromMinutes(4.5).Ticks, tokyo.TotalTicks);

        // The same log on UTC's clock: the window is Oct 22-28, the two Oct 21 plays are outside, and
        // both late plays are Oct 27.
        var utc = ListeningWeekCalculator.Compute(log, _ => TimeSpan.FromMinutes(3), now, TimeZoneInfo.Utc);
        Assert.Equal(2, utc.Plays);
        Assert.Equal(0, utc.DayTicks[6]);
        Assert.Equal(TimeSpan.FromMinutes(3).Ticks, utc.DayTicks[5]);
    }

    [Fact]
    public void Week_SkipsCountAsPlaysWithNoTime_AndUnknownLengthsCountHalfOfThree()
    {
        var known = Guid.NewGuid();
        var unknown = Guid.NewGuid();
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var log = new[]
        {
            Play(known, now.AddHours(-1)),
            Play(known, now.AddHours(-2), skipped: true),
            Play(unknown, now.AddHours(-3)),
        };

        var week = ListeningWeekCalculator.Compute(log, id => id == known ? TimeSpan.FromMinutes(4) : null, now, TimeZoneInfo.Utc);

        Assert.Equal(3, week.Plays);
        Assert.Equal(TimeSpan.FromMinutes(3.5).Ticks, week.TotalTicks);
        Assert.Equal("3 min this week", ListeningWeekCalculator.FormatSummary(week));
    }

    [Fact]
    public void Week_Formats()
    {
        Assert.Equal("6 h 12 min", ListeningWeekCalculator.FormatTotal(TimeSpan.FromMinutes(372).Ticks));
        Assert.Equal("2 h", ListeningWeekCalculator.FormatTotal(TimeSpan.FromHours(2).Ticks));
        Assert.Equal("12 min", ListeningWeekCalculator.FormatTotal(TimeSpan.FromMinutes(12).Ticks));
        Assert.Equal("0 min", ListeningWeekCalculator.FormatTotal(0));
        Assert.Equal("", ListeningWeekCalculator.FormatSummary(ListeningWeek.Empty(new DateTime(2026, 10, 28))));
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Now", ListeningWeekCalculator.FormatAgo(now.AddSeconds(-20), now));
        Assert.Equal("12 min", ListeningWeekCalculator.FormatAgo(now.AddMinutes(-12), now));
        Assert.Equal("1 h", ListeningWeekCalculator.FormatAgo(now.AddMinutes(-90), now));
        Assert.Equal("3 d", ListeningWeekCalculator.FormatAgo(now.AddDays(-3), now));
    }

    [Fact]
    public void Page_ShowsRealWeek_AndTheLastThreeDistinctSongs()
    {
        var songs = Enumerable.Range(1, 4).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs, log: h =>
        {
            var now = DateTime.UtcNow;
            h.Seed(songs[0], now.AddMinutes(-50));
            h.Seed(songs[1], now.AddMinutes(-40));
            h.Seed(songs[2], now.AddMinutes(-30));
            h.Seed(songs[3], now.AddMinutes(-20));
            h.Seed(songs[2], now.AddMinutes(-12));   // again: one row, at its newest time
        });

        var vm = new ListeningHistoryViewModel(rig.Shell);

        Assert.Equal(new[] { "Song 3", "Song 4", "Song 2" }, vm.Recent.Select(r => r.Track.Title));
        Assert.Equal("12 min", vm.Recent[0].When);
        Assert.Equal("5 plays", vm.WeekPlays);
        Assert.Equal("3 min this week", vm.Summary);   // 5 x 45 s: old events count half the song (PlayListening.LegacyShare)
        Assert.Equal(7, vm.Bars.Count);
        Assert.True(vm.Bars[^1].IsToday || vm.Bars.Any(b => b.IsToday));
        Assert.True(vm.HasRecent);
    }

    [Fact]
    public void Page_WithNoPlays_HasAnEmptySummary()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new ListeningHistoryViewModel(rig.Shell);
        Assert.Equal("", vm.Summary);
        Assert.False(vm.HasRecent);
        Assert.Equal(7, vm.Bars.Count);
    }

    [Fact]
    public async Task ShowAll_OpensASongListOfTheRecentSongs()
    {
        var songs = Enumerable.Range(1, 5).Select(i => MobileFixtures.Song("S" + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs, log: h =>
        {
            for (var i = 0; i < 5; i++) h.Seed(songs[i], DateTime.UtcNow.AddMinutes(-10 + i));
        });
        var vm = new ListeningHistoryViewModel(rig.Shell);

        vm.ShowAllCommand.Execute(null);

        var page = Assert.IsType<SongListPageViewModel>(rig.Shell.CurrentPage);
        Assert.Equal("Recently Played", page.Title);
        Assert.Equal(new[] { "S5", "S4", "S3", "S2", "S1" }, page.Songs.Select(s => s.Title));
        await Task.CompletedTask;
    }

    // ── Keep History / Clear ──

    [Fact]
    public async Task PlayHistory_PruneOlderThan_DropsThem_AndTheFileForgetsThem()
    {
        var path = Path.Combine(_dir, "play_history.json");
        var now = DateTime.UtcNow;
        var old = new[] { Play(Guid.NewGuid(), now.AddDays(-400)), Play(Guid.NewGuid(), now.AddDays(-100)) };
        var fresh = Play(Guid.NewGuid(), now.AddDays(-5));
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(old.Append(fresh).ToList()), TestContext.Current.CancellationToken);
        var history = new PlayHistoryService(path);

        Assert.Equal(1, history.PruneOlderThan(now.AddDays(-365)));
        Assert.Equal(2, history.Events.Count);
        Assert.Equal(1, history.PruneOlderThan(now.AddDays(-90)));
        Assert.Equal(0, history.PruneOlderThan(now.AddDays(-90)));   // nothing left to drop
        Assert.Equal(fresh.TrackId, Assert.Single(history.Events).TrackId);

        await history.FlushAsync();
        var reloaded = new PlayHistoryService(path);
        Assert.Equal(fresh.TrackId, Assert.Single(reloaded.Events).TrackId);

        // A play after a prune is kept and saved.
        history.RecordPlay(new Track { Id = Guid.NewGuid(), Title = "New", Artist = "A" });
        await history.FlushAsync();
        Assert.Equal(2, new PlayHistoryService(path).Events.Count);
    }

    [Fact]
    public async Task PlayHistory_Clear_EmptiesTheLog_AndTheFile_AndLaterPlaysStillRecord()
    {
        var path = Path.Combine(_dir, "play_history.json");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new List<PlayHistoryEvent> { Play(Guid.NewGuid(), DateTime.UtcNow) }),
            TestContext.Current.CancellationToken);
        var history = new PlayHistoryService(path);
        var before = history.Events;
        Assert.Single(before);

        await history.ClearAsync();

        Assert.Empty(history.Events);
        Assert.Single(before);   // a snapshot a reader holds is left alone
        Assert.Empty(new PlayHistoryService(path).Events);

        history.RecordPlay(new Track { Id = Guid.NewGuid(), Title = "After", Artist = "A" });
        await history.FlushAsync();
        Assert.Equal("After", Assert.Single(new PlayHistoryService(path).Events).Title);
    }

    [Fact]
    public async Task Clear_AsksFirst_ThenEmptiesTheLog_AndThePageRereads()
    {
        var song = MobileFixtures.Song("Song");
        using var rig = MobileFixtures.MakeRig(new[] { song }, log: h => h.Seed(song, DateTime.UtcNow.AddMinutes(-5)));
        var vm = new ListeningHistoryViewModel(rig.Shell);
        Assert.True(vm.HasRecent);

        vm.AskClearCommand.Execute(null);
        var prompt = Assert.IsType<PromptViewModel>(rig.Shell.Prompt);
        Assert.Contains("Last.fm", prompt.Message);
        Assert.Equal(1, rig.History.EventList.Count);   // nothing cleared yet

        var clear = Assert.Single(prompt.Actions);
        Assert.True(clear.IsDestructive);
        await clear.RunCommand.ExecuteAsync(null);

        Assert.Empty(rig.History.EventList);
        Assert.False(vm.HasRecent);
        Assert.Equal("", vm.Summary);
        Assert.Null(rig.Shell.Prompt);
    }

    [Fact]
    public async Task KeepHistory_Chooser_OffersFourChoices_AndSavesOnlyItsOwnField()
    {
        using var rig = MobileFixtures.MakeRig(seed: p => p.UpdateSettingsAsync(s => s.MobileLyricsTextScale = 1.4));
        await rig.Shell.LoadHistoryPrefsAsync();
        // Another save lands after the load; the history save must not undo it.
        await rig.Persistence.UpdateSettingsAsync(s => s.MobileLyricsTextScale = 1.2);
        var vm = new ListeningHistoryViewModel(rig.Shell);

        vm.ChooseKeepCommand.Execute(null);
        var prompt = Assert.IsType<PromptViewModel>(rig.Shell.Prompt);
        Assert.Equal(new[] { "Forever", "1 Year", "90 Days", "30 Days" }, prompt.Actions.Select(a => a.Label));
        await prompt.Actions[2].RunCommand.ExecuteAsync(null);
        await rig.Shell.PendingHistorySave;

        Assert.Equal("90 Days", vm.KeepText);
        var saved = await rig.Persistence.LoadSettingsAsync();
        Assert.Equal(90, saved.MobileKeepHistoryDays);
        Assert.Equal(1.2, saved.MobileLyricsTextScale);
    }

    [Fact]
    public async Task KeepHistory_PrunesOnLoad_AndOnChange_OffTheCallingThread()
    {
        var song = MobileFixtures.Song("Song");
        using var rig = MobileFixtures.MakeRig(new[] { song }, seed: p => p.UpdateSettingsAsync(s => s.MobileKeepHistoryDays = 30),
            log: h =>
            {
                h.Seed(song, DateTime.UtcNow.AddDays(-200));
                h.Seed(song, DateTime.UtcNow.AddDays(-45));
                h.Seed(song, DateTime.UtcNow.AddDays(-10));
            });
        var shell = ShellWith(rig, null);

        await shell.LoadHistoryPrefsAsync();
        Assert.Equal(30, shell.KeepHistoryDays);
        Assert.Single(rig.History.EventList);

        rig.History.Seed(song, DateTime.UtcNow.AddDays(-400));
        shell.SetKeepHistoryDays(0);   // Forever: nothing is pruned
        await shell.PruneHistory();
        Assert.Equal(2, rig.History.EventList.Count);

        shell.SetKeepHistoryDays(90);  // a change prunes at once, on the pool
        await shell.PruneHistory();
        Assert.Single(rig.History.EventList);
    }

    // ── Share with Computer ──

    [Fact]
    public async Task ShareWithComputer_Off_KeepsTheDesktopPlayOnThePhone()
    {
        var remote = RemoteSong("Remote");
        using var rig = MobileFixtures.MakeRig(new[] { remote });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        await shell.LoadHistoryPrefsAsync();
        Assert.True(shell.ShareHistoryWithComputer);   // on by default: nothing changes for anyone who never opens this page

        shell.SetShareHistoryWithComputer(false);
        shell.Player.PlayTracks(new[] { remote }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        Assert.Empty(account.Plays);
        Assert.Single(rig.History.EventList);   // the phone's own log still has it

        shell.SetShareHistoryWithComputer(true);
        shell.Player.PlayTracks(new[] { remote }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        Assert.Single(account.Plays);

        await shell.PendingHistorySave;
        Assert.True((await rig.Persistence.LoadSettingsAsync()).MobileShareHistory);
    }

    [Fact]
    public async Task WithTheScrobblerFollowingPlayback_ADesktopPlayWaitsForItsVerdict_AndASkipSaysNo()
    {
        var remote = RemoteSong("Remote");
        var next = RemoteSong("Next");
        using var rig = MobileFixtures.MakeRig(new[] { remote, next });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var scrobbler = new Noctis.Mobile.Services.Scrobbling.PhoneScrobbler(rig.Persistence, new System.Net.Http.HttpClient()) { AutoFlush = false };
        await scrobbler.EnsureLoadedAsync();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account, Scrobbler = scrobbler, Marshal = a => a(), TintFactory = rig.Shell.TintFactory,
        };

        shell.Player.PlayTracks(new[] { remote, next }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        Assert.Equal(new[] { remote }, account.Started);                    // held for its verdict
        shell.Player.PlayTracks(new[] { next }, 0);                         // skipped after a second
        Assert.Contains((remote, PlayScrobble.No), account.Decided);
        Assert.DoesNotContain(account.Decided, d => d.Verdict != PlayScrobble.No);
    }

    [Fact]
    public async Task ChoiceMadeBeforeTheSavedOnesLoaded_IsKept_AndSaved()
    {
        using var rig = MobileFixtures.MakeRig(seed: p => p.UpdateSettingsAsync(s => s.MobileShareHistory = true));
        var shell = rig.Shell;

        shell.SetShareHistoryWithComputer(false);   // Settings opened while the start still loads
        await shell.LoadHistoryPrefsAsync();
        await shell.PendingHistorySave;

        Assert.False(shell.ShareHistoryWithComputer);
        Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileShareHistory);
    }

    [Fact]
    public void PageSwitches_AreTheShellsChoices_OnEveryInstance()
    {
        using var rig = MobileFixtures.MakeRig();
        var a = new ListeningHistoryViewModel(rig.Shell);
        var b = new ListeningHistoryViewModel(rig.Shell);

        a.PickUpOnComputer = true;
        a.ShareWithComputer = false;

        Assert.True(rig.Shell.PickUpOnComputer);
        Assert.False(rig.Shell.ShareHistoryWithComputer);
        Assert.True(b.PickUpOnComputer);
        Assert.False(b.ShareWithComputer);
    }

    // ── The payload ──

    [Fact]
    public void Handoff_FromWire_PutsTheCurrentSongInPlace_AndNeverTrustsAPhoneClock()
    {
        var ids = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToList();
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);

        var h = PhoneHandoff.FromWire(ids, ids[1], 61_500, 4_000, now, "Pixel")!;
        Assert.Equal(ids, h.TrackIds);
        Assert.Equal(ids[1], h.CurrentId);
        Assert.Equal(61_500, h.PositionMs);
        Assert.Equal(now.AddSeconds(-4), h.SavedUtc);
        Assert.Equal("Pixel", h.DeviceName);

        // A current song missing from the list: the first one, from its start. A huge age is capped; a negative one is now.
        var odd = PhoneHandoff.FromWire(ids, Guid.NewGuid(), 9_000, long.MaxValue, now, "")!;
        Assert.Equal(ids[0], odd.CurrentId);
        Assert.Equal(0, odd.PositionMs);
        Assert.Equal(now.AddDays(-7), odd.SavedUtc);
        Assert.Equal(now, PhoneHandoff.FromWire(ids, ids[0], 0, -5, now, "")!.SavedUtc);

        Assert.Null(PhoneHandoff.FromWire(Array.Empty<Guid>(), null, 0, 0, now, ""));
        Assert.Null(PhoneHandoff.FromWire(new[] { Guid.Empty }, null, 0, 0, now, ""));
        var many = Enumerable.Range(0, PhoneHandoff.MaxTracks + 50).Select(_ => Guid.NewGuid()).ToList();
        Assert.Equal(PhoneHandoff.MaxTracks, PhoneHandoff.FromWire(many, many[0], 0, 0, now, "")!.TrackIds.Count);
    }

    [Fact]
    public void Phone_CapturesTheDesktopSongsOnly_PlayingOneFirst()
    {
        var a = RemoteSong("A");
        var b = RemoteSong("B");
        var local = MobileFixtures.Song("Local");
        using var rig = MobileFixtures.MakeRig(new[] { a, b, local });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        shell.SetPickUpOnComputer(true);

        shell.Player.PlayTracks(new[] { a, local, b }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(12));
        shell.PickUp.Flush();

        var sent = account.SavedQueues.Last();
        Assert.Equal(new[] { a.Id, b.Id }, sent.Ids);
        Assert.Equal(a.Id, sent.Current);
        Assert.InRange(sent.PositionMs, 11_000, 13_000);

        // A phone-only song playing: nothing to continue on the computer, nothing sent.
        account.SavedQueues.Clear();
        shell.Player.PlayTracks(new[] { local, a }, 0);
        shell.PickUp.Flush();
        Assert.Empty(account.SavedQueues);
    }

    [Fact]
    public void PickUp_IsOffByDefault_AndSendsNothingUntilOn()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        Assert.False(shell.PickUpOnComputer);

        shell.Player.PlayTracks(new[] { a }, 0);
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(31));
        shell.PickUp.Flush();
        Assert.Empty(account.SavedQueues);

        shell.SetPickUpOnComputer(true);   // on: the current spot goes at once
        Assert.Single(account.SavedQueues);
    }

    [Fact]
    public void PickUp_SyncFinishing_SendsTheSpot()
    {
        var a = RemoteSong("A");
        using var rig = MobileFixtures.MakeRig(new[] { a });
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var shell = ShellWith(rig, account);
        shell.SetPickUpOnComputer(true);
        shell.Player.PlayTracks(new[] { a }, 0);
        account.SavedQueues.Clear();
        rig.Player.RaisePositionChanged(TimeSpan.FromSeconds(5));
        // The spot moved since the last send: a sync that finishes sends the new one.
        account.RaiseSyncProgress(new NoctisSyncProgress(NoctisSyncStage.Done, 1, 1));
        Assert.Single(account.SavedQueues);
    }

    // ── The publisher ──

    [Fact]
    public async Task Publisher_SendsAtOnceOnFlush_ThenEveryIntervalWhilePlaying_AndNotTheSameSpotTwice()
    {
        var clock = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var id = Guid.NewGuid();
        long position = 0;
        var sent = new List<PlayQueueShare>();
        var publisher = new PickUpPublisher(() => true, () => new PlayQueueShare(new[] { id }, id, position),
            s => { sent.Add(s); return Task.CompletedTask; }, () => clock);

        publisher.Flush();
        await publisher.Pending;
        Assert.Single(sent);

        publisher.Flush();                       // same spot
        position = 500;
        publisher.Flush();                       // half a second on: the same spot
        await publisher.Pending;
        Assert.Single(sent);

        position = 10_000;
        clock += TimeSpan.FromSeconds(10);
        publisher.Tick();                        // too soon
        await publisher.Pending;
        Assert.Single(sent);

        clock += PickUpPublisher.Interval;
        publisher.Tick();
        await publisher.Pending;
        Assert.Equal(2, sent.Count);
        Assert.Equal(10_000, sent[1].PositionMs);
    }

    [Fact]
    public async Task Publisher_AFailedSend_IsDropped_AndTheNextTickTriesAgain()
    {
        var clock = DateTime.UtcNow;
        var id = Guid.NewGuid();
        var attempts = 0;
        var publisher = new PickUpPublisher(() => true, () => new PlayQueueShare(new[] { id }, id, 0),
            _ => { attempts++; return attempts == 1 ? Task.FromException(new NoctisServerException(NoctisErrorKind.Unreachable, "off")) : Task.CompletedTask; },
            () => clock);

        publisher.Flush();
        await publisher.Pending;
        Assert.Equal(1, attempts);

        clock += PickUpPublisher.Interval;
        publisher.Tick();
        await publisher.Pending;
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task Publisher_ARequestDuringASend_SendsAgainAfterIt()
    {
        var id = Guid.NewGuid();
        long position = 0;
        var gate = new TaskCompletionSource();
        var sent = new List<long>();
        var publisher = new PickUpPublisher(() => true, () => new PlayQueueShare(new[] { id }, id, position),
            async s => { sent.Add(s.PositionMs); if (sent.Count == 1) await gate.Task; });

        publisher.Flush();
        position = 60_000;
        publisher.Flush();                       // while the first is out
        Assert.Single(sent);
        gate.SetResult();
        await publisher.Pending;
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(new long[] { 0, 60_000 }, sent);
    }

    [Fact]
    public async Task Publisher_TheSendAgain_CapturesOnTheUiThread_NotTheSendsThread()
    {
        // The repeat ran where the send finished (a pool thread) and walked Player.UpNext there,
        // an ObservableCollection the UI changes.
        var id = Guid.NewGuid();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ui = new List<Action>();
        var captures = 0;
        var sent = new List<long>();
        long position = 0;
        var publisher = new PickUpPublisher(() => true, () => { captures++; return new PlayQueueShare(new[] { id }, id, position); },
            async s => { sent.Add(s.PositionMs); if (sent.Count == 1) await gate.Task; },
            post: a => { lock (ui) ui.Add(a); });

        publisher.Flush();
        position = 60_000;
        publisher.Flush();                       // while the first is out: sends again after it
        gate.SetResult();
        await publisher.Pending;
        Assert.Equal(1, captures);               // not captured on the send's thread
        Action[] queued;
        lock (ui) queued = ui.ToArray();
        Assert.Single(queued);
        queued[0]();                             // the UI thread runs it
        await publisher.Pending;
        Assert.Equal(new long[] { 0, 60_000 }, sent);
    }

    [Fact]
    public void Publisher_Disabled_NeverCaptures()
    {
        var captured = 0;
        var publisher = new PickUpPublisher(() => false, () => { captured++; return null; }, _ => Task.CompletedTask);
        publisher.Flush();
        publisher.Tick();
        Assert.Equal(0, captured);
    }

    // ── The desktop's inbox: newer than the desktop's last playback ──

    [Fact]
    public void Inbox_OffersOnlyAStateNewerThanTheDesktopsLastPlayback_OnceAnswered()
    {
        var inbox = new PhoneHandoffInbox(_dir);
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var h = PhoneHandoff.FromWire(new[] { Guid.NewGuid() }, null, 0, 0, now, "Pixel")!;
        Assert.Null(inbox.Pending(DateTime.MinValue));   // nothing yet

        Assert.True(inbox.Save(h));
        Assert.Same(h, inbox.Pending(now.AddMinutes(-1)));   // the desktop last played before the phone
        Assert.Null(inbox.Pending(now));                     // played here at the same moment: not newer
        Assert.Null(inbox.Pending(now.AddMinutes(1)));       // the desktop played after the phone

        inbox.MarkHandled(h);
        Assert.Null(inbox.Pending(now.AddMinutes(-1)));      // answered: not again

        var newer = PhoneHandoff.FromWire(new[] { Guid.NewGuid() }, null, 0, 0, now.AddMinutes(5), "Pixel")!;
        Assert.True(inbox.Save(newer));
        Assert.Same(newer, inbox.Pending(now));              // a later phone state is a new offer

        Assert.False(inbox.Save(h));                         // an older one (a slower phone) never replaces it
        Assert.Same(newer, inbox.Latest);
    }

    [Fact]
    public void Inbox_SurvivesARestart_WithTheAnswerAndTheDesktopsPlayback()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var first = new PhoneHandoffInbox(_dir);
        var h = PhoneHandoff.FromWire(new[] { Guid.NewGuid(), Guid.NewGuid() }, null, 4_000, 0, now, "Pixel")!;
        first.Save(h);
        first.NoteDesktopPlayback(now.AddHours(-2));

        var again = new PhoneHandoffInbox(_dir);
        var pending = again.Pending(again.DesktopPlaybackUtc)!;
        Assert.Equal(h.TrackIds, pending.TrackIds);
        Assert.Equal(h.SavedUtc, pending.SavedUtc);
        Assert.Equal(now.AddHours(-2), again.DesktopPlaybackUtc);

        again.MarkHandled(pending);
        Assert.Null(new PhoneHandoffInbox(_dir).Pending(DateTime.MinValue));
    }

    [Fact]
    public void Inbox_TheSameSpotSentAgain_KeepsItsTime_SoAnAnsweredOfferDoesNotComeBack()
    {
        // A paused phone whose app restarted forgets what it sent and sends the same spot again
        // (a finished sync flushes it). It must not become a new, newer offer.
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var ids = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var inbox = new PhoneHandoffInbox(_dir);
        var h = PhoneHandoff.FromWire(ids, ids[1], 83_000, 0, now, "Pixel")!;
        inbox.Save(h);
        inbox.MarkHandled(h);                        // the user took it (or dismissed it)
        inbox.NoteDesktopPlayback(now.AddMinutes(10)); // and played here since

        var again = PhoneHandoff.FromWire(ids, ids[1], 83_400, 0, now.AddHours(1), "Pixel")!;
        inbox.Save(again);

        Assert.Null(inbox.Pending(inbox.DesktopPlaybackUtc));
        Assert.Equal(now, inbox.Latest!.SavedUtc);
        Assert.Null(new PhoneHandoffInbox(_dir).Pending(DateTime.MinValue)); // also after a restart

        // A real move on the phone is still a new offer.
        var moved = PhoneHandoff.FromWire(ids, ids[1], 120_000, 0, now.AddHours(2), "Pixel")!;
        inbox.Save(moved);
        Assert.Same(moved, inbox.Pending(inbox.DesktopPlaybackUtc));
    }

    [Fact]
    public void Inbox_AnUnreadableFile_IsIgnored()
    {
        File.WriteAllText(Path.Combine(_dir, "phone_handoff.json"), "{not json");
        var inbox = new PhoneHandoffInbox(_dir);
        Assert.Null(inbox.Latest);
        Assert.Null(inbox.Pending(DateTime.MinValue));
    }

    // ── The desktop's prompt ──

    private sealed class ManualClock
    {
        public List<(TimeSpan Delay, Action Run, Handle Handle)> Scheduled { get; } = new();
        public IDisposable Schedule(TimeSpan delay, Action run)
        {
            var handle = new Handle();
            Scheduled.Add((delay, run, handle));
            return handle;
        }
        public sealed class Handle : IDisposable { public bool Cancelled; public void Dispose() => Cancelled = true; }
    }

    private (Noctis.ViewModels.HandoffPromptViewModel Prompt, PhoneHandoffInbox Inbox, List<PhoneHandoff> Loaded, ManualClock Clock) PromptRig(
        DateTime now, Guid track, string title, bool playingHere = false)
    {
        var inbox = new PhoneHandoffInbox(_dir);
        var clock = new ManualClock();
        var loaded = new List<PhoneHandoff>();
        var prompt = new Noctis.ViewModels.HandoffPromptViewModel(clock.Schedule);
        prompt.Attach(inbox, h => { loaded.Add(h); return true; }, id => id == track ? title : null, () => playingHere, () => now);
        return (prompt, inbox, loaded, clock);
    }

    [AvaloniaFact]
    public void Prompt_OffersThePhonesSong_WhenNewer_AndContinueLoadsItOnce()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var song = Guid.NewGuid();
        var (prompt, inbox, loaded, _) = PromptRig(now, song, "Grenade");
        inbox.NoteDesktopPlayback(now.AddHours(-3));
        inbox.Save(PhoneHandoff.FromWire(new[] { Guid.NewGuid(), song }, song, 83_000, 0, now.AddMinutes(-1), "Pixel")!);

        prompt.Check();
        Assert.True(prompt.IsShown);
        Assert.Equal("Grenade", prompt.Detail);
        Assert.Empty(loaded);   // never loaded until the user says so

        prompt.ContinueCommand.Execute(null);
        var h = Assert.Single(loaded);
        Assert.Equal(83_000, h.PositionMs);
        Assert.False(prompt.IsShown);

        prompt.Check();         // answered: the next time the window comes forward it stays quiet
        Assert.False(prompt.IsShown);
    }

    [AvaloniaFact]
    public void Prompt_StaysQuiet_WhenTheDesktopPlayedAfterThePhone_OrIsPlayingNow_OrTheSongIsUnknown()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var song = Guid.NewGuid();

        var (played, inbox1, _, _) = PromptRig(now, song, "Grenade");
        inbox1.Save(PhoneHandoff.FromWire(new[] { song }, song, 0, 0, now.AddMinutes(-10), "Pixel")!);
        inbox1.NoteDesktopPlayback(now.AddMinutes(-5));
        played.Check();
        Assert.False(played.IsShown);

        var dir2 = Path.Combine(_dir, "b");
        Directory.CreateDirectory(dir2);
        var inbox2 = new PhoneHandoffInbox(dir2);
        var playing = new Noctis.ViewModels.HandoffPromptViewModel(new ManualClock().Schedule);
        playing.Attach(inbox2, _ => true, _ => "Grenade", () => true, () => now);
        inbox2.Save(PhoneHandoff.FromWire(new[] { song }, song, 0, 0, now.AddMinutes(-1), "Pixel")!);
        playing.Check();
        Assert.False(playing.IsShown);

        var (unknown, inbox3, _, _) = PromptRig(now, Guid.NewGuid(), "Other");
        inbox3.Save(PhoneHandoff.FromWire(new[] { song }, song, 0, 0, now.AddMinutes(-1), "Pixel")!);
        unknown.Check();
        Assert.False(unknown.IsShown);   // the song is not in this library
    }

    [AvaloniaFact]
    public void Prompt_GoesByItself_AndDismissingAnswersIt()
    {
        var now = new DateTime(2026, 10, 28, 12, 0, 0, DateTimeKind.Utc);
        var song = Guid.NewGuid();
        var (prompt, inbox, loaded, clock) = PromptRig(now, song, "Grenade");
        inbox.Save(PhoneHandoff.FromWire(new[] { song }, song, 0, 0, now.AddMinutes(-1), "Pixel")!);

        prompt.Check();
        var (delay, run, _) = Assert.Single(clock.Scheduled);
        Assert.Equal(Noctis.ViewModels.HandoffPromptViewModel.HoldDuration, delay);
        run();   // the hold ran out

        Assert.False(prompt.IsShown);
        Assert.Empty(loaded);
        prompt.Check();
        Assert.False(prompt.IsShown);
    }

    // ── Old peers ──

    /// <summary>A desktop from before Pick Up: knows no savePlayQueue (the Subsonic "not implemented" error).</summary>
    [Fact]
    public async Task OldDesktop_RefusesSavePlayQueue_AndThePhoneCarriesOn()
    {
        var handler = new ScriptedHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"code\":70,\"message\":\"unknown\"}}}", Encoding.UTF8, "application/json"),
        });
        using var client = new NoctisServerClient(_ => handler, "http://127.0.0.1:4533", "", "nk_key");
        var id = Guid.NewGuid();
        var share = new PlayQueueShare(new[] { id }, id, 0);

        await Assert.ThrowsAsync<NoctisServerException>(() => client.SavePlayQueueAsync(share, TestContext.Current.CancellationToken));

        // Through the publisher the failure is only logged.
        var publisher = new PickUpPublisher(() => true, () => share, s => client.SavePlayQueueAsync(s, CancellationToken.None));
        publisher.Flush();
        await publisher.Pending;
    }

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    // ── Phone → real server → desktop ──

    private sealed class Desk : IServerLibrary
    {
        public Desk(IEnumerable<Track> tracks, PhoneHandoffInbox inbox) { Tracks = tracks.ToList(); Inbox = inbox; }
        public List<Track> Tracks { get; }
        public PhoneHandoffInbox Inbox { get; }
        public Task<LibrarySnapshot> SnapshotAsync() => Task.FromResult(new LibrarySnapshot(Tracks.ToList(), new List<Album>(), new List<Artist>(), new List<Playlist>()));
        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> t, IReadOnlyList<Guid> al, IReadOnlyList<Guid> ar, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => Task.FromResult(new Playlist { Name = name });
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(true);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(true);
        public Task ApplyTrackStateAsync(Guid trackId, Noctis.Services.Sync.TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, Noctis.Services.Sync.PlaylistSyncState state) => Task.CompletedTask;
        public Task SavePlayQueueAsync(PhoneHandoff handoff) { Inbox.Save(handoff); return Task.CompletedTask; }
        public PhoneHandoff? PlayQueue => Inbox.Latest;
    }

    /// <summary>The same desk without the Pick Up members: what a library written before them gets (the defaults).</summary>
    private sealed class OldDesk(Desk inner) : IServerLibrary
    {
        public Task<LibrarySnapshot> SnapshotAsync() => inner.SnapshotAsync();
        public string? ArtworkPath(Guid albumId) => null;
        public Task SetStarredAsync(IReadOnlyList<Guid> t, IReadOnlyList<Guid> al, IReadOnlyList<Guid> ar, bool starred) => Task.CompletedTask;
        public Task ScrobbleAsync(Guid trackId) => Task.CompletedTask;
        public Task<Playlist> CreatePlaylistAsync(string name, IReadOnlyList<Guid> trackIds) => inner.CreatePlaylistAsync(name, trackIds);
        public Task<bool> UpdatePlaylistAsync(Guid id, string? name, IReadOnlyList<Guid> add, IReadOnlyList<int> removeIndexes) => Task.FromResult(true);
        public Task<bool> DeletePlaylistAsync(Guid id) => Task.FromResult(true);
        public Task ApplyTrackStateAsync(Guid trackId, Noctis.Services.Sync.TrackSyncState state) => Task.CompletedTask;
        public Task ApplyPlaylistStateAsync(Guid playlistId, Noctis.Services.Sync.PlaylistSyncState state) => Task.CompletedTask;
    }

    private async Task<(NoctisServer Server, NoctisServerClient Client, HttpClient Http, string Key)> StartAsync(IServerLibrary library)
    {
        var users = new ServerUserStore(Path.Combine(_dir, "users-" + Guid.NewGuid().ToString("N") + ".db"));
        users.Create("alice", "correct horse", isAdmin: true);
        var key = users.RegenerateApiKey("alice");
        var server = new NoctisServer(library, users, "test") { PrivateClientsOnly = true };
        await server.StartAsync(0, certificate: null, bindAddress: IPAddress.Loopback);
        var url = $"http://127.0.0.1:{server.Port}";
        return (server, new NoctisServerClient(NoctisHandlers.Sockets, url, "", key), new HttpClient { BaseAddress = new Uri(url + "/") }, key);
    }

    [Fact]
    public async Task SavePlayQueue_RoundTrips_PhoneToServerToTheInbox_AndGetPlayQueueReturnsIt()
    {
        var file = Path.Combine(_dir, "song.mp3");   // the server lists only songs whose file is there
        File.WriteAllBytes(file, new byte[] { 1, 2, 3 });
        var songs = new[] { "One", "Two", "Three" }.Select(t => new Track { Id = Guid.NewGuid(), Title = t, Artist = "A", Album = "Al", AlbumId = Guid.NewGuid(), FilePath = file }).ToList();
        var inbox = new PhoneHandoffInbox(Path.Combine(_dir, "desk"));
        var (server, client, http, key) = await StartAsync(new Desk(songs, inbox));
        try
        {
            var before = DateTime.UtcNow;
            await client.SavePlayQueueAsync(new PlayQueueShare(songs.Select(s => s.Id).ToList(), songs[1].Id, 83_250), TestContext.Current.CancellationToken);

            var h = Assert.IsType<PhoneHandoff>(inbox.Latest);
            Assert.Equal(songs.Select(s => s.Id), h.TrackIds);
            Assert.Equal(songs[1].Id, h.CurrentId);
            Assert.Equal(83_250, h.PositionMs);
            Assert.InRange(h.SavedUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));
            Assert.NotEmpty(h.DeviceName);
            Assert.NotNull(inbox.Pending(before.AddMinutes(-5)));   // newer than a desktop that last played earlier

            var json = await http.GetStringAsync($"rest/getPlayQueue.view?f=json&apiKey={key}", TestContext.Current.CancellationToken);
            var q = JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response").GetProperty("playQueue");
            Assert.Equal("tr-" + songs[1].Id.ToString("N"), q.GetProperty("current").GetString());
            Assert.Equal(83_250, q.GetProperty("position").GetInt64());
            Assert.Equal(new[] { "One", "Two", "Three" }, q.GetProperty("entry").EnumerateArray().Select(e => e.GetProperty("title").GetString()));
        }
        finally
        {
            http.Dispose();
            await server.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task ADesktopLibraryWithoutPickUp_AcceptsTheCall_AndGetPlayQueueStaysEmpty()
    {
        // A host written before Pick Up (the default interface members): the phone's save is a quiet no-op.
        var inbox = new PhoneHandoffInbox(Path.Combine(_dir, "old"));
        var (server, client, http, key) = await StartAsync(new OldDesk(new Desk(Array.Empty<Track>(), inbox)));
        try
        {
            var id = Guid.NewGuid();
            await client.SavePlayQueueAsync(new PlayQueueShare(new[] { id }, id, 1000), TestContext.Current.CancellationToken);

            Assert.Null(inbox.Latest);
            var json = await http.GetStringAsync($"rest/getPlayQueue.view?f=json&apiKey={key}", TestContext.Current.CancellationToken);
            var r = JsonDocument.Parse(json).RootElement.GetProperty("subsonic-response");
            Assert.Equal("ok", r.GetProperty("status").GetString());
            Assert.False(r.TryGetProperty("playQueue", out _));   // what an old phone or client always got
        }
        finally
        {
            http.Dispose();
            await server.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task AnEmptySave_AndGarbageIds_LeaveNothingToOffer()
    {
        var inbox = new PhoneHandoffInbox(Path.Combine(_dir, "empty"));
        var (server, _, http, key) = await StartAsync(new Desk(Array.Empty<Track>(), inbox));
        try
        {
            await http.PostAsync($"rest/savePlayQueue.view?f=json&apiKey={key}", new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("id", "not-an-id"),
                new KeyValuePair<string, string>("current", "nope"),
                new KeyValuePair<string, string>("position", "-5"),
            }), TestContext.Current.CancellationToken);
            await http.GetStringAsync($"rest/savePlayQueue.view?f=json&apiKey={key}", TestContext.Current.CancellationToken);   // no ids at all
            Assert.Null(inbox.Latest);
        }
        finally
        {
            http.Dispose();
            await server.StopAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    // ── The pane ──

    [AvaloniaFact]
    public void Pane_RendersTheNumbers_AndTheRows()
    {
        var songs = Enumerable.Range(1, 3).Select(i => MobileFixtures.Song("Song " + i)).ToArray();
        using var rig = MobileFixtures.MakeRig(songs, log: h =>
        {
            h.Seed(songs[0], DateTime.UtcNow.AddMinutes(-30));
            h.Seed(songs[1], DateTime.UtcNow.AddMinutes(-20));
            h.Seed(songs[2], DateTime.UtcNow.AddMinutes(-2));
        });
        MobileFixtures.EnsurePhonePalette();
        var pane = new ListeningHistoryPane { DataContext = new ListeningHistoryViewModel(rig.Shell) };
        var window = new Avalonia.Controls.Window { Width = 412, Height = 915, Content = pane };
        window.Show();
        window.UpdateLayout();

        Assert.Equal("2 min", MobileFixtures.Named<Avalonia.Controls.TextBlock>(pane, "WeekTotalLabel").Text);
        Assert.Equal("3 plays", MobileFixtures.Named<Avalonia.Controls.TextBlock>(pane, "WeekPlaysLabel").Text);
        Assert.Equal("Forever", MobileFixtures.Named<Avalonia.Controls.TextBlock>(pane, "KeepValueLabel").Text);
        Assert.True(MobileFixtures.Named<Avalonia.Controls.Button>(pane, "ShowAllButton").IsVisible);
        Assert.False(MobileFixtures.Named<Avalonia.Controls.Grid>(pane, "NoRecentRow").IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pane_InTheSheet_TheWeekCaptionLinesUpWithTheTotal_AndKeepHistoryHasItsChooserGlyph()
    {
        // Live: inside the Settings sheet "This Week" sat indented, centred-ish, over "6 min".
        using var rig = MobileFixtures.MakeRig();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSettingsCommand.Execute(null);
        var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await settings.Loaded;
        settings.OpenPaneCommand.Execute(SettingsPane.History);
        MobileFixtures.Land(window);

        var pane = MobileFixtures.Find<ListeningHistoryPane>(view);
        var card = MobileFixtures.Named<Avalonia.Controls.Border>(pane, "WeekCard");
        double Left(string name) => MobileFixtures.Named<Avalonia.Controls.TextBlock>(pane, name).TranslatePoint(default, card)!.Value.X;
        Assert.Equal(Left("WeekTotalLabel"), Left("WeekCaption"), 1);
        var keepRow = MobileFixtures.Named<Avalonia.Controls.Button>(pane, "KeepRow");
        Assert.Contains(keepRow.GetVisualDescendants().OfType<GridIcon>(), g => g.Classes.Contains("row-trailing"));
        window.Close();
    }

    private static ShellViewModel ShellWith(MobileFixtures.Rig rig, INoctisAccountService? account) =>
        new(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };
}
