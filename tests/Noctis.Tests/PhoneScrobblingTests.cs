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
using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.Services.Scrobbling;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Scrobbling from the phone: the counting rule, the persistent queue, retry with backoff, batches of 50,
/// and above all that a play is sent exactly once whichever route is on and however often the route flips.
/// All HTTP is faked: nothing here reaches Last.fm or ListenBrainz.</summary>
public sealed class PhoneScrobblingTests : IDisposable
{
    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }

    /// <summary>Answers per service; records every scrobble call's play count and its (artist, timestamp) pairs.</summary>
    private sealed class FakeServices : HttpMessageHandler
    {
        public Queue<HttpStatusCode> LastFmStatus { get; } = new();
        public Queue<HttpStatusCode> LbStatus { get; } = new();
        public string? LastFmBody { get; set; }
        public List<int> LastFmCalls { get; } = new();
        public List<string> LastFmTimes { get; } = new();
        public List<int> LbCalls { get; } = new();
        public List<string> LbTimes { get; } = new();
        public int NowPlayingCalls { get; private set; }
        public string ValidateAs { get; set; } = "ben";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var url = request.RequestUri!.ToString();
            if (request.Method == HttpMethod.Post && url.StartsWith("https://ws.audioscrobbler.com", StringComparison.Ordinal))
            {
                var form = (await request.Content!.ReadAsStringAsync(ct)).Split('&')
                    .Select(p => p.Split('=')).ToDictionary(p => WebUtility.UrlDecode(p[0]), p => WebUtility.UrlDecode(p[1]));
                if (form["method"] == "track.updateNowPlaying") { NowPlayingCalls++; return Json(HttpStatusCode.OK, "{}"); }
                var count = form.Keys.Count(k => k.StartsWith("artist[", StringComparison.Ordinal));
                var status = LastFmStatus.Count > 0 ? LastFmStatus.Dequeue() : HttpStatusCode.OK;
                if (status == HttpStatusCode.OK)
                {
                    LastFmCalls.Add(count);
                    for (var i = 0; i < count; i++) LastFmTimes.Add(form[$"timestamp[{i}]"]);
                }
                return Json(status, LastFmBody ?? "{\"scrobbles\":{}}");
            }
            if (url.EndsWith("/1/validate-token", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, $"{{\"valid\":true,\"user_name\":\"{ValidateAs}\"}}");
            if (url.EndsWith("/1/submit-listens", StringComparison.Ordinal))
            {
                using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                if (doc.RootElement.GetProperty("listen_type").GetString() == "playing_now") { NowPlayingCalls++; return Json(HttpStatusCode.OK, "{}"); }
                var payload = doc.RootElement.GetProperty("payload");
                var status = LbStatus.Count > 0 ? LbStatus.Dequeue() : HttpStatusCode.OK;
                if (status == HttpStatusCode.OK)
                {
                    LbCalls.Add(payload.GetArrayLength());
                    foreach (var l in payload.EnumerateArray()) LbTimes.Add(l.GetProperty("listened_at").GetInt64().ToString());
                }
                return Json(status, "{}");
            }
            if (url.Contains("method=auth.getToken", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"token\":\"tok123\"}");
            if (url.Contains("method=auth.getSession", StringComparison.Ordinal))
                return Json(HttpStatusCode.OK, "{\"session\":{\"name\":\"ferlin\",\"key\":\"sess-key\"}}");
            if (url.Contains("method=user.getinfo", StringComparison.Ordinal)) return Json(HttpStatusCode.OK, "{\"user\":{\"playcount\":\"14208\"}}");
            return Json(HttpStatusCode.NotFound, "{}");
        }

        private static HttpResponseMessage Json(HttpStatusCode code, string body)
            => new(code) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    }

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly FakeServices _http = new();
    private readonly ManualTime _time = new();

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private PersistenceService NewPersistence() => new(_root);

    private PhoneScrobbler NewScrobbler(PersistenceService? persistence = null)
        => new(persistence ?? NewPersistence(), new HttpClient(_http), _time) { AutoFlush = false };

    private async Task<PhoneScrobbler> Connected(bool fromPhone = true, bool lastFm = true, bool listenBrainz = false, PersistenceService? persistence = null)
    {
        var persistence1 = persistence ?? NewPersistence();
        await persistence1.UpdateSettingsAsync(s =>
        {
            s.MobileScrobbleFromPhone = fromPhone;
            s.LastFmScrobblingEnabled = lastFm;
            s.LastFmSessionKey = lastFm ? "sk" : "";
            s.LastFmUsername = lastFm ? "ferlin" : "";
            s.ListenBrainzScrobblingEnabled = listenBrainz;
            s.ListenBrainzToken = listenBrainz ? "lbtoken" : "";
            s.ListenBrainzUsername = listenBrainz ? "ben" : "";
        });
        var scrobbler = NewScrobbler(persistence1);
        await scrobbler.LoadAsync();
        return scrobbler;
    }

    private static Track Song(string title, int seconds = 200, string artist = "Artist") => new()
    {
        Title = title, Artist = artist, Album = "Album", Duration = TimeSpan.FromSeconds(seconds), FilePath = "/m/" + title + ".mp3",
    };

    /// <summary>Plays <paramref name="track"/> for <paramref name="seconds"/> of audible time, ticking once a second.</summary>
    private void Play(PhoneScrobbler s, Track track, int seconds)
    {
        s.TrackStarted(track);
        for (var i = 0; i < seconds; i++)
        {
            _time.Advance(TimeSpan.FromSeconds(1));
            s.Tick(true);
        }
    }

    // ── The counting rule ──

    [Fact]
    public async Task APlayCountsAfterHalfTheSong_Once()
    {
        var s = await Connected();
        var song = Song("A", seconds: 200);
        Play(s, song, 99);
        Assert.Equal(0, s.PendingCount);              // not yet half
        Play(s, song, 101);                            // a fresh start, 101 s played
        Assert.Equal(1, s.PendingCount);
        for (var i = 0; i < 30; i++) { _time.Advance(TimeSpan.FromSeconds(1)); s.Tick(true); }
        Assert.Equal(1, s.PendingCount);              // played on: still the one play
    }

    [Fact]
    public async Task EveryPlay_GetsOneVerdict_ForTheComputer_ByTheRouteWhenItCounted()
    {
        // The account recorded each play when it started; this says who sends it, if anyone.
        var s = await Connected(fromPhone: false);
        var verdicts = new List<(string Title, PlayScrobble Verdict)>();
        s.PlayDecided += (t, v) => verdicts.Add((t.Title, v));

        Play(s, Song("Counted"), 101);                                     // Through Computer: the computer sends it
        Play(s, Song("Skipped"), 20);                                      // skipped: decided when the next starts
        await s.SetFromPhoneAsync(true);
        Play(s, Song("Mine"), 101);                                        // From This Phone: sent here, never by the computer
        for (var i = 0; i < 30; i++) { _time.Advance(TimeSpan.FromSeconds(1)); s.Tick(true); }

        Assert.Equal(new[] { ("Counted", PlayScrobble.Computer), ("Skipped", PlayScrobble.No), ("Mine", PlayScrobble.Phone) }, verdicts);
        Assert.Equal(1, s.PendingCount);                                   // only "Mine" is queued on the phone
    }

    [Fact]
    public async Task TimePausedAndSongsUnder30SecondsDoNotCount()
    {
        var s = await Connected();
        var song = Song("A", seconds: 200);
        s.TrackStarted(song);
        for (var i = 0; i < 60; i++) { _time.Advance(TimeSpan.FromSeconds(1)); s.Tick(true); }
        for (var i = 0; i < 600; i++) { _time.Advance(TimeSpan.FromSeconds(1)); s.Tick(false); }   // ten minutes paused
        Assert.Equal(0, s.PendingCount);              // 60 s of 200 s
        Play(s, Song("Short", seconds: 25), 25);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public async Task LongSongCountsAtFourMinutes()
    {
        var s = await Connected();
        Play(s, Song("Epic", seconds: 900), 239);
        Assert.Equal(0, s.PendingCount);
        _time.Advance(TimeSpan.FromSeconds(1)); s.Tick(true);
        Assert.Equal(1, s.PendingCount);
    }

    [Fact]
    public async Task ExcludedSongsAreNeverQueued()
    {
        var persistence = NewPersistence();
        var song = Song("Secret");
        await persistence.UpdateSettingsAsync(s => s.ScrobbleExcludedKeys = new List<string> { ScrobbleExclusionKeys.ForTrack(song.Id) });
        var s = await Connected(persistence: persistence);
        Play(s, song, 150);
        Assert.Equal(0, s.PendingCount);
    }

    // ── Exactly once ──

    [Fact]
    public async Task ThroughComputer_ThePhoneSendsNothingItself()
    {
        var s = await Connected(fromPhone: false);
        Play(s, Song("A"), 150);
        await s.FlushAsync();
        Assert.Equal(0, s.PendingCount);
        Assert.Empty(_http.LastFmCalls);
    }

    [Fact]
    public async Task FromPhone_AQueuedPlayIsSentOnceAndNotAgain()
    {
        var s = await Connected();
        Play(s, Song("A"), 150);
        Assert.Equal(1, s.PendingCount);
        await s.FlushAsync();
        await s.FlushAsync();
        await s.FlushAsync();
        Assert.Equal(new[] { 1 }, _http.LastFmCalls);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public async Task SwitchingTheRoute_NeitherResendsNorDropsWhatIsQueued()
    {
        var s = await Connected();                      // From This Phone, offline
        _http.LastFmStatus.Enqueue(HttpStatusCode.ServiceUnavailable);
        Play(s, Song("One"), 150);
        await s.FlushAsync();                           // fails: stays queued
        Assert.Equal(1, s.PendingCount);

        await s.SetFromPhoneAsync(false);               // Through Computer: later plays go to the computer
        Play(s, Song("Two"), 150);
        Assert.Equal(1, s.PendingCount);                // "Two" was not queued here: the account sync carries it

        _time.Advance(TimeSpan.FromMinutes(1));
        await s.FlushAsync();                           // "One" is still the phone's: sent now, once
        Assert.Equal(new[] { 1 }, _http.LastFmCalls);

        await s.SetFromPhoneAsync(true);
        await s.SetFromPhoneAsync(false);
        await s.SetFromPhoneAsync(true);
        await s.FlushAsync();
        Assert.Equal(new[] { 1 }, _http.LastFmCalls);   // flipping back and forth sent nothing more
        Assert.Equal(0, s.PendingCount);

        Play(s, Song("Three"), 150);
        await s.FlushAsync();
        Assert.Equal(new[] { 1, 1 }, _http.LastFmCalls);
        Assert.Equal(_http.LastFmTimes.Distinct().Count(), _http.LastFmTimes.Count);   // every play once
    }

    [Fact]
    public async Task EveryPlayLandsExactlyOnce_AcrossFailuresRestartsAndFlips()
    {
        var persistence = NewPersistence();
        var s = await Connected(lastFm: true, listenBrainz: true, persistence: persistence);
        _http.LastFmStatus.Enqueue(HttpStatusCode.InternalServerError);     // Last.fm down for the first try
        for (var i = 0; i < 7; i++)
        {
            Play(s, Song("S" + i), 150);
            if (i == 2) await s.SetFromPhoneAsync(false);
            if (i == 3) await s.SetFromPhoneAsync(true);
            if (i == 3)
            {
                await s.FlushAsync();                                     // LB takes what it owes; Last.fm is backing off
                s = NewScrobbler(persistence);                            // the app restarts
                await s.LoadAsync();
                _time.Advance(TimeSpan.FromMinutes(5));
            }
        }
        // Song 2 was counted before the flip to Through Computer; songs after it up to the flip back were not queued.
        await s.FlushAsync();
        await s.FlushAsync();
        Assert.Equal(_http.LastFmTimes.Count, _http.LastFmTimes.Distinct().Count());
        Assert.Equal(_http.LbTimes.Count, _http.LbTimes.Distinct().Count());
        Assert.Equal(_http.LastFmTimes.OrderBy(x => x), _http.LbTimes.OrderBy(x => x));   // both services got the same plays
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public async Task Services_AreIndependent_OneFailingDoesNotResendTheOther()
    {
        var s = await Connected(lastFm: true, listenBrainz: true);
        _http.LbStatus.Enqueue(HttpStatusCode.BadGateway);
        Play(s, Song("A"), 150);
        await s.FlushAsync();
        Assert.Equal(new[] { 1 }, _http.LastFmCalls);
        Assert.Empty(_http.LbCalls);
        Assert.Equal(1, s.PendingCount);                // still owed to ListenBrainz
        _time.Advance(TimeSpan.FromMinutes(1));
        await s.FlushAsync();
        Assert.Equal(new[] { 1 }, _http.LastFmCalls);   // Last.fm not asked again
        Assert.Equal(new[] { 1 }, _http.LbCalls);
        Assert.Equal(0, s.PendingCount);
    }

    // ── Queue ──

    [Fact]
    public async Task TheQueueSurvivesARestart_AndIsSentOnce()
    {
        var persistence = NewPersistence();
        var s = await Connected(persistence: persistence);
        Play(s, Song("A"), 150);
        Play(s, Song("B"), 150);
        Assert.True(File.Exists(Path.Combine(_root, "scrobble_queue.json")));

        var restarted = NewScrobbler(persistence);
        await restarted.LoadAsync();
        Assert.Equal(2, restarted.PendingCount);
        await restarted.FlushAsync();
        await restarted.FlushAsync();
        Assert.Equal(new[] { 2 }, _http.LastFmCalls);
        Assert.Equal(0, NewScrobbler(persistence).PendingCount);
    }

    [Fact]
    public async Task SendsInBatchesOfFifty()
    {
        var s = await Connected();
        for (var i = 0; i < 120; i++) s.Enqueue(Song("S" + i), _time.GetUtcNow().UtcDateTime.AddMinutes(-i - 5));
        await s.FlushAsync();
        Assert.Equal(new[] { 50, 50, 20 }, _http.LastFmCalls);
        Assert.Equal(120, _http.LastFmTimes.Distinct().Count());
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public async Task OfflineRetriesWithBackoff_KeepingTheBatchWhole()
    {
        var s = await Connected();
        for (var i = 0; i < 3; i++) s.Enqueue(Song("S" + i), _time.GetUtcNow().UtcDateTime.AddMinutes(-i - 5));
        for (var i = 0; i < 3; i++) _http.LastFmStatus.Enqueue(HttpStatusCode.ServiceUnavailable);
        await s.FlushAsync();                           // 1st failure: 30 s
        Assert.Equal(3, s.PendingCount);
        await s.FlushAsync();                           // inside the backoff: not even tried
        Assert.Equal(2, _http.LastFmStatus.Count);
        _time.Advance(TimeSpan.FromSeconds(31));
        await s.FlushAsync();                           // 2nd failure: 60 s
        Assert.Single(_http.LastFmStatus);
        _time.Advance(TimeSpan.FromSeconds(45));
        await s.FlushAsync();                           // still waiting
        Assert.Single(_http.LastFmStatus);
        _time.Advance(TimeSpan.FromSeconds(20));
        await s.FlushAsync();                           // 3rd failure
        Assert.Empty(_http.LastFmStatus);
        _time.Advance(TimeSpan.FromMinutes(5));
        await s.FlushAsync();                           // back online: all three, in one call
        Assert.Equal(new[] { 3 }, _http.LastFmCalls);
        Assert.Equal(0, s.PendingCount);
    }

    [Fact]
    public async Task ARejectedPlayIsDropped_ADeadSessionKeepsTheQueueAndSignsOut()
    {
        var s = await Connected();
        s.Enqueue(Song("Bad"), _time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        _http.LastFmBody = "{\"error\":6,\"message\":\"Invalid parameters\"}";
        await s.FlushAsync();
        Assert.Equal(0, s.PendingCount);                // never going to be accepted: not retried forever

        s.Enqueue(Song("Good"), _time.GetUtcNow().UtcDateTime.AddMinutes(-4));
        _http.LastFmBody = "{\"error\":9,\"message\":\"Invalid session key\"}";
        await s.FlushAsync();
        Assert.False(s.LastFmConnected);
        _http.LastFmBody = null;
        Assert.Equal(0, s.PendingCount);                // not owed to a signed-out service...
        var settings = await NewPersistence().LoadSettingsAsync();
        Assert.False(settings.LastFmScrobblingEnabled);
    }

    [Fact]
    public async Task Disconnect_DropsWhatWasQueuedForThatService()
    {
        var s = await Connected(lastFm: true, listenBrainz: true);
        s.Enqueue(Song("A"), _time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await s.DisconnectAsync(lastFm: true);
        await s.FlushAsync();
        Assert.Empty(_http.LastFmCalls);
        Assert.Equal(new[] { 1 }, _http.LbCalls);
        Assert.False(s.LastFmConnected);
        Assert.True(s.ListenBrainzConnected);
    }

    [Fact]
    public async Task SendPlaysOff_QueuesNothingNewAndHoldsTheQueue()
    {
        var s = await Connected();
        s.Enqueue(Song("A"), _time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        await s.SetSendPlaysAsync(false);
        Play(s, Song("B"), 150);
        await s.FlushAsync();
        Assert.Empty(_http.LastFmCalls);
        Assert.Equal(1, s.PendingCount);
        await s.SetSendPlaysAsync(true);
        await s.FlushAsync();
        Assert.Equal(new[] { 1 }, _http.LastFmCalls);
    }

    [Fact]
    public async Task NowPlayingIsSentOnlyFromThePhoneWhenAsked()
    {
        var s = await Connected();
        s.TrackStarted(Song("A"));
        await Task.Delay(50);
        Assert.Equal(1, _http.NowPlayingCalls);
        await s.SetShowNowPlayingAsync(false);
        s.TrackStarted(Song("B"));
        await s.SetFromPhoneAsync(false);
        await s.SetShowNowPlayingAsync(true);
        s.TrackStarted(Song("C"));
        await Task.Delay(50);
        Assert.Equal(1, _http.NowPlayingCalls);
    }

    [Fact]
    public async Task SavingTheScrobbleSettings_LeavesEveryOtherFieldAlone()
    {
        var persistence = NewPersistence();
        var s = await Connected(persistence: persistence);
        await persistence.UpdateSettingsAsync(x => x.MobileArtworkPageColors = false);   // changed elsewhere after load
        await s.SetSendPlaysAsync(false);
        var saved = await persistence.LoadSettingsAsync();
        Assert.False(saved.MobileScrobbleSendPlays);
        Assert.False(saved.MobileArtworkPageColors);
    }

    // ── Connecting ──

    [Fact]
    public async Task LastFmWebAuth_TokenThenSession()
    {
        var s = await Connected(lastFm: false);
        var url = await s.BeginLastFmAuthAsync();
        Assert.StartsWith("https://www.last.fm/api/auth/?api_key=", url);
        Assert.EndsWith("&token=tok123", url);
        Assert.True(await s.CompleteLastFmAuthAsync());
        Assert.True(s.LastFmConnected);
        Assert.Equal("ferlin", s.LastFmUser);
        var saved = await NewPersistence().LoadSettingsAsync();
        Assert.Equal("sess-key", saved.LastFmSessionKey);
        Assert.True(saved.LastFmScrobblingEnabled);
    }

    // ── The page ──

    private (ShellViewModel Shell, PhoneScrobbler Scrobbler) NewShell(PhoneScrobbler scrobbler)
    {
        var rig = MobileFixtures.MakeRig();
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Scrobbler = scrobbler, Marshal = a => a() };
        return (shell, scrobbler);
    }

    [AvaloniaFact]
    public async Task Page_SummaryNamesWhatThePhoneSendsTo()
    {
        var (shell, scrobbler) = NewShell(await Connected(lastFm: true, listenBrainz: false));
        var vm = new ScrobblingSettingsViewModel(shell);
        Assert.Equal("Last.fm", vm.Summary);
        await scrobbler.ConnectListenBrainzAsync("lbtoken");
        Assert.Equal("Last.fm + ListenBrainz", vm.Summary);
        await scrobbler.DisconnectAsync(lastFm: true);
        Assert.Equal("ListenBrainz", vm.Summary);
        await scrobbler.DisconnectAsync(lastFm: false);
        Assert.Equal("Off", vm.Summary);
    }

    [AvaloniaFact]
    public async Task Page_ThroughComputerSaysSetUpOnYourComputer_AndRouteSwitchPersists()
    {
        var (shell, scrobbler) = NewShell(await Connected(fromPhone: false));
        var vm = new ScrobblingSettingsViewModel(shell);
        Assert.True(vm.IsThroughComputer);
        Assert.Equal("Set up on your computer", vm.LastFmSubtitle);
        Assert.Equal("Set up on your computer", vm.ListenBrainzSubtitle);
        Assert.False(vm.PhoneControlsEnabled);
        Assert.Equal("Off", vm.Summary);                                    // nothing is sent from the phone

        vm.SelectFromPhoneCommand.Execute(null);
        Assert.True(scrobbler.FromPhone);
        Assert.Equal("Connected", vm.LastFmStatus);
        Assert.Equal("Not Connected", vm.ListenBrainzStatus);
        Assert.Equal("Last.fm", vm.Summary);
        for (var i = 0; i < 40 && !(await NewPersistence().LoadSettingsAsync()).MobileScrobbleFromPhone; i++) await Task.Delay(50);   // the save is queued behind the tap
        Assert.True((await NewPersistence().LoadSettingsAsync()).MobileScrobbleFromPhone);
    }

    [AvaloniaFact]
    public async Task Page_ThroughComputer_ShowsTheComputersServices_AndHidesThePhonesSwitches()
    {
        // Live: Through Computer showed Send Plays and Show Now Playing switched on, which do nothing there.
        var rig = MobileFixtures.MakeRig();
        var account = new MobileAccountTests.FakeAccount { Account = MobileAccountTests.FakeAccount.SignedInAs() };
        var scrobbler = await Connected(fromPhone: false, lastFm: false);
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Scrobbler = scrobbler, Account = account, Marshal = a => a(),
        };
        var vm = new ScrobblingSettingsViewModel(shell);

        // A computer that has not said (older, or not synced yet): no guess.
        Assert.Equal("On Computer", vm.LastFmStatus);
        Assert.Equal("Computer", vm.Summary);
        Assert.Contains("up-to-date Noctis", vm.RouteFootnote);

        account.ComputerScrobbling = new ComputerScrobbling(LastFm: true, ListenBrainz: false);
        account.RaiseStateChanged();
        Assert.Equal("Connected", vm.LastFmStatus);
        Assert.True(vm.LastFmConnected);
        Assert.Equal("On your computer", vm.LastFmSubtitle);
        Assert.Equal("Not Connected", vm.ListenBrainzStatus);
        Assert.Equal("Set up on your computer", vm.ListenBrainzSubtitle);
        Assert.Equal("Last.fm", vm.Summary);
        Assert.Contains("sends the ones that count on", vm.RouteFootnote);

        shell.SetShareHistoryWithComputer(false);
        Assert.Contains("Share with Computer is off", vm.RouteFootnote);
        shell.SetShareHistoryWithComputer(true);

        var pane = new Noctis.Mobile.Views.ScrobblingPane { DataContext = vm };
        MobileFixtures.EnsurePhonePalette();
        var window = new Avalonia.Controls.Window { Width = 412, Height = 1400, Content = pane };
        window.Show();
        window.UpdateLayout();
        Assert.False(MobileFixtures.Named<Avalonia.Controls.StackPanel>(pane, "PhoneControls").IsVisible);
        Assert.True(MobileFixtures.Named<Avalonia.Controls.TextBlock>(pane, "WaitingLabel").IsEffectivelyVisible);
        vm.SelectFromPhoneCommand.Execute(null);
        window.UpdateLayout();
        Assert.True(MobileFixtures.Named<Avalonia.Controls.StackPanel>(pane, "PhoneControls").IsVisible);
        Assert.Equal("Off", vm.Summary);                                   // the phone itself is connected to nothing
        window.Close();
    }

    [AvaloniaFact]
    public async Task Page_ConnectsListenBrainzWithAPastedToken_AndShowsWaitingPlays()
    {
        var (shell, scrobbler) = NewShell(await Connected(lastFm: false));
        var vm = new ScrobblingSettingsViewModel(shell);
        vm.ListenBrainzTapCommand.Execute(null);
        var prompt = Assert.IsType<PromptViewModel>(shell.Prompt);
        Assert.True(prompt.HasField);
        prompt.FieldText = "  pasted-token ";
        await prompt.Actions[0].RunCommand.ExecuteAsync(null);
        Assert.True(scrobbler.ListenBrainzConnected);
        Assert.Equal("ben", vm.ListenBrainzSubtitle);
        Assert.Equal("Connected", vm.ListenBrainzStatus);

        scrobbler.Enqueue(Song("A"), _time.GetUtcNow().UtcDateTime.AddMinutes(-5));
        scrobbler.Enqueue(Song("B"), _time.GetUtcNow().UtcDateTime.AddMinutes(-4));
        Assert.Equal("2 plays", vm.WaitingText);
    }

    [AvaloniaFact]
    public async Task Page_LastFmSignIn_OpensTheBrowserThenFinishesOnDone()
    {
        var (shell, scrobbler) = NewShell(await Connected(lastFm: false));
        var vm = new ScrobblingSettingsViewModel(shell);
        string? opened = null;
        vm.OpenUrlRequested += u => opened = u;
        await vm.LastFmTapCommand.ExecuteAsync(null);
        Assert.Contains("token=tok123", opened);
        var prompt = Assert.IsType<PromptViewModel>(shell.Prompt);
        await prompt.Actions[0].RunCommand.ExecuteAsync(null);
        Assert.True(scrobbler.LastFmConnected);
        Assert.Equal("Connected", vm.LastFmStatus);
        await Task.Delay(100);
        Assert.Equal("ferlin · 14,208 scrobbles", vm.LastFmSubtitle.Replace(' ', ' '));
    }
}
