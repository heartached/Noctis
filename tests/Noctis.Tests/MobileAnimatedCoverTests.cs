using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Animated covers on the phone's Now Playing (Discord beta 11-23, "no animated cover support ...
/// I downloaded all those animations for nothing"): the setting, the loops' cache, where a song's
/// loop comes from (beside the phone's file, or the computer's, by the Wi-Fi rule), and when the
/// native video may show (only while the cover is seen and still; paused at once, decoder let go
/// later, a failing loop never again).
/// </summary>
public class MobileAnimatedCoverTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "phone-motion-" + Guid.NewGuid().ToString("N"));
    private DateTime _now = new(2026, 11, 23, 12, 0, 0, DateTimeKind.Utc);

    public MobileAnimatedCoverTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private AnimatedCoverCache Cache(long cap = AnimatedCoverCache.DefaultCapBytes) => new(Path.Combine(_dir, "cache"), cap, () => _now);

    // ── The setting ──

    [Fact]
    public void TheSavedName_ReadsBack_AndAnythingElseIsWifiOnly()
    {
        Assert.Equal(AnimatedCoversSetting.Off, AnimatedCoversSettings.Parse("Off"));
        Assert.Equal(AnimatedCoversSetting.Always, AnimatedCoversSettings.Parse(" always "));
        foreach (var bad in new[] { null, "", "2", "Sometimes" })
            Assert.Equal(AnimatedCoversSetting.WifiOnly, AnimatedCoversSettings.Parse(bad));
        Assert.Equal("WifiOnly", new AppSettings().MobileAnimatedCovers);   // the default
    }

    [Fact]
    public async Task TheSetting_IsSaved_AndReadBack()
    {
        using var rig = MobileFixtures.MakeRig(seed: async p =>
        {
            var s = await p.LoadSettingsAsync();
            s.MobileAnimatedCovers = "Off";
            await p.SaveSettingsAsync(s);
        });
        Assert.Equal(AnimatedCoversSetting.WifiOnly, rig.Shell.AnimatedCovers);   // before the read
        await rig.Shell.InitializeAsync();
        Assert.Equal(AnimatedCoversSetting.Off, rig.Shell.AnimatedCovers);

        rig.Shell.AnimatedCovers = AnimatedCoversSetting.Always;
        await rig.Shell.PendingPlayerBarSave;
        Assert.Equal("Always", (await rig.Persistence.LoadSettingsAsync()).MobileAnimatedCovers);
    }

    /// <summary>Settings › Appearance › Animated Covers: Off / Wi-Fi Only / Always on the glass
    /// menu, the one in use ticked; a pick applies at once and the row says it.</summary>
    [AvaloniaFact]
    public async Task TheSettingsRow_PicksTheSetting()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.OpenSettingsCommand.Execute(null);
        var vm = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);
        await vm.Loaded;
        Assert.Equal("Wi-Fi Only", vm.AnimatedCoversValue);
        Assert.Equal("Animated Covers, Wi-Fi Only", vm.AnimatedCoversRowName);

        vm.ShowAnimatedCoversMenuCommand.Execute(null);
        var menu = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.Equal(new[] { "Off", "Wi-Fi Only", "Always" }, menu.Items.Select(i => i.Label));
        Assert.Equal(new[] { false, true, false }, menu.Items.Select(i => i.IsChecked));
        menu.Items[2].PickCommand.Execute(null);
        Assert.Equal(AnimatedCoversSetting.Always, rig.Shell.AnimatedCovers);
        Assert.Equal("Always", vm.AnimatedCoversValue);
    }

    // ── The cache ──

    [Fact]
    public void Commit_KeepsTheLoopAndItsVersion_TrimDropsTheLeastRecentlyShown_NeverTheNewOne()
    {
        var cache = Cache(cap: 250);
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        string Save(Guid album, int bytes, string tag)
        {
            Directory.CreateDirectory(cache.Folder);
            File.WriteAllBytes(cache.PartPathFor(album), new byte[bytes]);
            var path = cache.Commit(album, cache.PartPathFor(album), tag);
            _now = _now.AddMinutes(1);
            return path;
        }

        Save(a, 100, "\"a1\"");
        Save(b, 100, "\"b1\"");
        Assert.Equal("\"a1\"", cache.ETagOf(a));
        Assert.Equal(cache.PathFor(a), cache.Get(a));          // shown again: now the most recent
        _now = _now.AddMinutes(1);
        Save(c, 100, "\"c1\"");                                 // 300 > 250: b, the oldest, goes

        Assert.Null(cache.Get(b));
        Assert.Null(cache.ETagOf(b));
        Assert.NotNull(cache.Get(a));
        Assert.NotNull(cache.Get(c));
        Assert.Equal(200, cache.SizeBytes());

        // One loop bigger than the cap still stays: it is the one about to play.
        Save(b, 400, "\"b2\"");
        Assert.NotNull(cache.Get(b));
        Assert.Null(cache.Get(a));
        Assert.Null(cache.Get(c));
    }

    // ── Where a song's loop comes from ──

    private sealed class FakeComputer
    {
        public readonly List<(Guid Album, string? ETag)> Asked = new();
        public readonly Dictionary<Guid, byte[]> Loops = new();
        public bool Unreachable;
        /// <summary>Holds each answer until it completes (a slow download).</summary>
        public Task? Gate;

        public async Task<AnimatedCoverDownload> Fetch(Guid album, string destination, string? etag, CancellationToken ct)
        {
            Asked.Add((album, etag));
            if (Gate is { } gate) await gate;
            return await Answer(album, destination, etag);
        }

        private Task<AnimatedCoverDownload> Answer(Guid album, string destination, string? etag)
        {
            if (Unreachable) throw new NoctisServerException(NoctisErrorKind.Unreachable, "down");
            if (!Loops.TryGetValue(album, out var bytes)) return Task.FromResult(new AnimatedCoverDownload(AnimatedCoverFetch.None, null));
            var tag = "\"v" + bytes.Length + "\"";
            if (etag == tag) return Task.FromResult(new AnimatedCoverDownload(AnimatedCoverFetch.NotModified, etag));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.WriteAllBytes(destination, bytes);
            return Task.FromResult(new AnimatedCoverDownload(AnimatedCoverFetch.Saved, tag));
        }
    }

    private static Track ComputerSong(Guid album) => new()
    {
        Id = Guid.NewGuid(), Title = "Desk", AlbumId = album, FilePath = "noctis://track/" + Guid.NewGuid().ToString("N"),
        SourceType = SourceType.NoctisServer,
    };

    private AnimatedCoverSource Source(FakeComputer computer, Func<bool> wifi, AnimatedCoverCache? cache = null,
        Func<Track, string?>? sidecar = null) =>
        new(cache ?? Cache(), wifi, () => _now)
        {
            FindSidecar = sidecar,
            IsComputerSong = t => t.SourceType == SourceType.NoctisServer,
            Fetch = computer.Fetch,
        };

    [Fact]
    public async Task Off_LooksNowhere()
    {
        var computer = new FakeComputer();
        var album = Guid.NewGuid();
        computer.Loops[album] = new byte[] { 1 };
        var asked = 0;
        var source = Source(computer, () => true, sidecar: _ => { asked++; return "content://loop"; });
        Assert.Null(await source.ResolveAsync(ComputerSong(album), AnimatedCoversSetting.Off, CancellationToken.None));
        Assert.Empty(computer.Asked);
        Assert.Equal(0, asked);
    }

    [Fact]
    public async Task ThePhonesOwnSong_PlaysTheCoverMp4BesideIt_OnAnyNetwork_AndNeverAsksTheComputer()
    {
        var computer = new FakeComputer();
        var own = MobileFixtures.Song("Mine");
        var source = Source(computer, () => false, sidecar: t => t == own ? "content://tree/cover.mp4" : null);
        Assert.Equal("content://tree/cover.mp4", await source.ResolveAsync(own, AnimatedCoversSetting.WifiOnly, CancellationToken.None));
        Assert.Null(await source.ResolveAsync(MobileFixtures.Song("Other"), AnimatedCoversSetting.Always, CancellationToken.None));
        Assert.Empty(computer.Asked);
        Assert.Equal(new[] { "cover.mp4", "cover.webm" }, Noctis.Services.AnimatedCoverLocator.SidecarNames);
    }

    [Fact]
    public async Task WifiOnly_DownloadsTheComputersLoopOnWifiOnly_ThenPlaysTheSavedOneOffline()
    {
        var computer = new FakeComputer();
        var album = Guid.NewGuid();
        computer.Loops[album] = new byte[] { 1, 2, 3 };
        var wifi = false;
        var source = Source(computer, () => wifi);
        var song = ComputerSong(album);

        Assert.Null(await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None));
        Assert.Empty(computer.Asked);                                   // mobile data: nothing downloaded

        wifi = true;
        var path = await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(path!));
        Assert.Single(computer.Asked);

        wifi = false;
        computer.Unreachable = true;                                     // away from home, offline
        Assert.Equal(path, await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None));
        Assert.Single(computer.Asked);
    }

    [Fact]
    public async Task Always_DownloadsOnMobileData_ANoneIsRemembered_AnUnreachableComputerIsTheStillCover()
    {
        var computer = new FakeComputer();
        var source = Source(computer, () => false);
        var bare = ComputerSong(Guid.NewGuid());
        Assert.Null(await source.ResolveAsync(bare, AnimatedCoversSetting.Always, CancellationToken.None));
        Assert.Null(await source.ResolveAsync(bare, AnimatedCoversSetting.Always, CancellationToken.None));
        Assert.Single(computer.Asked);                                   // "none" is not asked again soon
        _now += AnimatedCoverSource.NoneMemo + TimeSpan.FromMinutes(1);
        Assert.Null(await source.ResolveAsync(bare, AnimatedCoversSetting.Always, CancellationToken.None));
        Assert.Equal(2, computer.Asked.Count);

        computer.Unreachable = true;
        Assert.Null(await source.ResolveAsync(ComputerSong(Guid.NewGuid()), AnimatedCoversSetting.Always, CancellationToken.None));

        var album = Guid.NewGuid();
        computer.Unreachable = false;
        computer.Loops[album] = new byte[] { 9 };
        Assert.NotNull(await source.ResolveAsync(ComputerSong(album), AnimatedCoversSetting.Always, CancellationToken.None));
    }

    [Fact]
    public async Task ASavedLoop_IsAskedAboutAgainAfterTwelveHours_WithItsVersion_AndANewOneReplacesIt()
    {
        var computer = new FakeComputer();
        var album = Guid.NewGuid();
        computer.Loops[album] = new byte[] { 1 };
        var cache = Cache();
        var source = Source(computer, () => true, cache);
        var song = ComputerSong(album);
        var path = await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None);

        _now += TimeSpan.FromHours(1);
        await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None);
        await source.PendingRecheck;
        Assert.Single(computer.Asked);                                   // checked an hour ago: not again

        _now += AnimatedCoverSource.RecheckAfter;
        Assert.Equal(path, await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None));
        await source.PendingRecheck;
        Assert.Equal((album, "\"v1\""), computer.Asked[^1]);            // unchanged: 304, nothing sent
        Assert.Equal(_now, cache.CheckedAt(album));

        computer.Loops[album] = new byte[] { 4, 4 };                     // a new loop saved on the computer
        _now += AnimatedCoverSource.RecheckAfter + TimeSpan.FromMinutes(1);
        await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None);
        await source.PendingRecheck;
        Assert.Equal(new byte[] { 4, 4 }, File.ReadAllBytes(cache.PathFor(album)));

        computer.Loops.Remove(album);                                    // and then removed there
        _now += AnimatedCoverSource.RecheckAfter + TimeSpan.FromMinutes(1);
        await source.ResolveAsync(song, AnimatedCoversSetting.WifiOnly, CancellationToken.None);
        await source.PendingRecheck;
        Assert.Null(cache.Get(album));
    }

    // ── Now Playing's loop ──

    [Fact]
    public async Task TheLoopIsLookedUp_OnlyOnceNowPlayingShowsTheCover_AndANewSongStartsStill()
    {
        var computer = new FakeComputer();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        computer.Loops[a] = new byte[] { 1 };
        var one = ComputerSong(a);
        var two = ComputerSong(b);
        using var rig = MobileFixtures.MakeRig(new[] { one, two });
        var motion = new NowPlayingMotion(rig.Shell, Source(computer, () => true));

        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        await motion.Pending;
        Assert.Empty(computer.Asked);                                    // nobody opened Now Playing
        Assert.Null(motion.Path);

        rig.Shell.IsNowPlayingOpen = true;
        await motion.Pending;
        Assert.NotNull(motion.Path);

        rig.Shell.Player.NextCommand.Execute(null);
        Assert.Null(motion.Path);                                        // the next song's still cover first
        await motion.Pending;
        Assert.Null(motion.Path);
        Assert.Equal(new[] { a, b }, computer.Asked.Select(x => x.Album));

        rig.Shell.AnimatedCovers = AnimatedCoversSetting.Off;
        rig.Shell.Player.PreviousCommand.Execute(null);
        await motion.Pending;
        Assert.Null(motion.Path);
    }

    /// <summary>A download that breaks off deletes its partial file before the next download may
    /// start: deleted after, it could be the next one's file (the same album asked again), which
    /// then ended with the still cover.</summary>
    [Fact]
    public async Task ABrokenDownload_DeletesItsPartialFile_BeforeTheNextOneStarts()
    {
        var album = Guid.NewGuid();
        var gate = new TaskCompletionSource();
        var calls = 0;
        bool? leftOver = null;
        var source = new AnimatedCoverSource(Cache(), () => true, () => _now)
        {
            IsComputerSong = _ => true,
            Fetch = async (_, destination, _, _) =>
            {
                if (++calls == 1)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination, new byte[] { 1 });
                    await gate.Task;
                    throw new NoctisServerException(NoctisErrorKind.Unreachable, "dropped");
                }
                leftOver = File.Exists(destination);
                File.WriteAllBytes(destination, new byte[] { 1, 2 });
                return new AnimatedCoverDownload(AnimatedCoverFetch.Saved, "\"v2\"");
            },
        };
        var song = ComputerSong(album);
        var first = source.ResolveAsync(song, AnimatedCoversSetting.Always, CancellationToken.None);
        var second = source.ResolveAsync(song, AnimatedCoversSetting.Always, CancellationToken.None);   // waits its turn
        Assert.Equal(1, calls);
        gate.SetResult();
        Assert.Null(await first);
        Assert.NotNull(await second);
        Assert.False(leftOver);
    }

    [Fact]
    public async Task ALookupThatEndsAfterTheSongChanged_IsAskedAgain_WhenTheSongComesBack()
    {
        // Now Playing open on a song whose loop is still downloading, closed, the next song
        // plays, the download ends, then Previous and Now Playing again: the loop shows (it
        // stayed the still cover: the ended lookup still counted as running).
        var computer = new FakeComputer();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        computer.Loops[a] = new byte[] { 1 };
        var one = ComputerSong(a);
        var two = ComputerSong(b);
        using var rig = MobileFixtures.MakeRig(new[] { one, two });
        var motion = new NowPlayingMotion(rig.Shell, Source(computer, () => true));
        var gate = new TaskCompletionSource();
        computer.Gate = gate.Task;

        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.IsNowPlayingOpen = true;
        var slow = motion.Pending;
        rig.Shell.IsNowPlayingOpen = false;
        rig.Shell.Player.NextCommand.Execute(null);
        gate.SetResult();
        await slow;
        Assert.Null(motion.Path);

        computer.Gate = null;
        rig.Shell.Player.PreviousCommand.Execute(null);
        Assert.Same(one, rig.Shell.Player.CurrentTrack);
        rig.Shell.IsNowPlayingOpen = true;
        await motion.Pending;
        Assert.NotNull(motion.Path);
    }

    [AvaloniaFact]
    public void TheCover_IsSeen_OnlyOnTopUncoveredPlayingAndStill()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        var shell = rig.Shell;
        shell.Player.PlayTracks(rig.Library.TrackList, 0);
        shell.IsNowPlayingOpen = true;
        Assert.True(NowPlayingPage.IsMotionSeen(shell, pageVisible: true, held: false));

        Assert.False(NowPlayingPage.IsMotionSeen(shell, pageVisible: false, held: false));
        Assert.False(NowPlayingPage.IsMotionSeen(shell, pageVisible: true, held: true));   // a finger on it
        shell.IsQueueOpen = true;
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.IsQueueOpen = false;
        shell.IsLyricsOpen = true;
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.IsLyricsOpen = false;
        shell.IsSuspended = true;                                        // home, task switch, screen off
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.IsSuspended = false;
        shell.NowPlayingArtwork = NowPlayingArtworkStyle.Vinyl;
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.NowPlayingArtwork = NowPlayingArtworkStyle.Cover;
        shell.AnimatedCovers = AnimatedCoversSetting.Off;
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.AnimatedCovers = AnimatedCoversSetting.Always;
        shell.Player.TogglePlayPauseCommand.Execute(null);                     // paused: the cover shrinks back
        Assert.False(shell.Player.IsPlaying);
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.True(NowPlayingPage.IsMotionSeen(shell, true, false));
        shell.IsNowPlayingOpen = false;
        Assert.False(NowPlayingPage.IsMotionSeen(shell, true, false));
    }

    private sealed class FakeSaver : IBatterySaver
    {
        public bool IsOn { get; set; }
        public event EventHandler? Changed;
        public int Listeners => Changed?.GetInvocationList().Length ?? 0;
        public void Flip(bool on)
        {
            IsOn = on;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Battery Saver turned on or off while Now Playing is open stops or starts the loop at
    /// once (it waited for a pause, a new song or a menu), and nothing listens once the page is shut.</summary>
    [AvaloniaFact]
    public void BatterySaver_TurnedOnOrOff_WhileNowPlayingIsOpen_AppliesAtOnce()
    {
        var saver = new FakeSaver();
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        typeof(ShellViewModel).GetProperty(nameof(ShellViewModel.BatterySaver))!.SetValue(rig.Shell, saver);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        MobileFixtures.Land(window);
        var cover = view.FindControl<NowPlayingPage>("NowPlaying")!.FindControl<AnimatedCoverView>("MotionCover")!;
        Assert.True(cover.IsSeen);

        saver.Flip(true);
        Assert.False(cover.IsSeen);
        saver.Flip(false);
        Assert.True(cover.IsSeen);

        rig.Shell.IsNowPlayingOpen = false;
        MobileFixtures.Land(window);
        Assert.False(cover.IsSeen);
        Assert.Equal(0, saver.Listeners);
        window.Close();
    }

    // ── When the video shows ──

    private sealed class FakeVideo : IAnimatedCoverPlayer
    {
        public readonly List<string> Calls = new();
        /// <summary>Play raises Failed before it returns (the phone's player failing to start).</summary>
        public bool FailOnPlay;
        public void Play(string source)
        {
            Calls.Add("play " + source);
            if (FailOnPlay) Fail();
        }
        public void Pause() => Calls.Add("pause");
        public void Release() => Calls.Add("release");
        public event EventHandler? Failed;
        public void Fail() => Failed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>A manual clock for the presenter's two delays.</summary>
    private sealed class Clock
    {
        private readonly List<(TimeSpan At, Action Run, Ticket Ticket)> _due = new();
        private TimeSpan _now;

        private sealed class Ticket : IDisposable
        {
            public bool Cancelled;
            public void Dispose() => Cancelled = true;
        }

        public IDisposable Schedule(TimeSpan delay, Action run)
        {
            var t = new Ticket();
            _due.Add((_now + delay, run, t));
            return t;
        }

        public void Advance(TimeSpan by)
        {
            _now += by;
            foreach (var d in _due.Where(d => d.At <= _now).ToList())
            {
                _due.Remove(d);
                if (!d.Ticket.Cancelled) d.Run();
            }
        }
    }

    [Fact]
    public void TheVideo_ShowsOnceTheCoverHasBeenStill_GoesAtOnce_AndLetsTheDecoderGoLater()
    {
        var video = new FakeVideo();
        var clock = new Clock();
        var p = new AnimatedCoverPresenter(video, clock.Schedule);

        p.Update("loop-a", seen: true);
        Assert.False(p.IsShown);                                          // the sheet may still be rising
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.True(p.IsShown);
        Assert.Equal(new[] { "play loop-a" }, video.Calls);

        p.Update("loop-a", seen: false);                                  // a menu opened over it
        Assert.False(p.IsShown);
        Assert.Equal("pause", video.Calls[^1]);
        p.Update("loop-a", seen: true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.True(p.IsShown);
        Assert.Equal("play loop-a", video.Calls[^1]);                     // resumed, same decoder
        Assert.DoesNotContain("release", video.Calls);

        p.Update("loop-a", seen: false);                                  // Now Playing closed
        clock.Advance(AnimatedCoverPresenter.ReleaseDelay);
        Assert.Equal("release", video.Calls[^1]);
    }

    [Fact]
    public void ANewSongsLoop_ReplacesTheOld_AndASeenBlipShorterThanTheSettleNeverShows()
    {
        var video = new FakeVideo();
        var clock = new Clock();
        var p = new AnimatedCoverPresenter(video, clock.Schedule);
        p.Update("loop-a", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);

        p.Update(null, true);                                             // the next song: still cover first
        Assert.False(p.IsShown);
        Assert.Equal(new[] { "play loop-a", "pause", "release" }, video.Calls);
        p.Update("loop-b", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.Equal("play loop-b", video.Calls[^1]);

        video.Calls.Clear();
        p.Update("loop-b", false);
        p.Update("loop-b", true);                                          // a tap on the cover
        p.Update("loop-b", false);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.False(p.IsShown);
        Assert.Equal(new[] { "pause" }, video.Calls);
    }

    [Fact]
    public void ALoopThatFails_FallsBackToTheStillCover_AndIsNotTriedAgain()
    {
        var video = new FakeVideo();
        var clock = new Clock();
        var p = new AnimatedCoverPresenter(video, clock.Schedule);
        p.Update("bad", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        video.Fail();
        Assert.False(p.IsShown);
        Assert.Equal("release", video.Calls[^1]);

        p.Update("bad", false);
        p.Update("bad", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.False(p.IsShown);
        Assert.Equal(1, video.Calls.Count(c => c.StartsWith("play", StringComparison.Ordinal)));

        p.Update("good", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.True(p.IsShown);
    }

    [Fact]
    public void ALoopThatFailsToStart_IsNotShown_AndIsNotTriedAgain()
    {
        var video = new FakeVideo { FailOnPlay = true };
        var clock = new Clock();
        var p = new AnimatedCoverPresenter(video, clock.Schedule);
        p.Update("bad", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.False(p.IsShown);
        Assert.Equal(new[] { "play bad", "release" }, video.Calls);

        p.Update("bad", false);
        p.Update("bad", true);
        clock.Advance(AnimatedCoverPresenter.SettleDelay);
        Assert.False(p.IsShown);
        Assert.Equal(1, video.Calls.Count(c => c.StartsWith("play", StringComparison.Ordinal)));
    }
}
