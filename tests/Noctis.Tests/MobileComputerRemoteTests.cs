using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Xunit;
using FakeAccount = Noctis.Tests.MobileAccountTests.FakeAccount;

namespace Noctis.Tests;

/// <summary>
/// Settings › Account › Remote: the phone driving the computer's own player. The page asks only
/// while it is seen and stops for good when closed; the client's wire format (key in the header,
/// form POST, capped parsing) and error kinds.
/// </summary>
public class MobileComputerRemoteTests
{
    private static readonly Guid SongId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid AlbumId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid NextId = Guid.Parse("33333333-3333-3333-3333-333333333333");

    /// <summary>The page's wait between asks, released by the test.</summary>
    private sealed class Clock
    {
        private TaskCompletionSource? _pending;
        public List<TimeSpan> Waits { get; } = new();
        public bool IsWaiting => _pending is { Task.IsCompleted: false };

        public Task Delay(TimeSpan wait, CancellationToken ct)
        {
            Waits.Add(wait);
            var tcs = new TaskCompletionSource();
            _pending = tcs;
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }

        public void Tick() => _pending!.SetResult();
    }

    private static ComputerPlayback Playing(bool playing = true, int upNext = 1) => new(
        new ComputerTrack(SongId, AlbumId, "Song", "Artist", "Album", 200_000),
        playing, 42_000, 200_000, 65, false, ComputerRepeat.Off,
        Enumerable.Range(0, upNext).Select(i => new ComputerTrack(i == 0 ? NextId : Guid.NewGuid(), null, "Next " + i, "B", "C", 1000)).ToList(),
        upNext + 3);

    private static (ShellViewModel Shell, FakeAccount Account, ComputerRemotePageViewModel Page, Clock Clock) Open(
        MobileFixtures.Rig rig, ComputerPlayback? playback = null)
    {
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), Playback = playback ?? Playing(), CoverPath = "/covers/a.jpg" };
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
        {
            Account = account,
            Marshal = a => a(),
            TintFactory = rig.Shell.TintFactory,
        };
        shell.OpenAccountCommand.Execute(null);
        var clock = new Clock();
        var page = new ComputerRemotePageViewModel(shell, account, clock.Delay);
        shell.Navigate(page);
        return (shell, account, page, clock);
    }

    [AvaloniaFact]
    public void Opening_AsksWithUpNext_ShowsTheComputersSong_ThenAsksOnceASecondWithout()
    {
        using var rig = MobileFixtures.MakeRig();
        var (_, account, page, clock) = Open(rig);

        Assert.Equal(new[] { true }, account.PlaybackAsks);
        Assert.Equal(RemoteStatus.Ready, page.Status);
        Assert.True(page.ShowsPlayer);
        Assert.Equal("Song", page.SongTitle);
        Assert.Equal("Artist", page.Artist);
        Assert.True(page.IsPlaying);
        Assert.Equal(42, page.PositionSeconds, 3);
        Assert.Equal(200, page.DurationSeconds, 3);
        Assert.Equal(0.65, page.VolumeLevel, 3);
        Assert.Equal("Up Next on Studio PC", page.UpNextTitle);
        Assert.Equal(NextId, Assert.Single(page.UpNext).Id);
        Assert.Equal(4, page.UpNextTotal);
        Assert.Equal("and 3 more", page.UpNextMoreText);
        Assert.Equal(new[] { AlbumId }, account.CoverAsks);
        Assert.Equal("/covers/a.jpg", page.ArtworkPath);
        Assert.Equal(ComputerRemotePageViewModel.PollInterval, Assert.Single(clock.Waits));

        clock.Tick();
        Assert.Equal(new[] { true, false }, account.PlaybackAsks);       // the queue only when it can have changed
        Assert.Single(account.CoverAsks);                                // same album: not fetched again
    }

    [AvaloniaFact]
    public async Task Closing_StopsEverything_AndNothingAsksAfterwards()
    {
        using var rig = MobileFixtures.MakeRig();
        var (shell, account, page, clock) = Open(rig);
        Assert.True(page.IsRunning);

        shell.GoBack();

        Assert.False(page.IsRunning);
        await page.Loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(clock.IsWaiting);                                    // the pending wait was cancelled
        var asks = account.PlaybackAsks.Count;
        shell.IsSuspended = true;
        shell.IsSuspended = false;                                        // a closed page never restarts
        Assert.Equal(asks, account.PlaybackAsks.Count);
        Assert.False(page.IsRunning);
    }

    [AvaloniaFact]
    public async Task Backgrounded_OrCoveredByNowPlaying_Stops_AndComesBackWithUpNext()
    {
        using var rig = MobileFixtures.MakeRig();
        var (shell, account, page, _) = Open(rig);

        shell.IsSuspended = true;
        Assert.False(page.IsRunning);
        await page.Loop.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        shell.IsSuspended = false;
        Assert.True(page.IsRunning);
        Assert.True(account.PlaybackAsks[^1]);                           // back on screen: Up Next re-read

        shell.IsNowPlayingOpen = true;
        Assert.False(page.IsRunning);
        shell.IsNowPlayingOpen = false;
        Assert.True(page.IsRunning);
    }

    [AvaloniaFact]
    public void AnotherPagePushedOverIt_Stops()
    {
        using var rig = MobileFixtures.MakeRig();
        var (shell, account, page, _) = Open(rig);
        shell.Navigate(new AccountPageViewModel(shell, account));
        Assert.False(page.IsRunning);
        shell.GoBack();
        Assert.True(page.IsRunning);
    }

    [AvaloniaFact]
    public void Unreachable_SaysSo_RetriesSlowly_AndRecovers()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount
        {
            Account = FakeAccount.SignedInAs(),
            PlaybackError = new NoctisServerException(NoctisErrorKind.Unreachable, "x"),
        };
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Account = account, Marshal = a => a(), TintFactory = rig.Shell.TintFactory };
        var clock = new Clock();
        var page = new ComputerRemotePageViewModel(shell, account, clock.Delay);
        shell.Navigate(page);

        Assert.Equal(RemoteStatus.Unreachable, page.Status);
        Assert.True(page.ShowsMessage);
        Assert.False(page.ShowsPlayer);
        Assert.True(page.CanRetry);
        Assert.Equal("Can't reach Studio PC", page.MessageTitle);
        Assert.False(string.IsNullOrWhiteSpace(page.MessageText));
        Assert.Equal(ComputerRemotePageViewModel.RetryInterval, clock.Waits[^1]);
        Assert.True(page.IsRunning);                                     // still asking, slowly, while seen

        account.PlaybackError = null;
        account.Playback = Playing();
        clock.Tick();
        Assert.Equal(RemoteStatus.Ready, page.Status);
        Assert.True(page.ShowsPlayer);
    }

    [AvaloniaTheory]
    [InlineData(NoctisErrorKind.RemoteNotAllowed, RemoteStatus.NotAllowed)]
    [InlineData(NoctisErrorKind.Outdated, RemoteStatus.Outdated)]
    public void NotAllowedOrTooOld_StopsAsking_UntilTryAgain(NoctisErrorKind kind, RemoteStatus status)
    {
        using var rig = MobileFixtures.MakeRig();
        var (_, account, page, clock) = Open(rig);
        account.PlaybackError = new NoctisServerException(kind, "x");
        clock.Tick();

        Assert.Equal(status, page.Status);
        Assert.False(page.IsRunning);
        Assert.Contains("Studio PC", page.MessageText);
        var asks = account.PlaybackAsks.Count;

        account.PlaybackError = null;
        page.RetryCommand.Execute(null);
        Assert.Equal(asks + 1, account.PlaybackAsks.Count);
        Assert.Equal(RemoteStatus.Ready, page.Status);
        Assert.True(page.IsRunning);
    }

    [AvaloniaFact]
    public async Task Presses_GoToTheComputer_NotThePhonesPlayer()
    {
        using var rig = MobileFixtures.MakeRig();
        var (shell, account, page, _) = Open(rig, Playing(playing: false));
        var phoneTrack = shell.Player.CurrentTrack;
        var phoneState = shell.Player.IsPlaying;

        var toggle = page.TogglePlayPauseCommand.ExecuteAsync(null);
        await toggle;
        await page.NextCommand.ExecuteAsync(null);
        await page.PreviousCommand.ExecuteAsync(null);
        await page.ToggleShuffleCommand.ExecuteAsync(null);
        await page.CycleRepeatCommand.ExecuteAsync(null);
        await page.PlayUpNextCommand.ExecuteAsync(page.UpNext[0]);
        page.IsSeeking = true;
        await page.SeekAsync(61.5);
        await page.SetVolumeAsync(0.304);

        Assert.Equal(new[] { "toggle", "next", "previous", "shuffle", "repeat", "jump", "seek", "volume" }, account.Commands.Select(c => c.Action));
        Assert.Equal(("index", "0", "tr-" + NextId.ToString("N")), (account.Commands[5].Name, account.Commands[5].Value, account.Commands[5].Id));
        Assert.Equal("61500", account.Commands[6].Value);
        Assert.Equal("30", account.Commands[7].Value);
        Assert.False(page.IsSeeking);

        // The phone's own player was never touched.
        Assert.Same(phoneTrack, shell.Player.CurrentTrack);
        Assert.Equal(phoneState, shell.Player.IsPlaying);
    }

    [AvaloniaFact]
    public async Task APressTheComputerRefuses_KeepsThePlayer_AndRereadsUpNext()
    {
        using var rig = MobileFixtures.MakeRig();
        var (_, account, page, clock) = Open(rig);
        account.CommandError = new NoctisServerException(NoctisErrorKind.Server, "70");

        await page.PlayUpNextCommand.ExecuteAsync(page.UpNext[0]);

        Assert.Equal(RemoteStatus.Ready, page.Status);
        clock.Tick();
        Assert.True(account.PlaybackAsks[^1]);
    }

    [AvaloniaFact]
    public void AnOlderAnswerLandingLate_IsIgnored_AndADraggedBarIsLeftAlone()
    {
        using var rig = MobileFixtures.MakeRig();
        var (_, _, page, _) = Open(rig);

        page.Apply(Playing() with { PositionMs = 90_000 }, withQueue: false, ticket: 100);
        page.Apply(Playing() with { PositionMs = 10_000, Playing = false }, withQueue: false, ticket: 99);
        Assert.Equal(90, page.PositionSeconds, 3);
        Assert.True(page.IsPlaying);

        page.IsSeeking = true;
        page.Apply(Playing() with { PositionMs = 120_000 }, withQueue: false, ticket: 101);
        Assert.Equal(90, page.PositionSeconds, 3);
    }

    [AvaloniaFact]
    public void NothingPlayingOnTheComputer_SaysSo()
    {
        using var rig = MobileFixtures.MakeRig();
        var (_, _, page, _) = Open(rig, new ComputerPlayback(null, false, 0, 0, 40, false, ComputerRepeat.Off, null, 0));
        Assert.True(page.ShowsNothingPlaying);
        Assert.False(page.ShowsPlayer);
        Assert.Equal("Nothing is playing on Studio PC", page.NothingPlayingText);
    }

    [AvaloniaFact]
    public void AccountPage_OffersTheRemote_ForASignedInPhone()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), Playback = Playing() };
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Account = account, Marshal = a => a(), TintFactory = rig.Shell.TintFactory };
        shell.OpenAccountCommand.Execute(null);
        var accountPage = Assert.IsType<AccountPageViewModel>(shell.CurrentPage);
        Assert.Equal("Play, pause and skip on Studio PC", accountPage.RemoteHint);

        shell.OpenComputerRemoteCommand.Execute(null);
        var remote = Assert.IsType<ComputerRemotePageViewModel>(shell.CurrentPage);
        shell.OpenComputerRemoteCommand.Execute(null);                     // a second tap opens no second page
        Assert.Same(remote, shell.CurrentPage);
        shell.GoBack();
        Assert.False(remote.IsRunning);
    }

    [AvaloniaFact]
    public void TheView_ShowsThePlayer_ThenTheUnreachableHero()
    {
        using var rig = MobileFixtures.MakeRig();
        var account = new FakeAccount { Account = FakeAccount.SignedInAs(), Playback = Playing() };
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics) { Account = account, Marshal = a => a(), TintFactory = rig.Shell.TintFactory };
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.OpenAccountCommand.Execute(null);
            window.UpdateLayout();
            var accountView = MobileFixtures.Find<Noctis.Mobile.Views.AccountPage>(view);
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Button>(accountView, "RemoteButton").IsEffectivelyVisible);

            var clock = new Clock();
            var vm = new ComputerRemotePageViewModel(shell, account, clock.Delay);
            shell.Navigate(vm);
            window.UpdateLayout();
            var page = MobileFixtures.Find<Noctis.Mobile.Views.ComputerRemotePage>(view);
            Assert.Equal("Song", MobileFixtures.Named<Avalonia.Controls.TextBlock>(page, "RemoteTitle").Text);
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Border>(page, "PlayerCard").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<Avalonia.Controls.StackPanel>(page, "RemoteMessage").IsEffectivelyVisible);

            account.PlaybackError = new NoctisServerException(NoctisErrorKind.Unreachable, "x");
            clock.Tick();
            window.UpdateLayout();
            Assert.False(MobileFixtures.Named<Avalonia.Controls.Border>(page, "PlayerCard").IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<Avalonia.Controls.Button>(page, "RetryButton").IsEffectivelyVisible);
            Assert.Equal("Can't reach Studio PC", MobileFixtures.Named<Avalonia.Controls.TextBlock>(page, "MessageTitleText").Text);
        }
        finally
        {
            window.Close();
        }
    }

    // ── The client ──

    private sealed class ScriptedHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Seen { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Seen.Add((request, body));
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string inner) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"subsonic-response\":{\"status\":\"ok\",\"version\":\"1.16.1\"" + inner + "}}", Encoding.UTF8, "application/json"),
    };

    private static HttpResponseMessage Error(int code) => new(HttpStatusCode.OK)
    {
        Content = new StringContent("{\"subsonic-response\":{\"status\":\"failed\",\"error\":{\"code\":" + code + ",\"message\":\"x\"}}}", Encoding.UTF8, "application/json"),
    };

    private const string State = ",\"noctisRemote\":{\"playing\":true,\"positionMs\":5000,\"durationMs\":-3,\"volume\":250,\"shuffle\":true,\"repeat\":\"one\"," +
        "\"track\":{\"id\":\"tr-11111111111111111111111111111111\",\"albumId\":\"al-22222222222222222222222222222222\",\"title\":\"Song\",\"artist\":\"A\",\"album\":\"B\",\"durationMs\":200000}," +
        "\"queue\":{\"total\":9,\"entry\":[{\"id\":\"tr-33333333333333333333333333333333\",\"title\":\"N\"},{\"id\":\"bogus\",\"title\":\"skipped\"},5]}}";

    [Fact]
    public async Task Client_ReadsThePlayer_WithEveryValueBounded()
    {
        var handler = new ScriptedHandler(_ => Json(State));
        using var client = new NoctisServerClient(_ => handler, "http://127.0.0.1:4533", "", "nk_key");

        var p = await client.GetComputerPlaybackAsync(withQueue: true, TestContext.Current.CancellationToken);

        Assert.Equal(SongId, p.Track!.Id);
        Assert.Equal(AlbumId, p.Track.AlbumId);
        Assert.True(p.Playing);
        Assert.Equal(0, p.DurationMs);                                   // negative clamped
        Assert.Equal(100, p.Volume);                                     // over 100 clamped
        Assert.True(p.Shuffle);
        Assert.Equal(ComputerRepeat.One, p.Repeat);
        Assert.Equal(NextId, Assert.Single(p.UpNext!).Id);               // invalid entries skipped
        Assert.Equal(9, p.UpNextTotal);

        var (request, _) = Assert.Single(handler.Seen);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Contains("/rest/getNoctisRemote.view", request.RequestUri!.AbsolutePath);
        Assert.Contains("queue=50", request.RequestUri.Query);
        Assert.DoesNotContain("nk_key", request.RequestUri.ToString());
        Assert.Equal("nk_key", Assert.Single(request.Headers.GetValues(NoctisServerClient.KeyHeader)));
    }

    [Fact]
    public async Task Client_SendsPressesAsAFormPost()
    {
        var handler = new ScriptedHandler(_ => Json(State));
        using var client = new NoctisServerClient(_ => handler, "http://127.0.0.1:4533", "", "nk_key");

        await client.ControlComputerAsync(ComputerCommand.Jump(3, NextId), withQueue: false, TestContext.Current.CancellationToken);
        await client.ControlComputerAsync(ComputerCommand.Seek(-5), withQueue: true, TestContext.Current.CancellationToken);

        var (jump, jumpForm) = handler.Seen[0];
        Assert.Equal(HttpMethod.Post, jump.Method);
        Assert.Contains("/rest/noctisRemoteControl.view", jump.RequestUri!.AbsolutePath);
        Assert.Equal("action=jump&index=3&id=tr-33333333333333333333333333333333", jumpForm);
        Assert.Equal("action=seek&positionMs=0&queue=50", handler.Seen[1].Body);
        Assert.DoesNotContain("nk_key", jumpForm);
    }

    [Theory]
    [InlineData(0, NoctisErrorKind.Outdated)]          // a computer from before the remote: "Unknown method"
    [InlineData(50, NoctisErrorKind.RemoteNotAllowed)]
    [InlineData(40, NoctisErrorKind.SignedOut)]
    [InlineData(70, NoctisErrorKind.Server)]
    public async Task Client_ErrorsMapToKinds(int code, NoctisErrorKind kind)
    {
        var handler = new ScriptedHandler(_ => Error(code));
        using var client = new NoctisServerClient(_ => handler, "http://127.0.0.1:4533", "", "nk_key");
        var a = await Assert.ThrowsAsync<NoctisServerException>(() => client.GetComputerPlaybackAsync(false, TestContext.Current.CancellationToken));
        var b = await Assert.ThrowsAsync<NoctisServerException>(() => client.ControlComputerAsync(ComputerCommand.Toggle, false, TestContext.Current.CancellationToken));
        Assert.Equal(kind, a.Kind);
        Assert.Equal(kind, b.Kind);
    }
}
