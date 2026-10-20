using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone, 2026-10-19 (S23): with the computer off, a queue of songs that are not downloaded
/// "played" at 00:00 in silence and ran through all twenty songs every few minutes for over
/// half an hour (213 source errors in the log, ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT). The
/// five-error breaker never tripped because the player's 250 ms position poll — which reports
/// Playing from Play() on, at 00:00 while it is still connecting — reset the streak between
/// every two errors, and the error line was cleared by the very skip it caused.
/// </summary>
public class MobileUnreachableComputerPlaybackTests : IDisposable
{
    private const string Timeout = "ERROR_CODE_IO_NETWORK_CONNECTION_TIMEOUT: Source error";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    /// <summary>"s" = a desktop song that is not downloaded (needs the computer), "l" = plays on
    /// the phone (a download or the phone's own file).</summary>
    private static Track[] Tracks(string kinds) => kinds
        .Select((k, i) => new Track
        {
            Id = Guid.NewGuid(),
            Title = $"{k}{i}",
            FilePath = $"content://x/{k}{i}",
            Duration = TimeSpan.FromSeconds(100 + i),
            SourceType = k == 's' ? SourceType.NoctisServer : SourceType.Local,
        })
        .ToArray();

    private (NowPlayingViewModel Vm, FakeAudioPlayer Player) Make(Track[] tracks)
    {
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(tracks);
        var vm = new NowPlayingViewModel(player, library, new PersistenceService(_root), marshal: a => a())
        {
            NeedsComputer = t => t.Title.StartsWith('s'),
        };
        return (vm, player);
    }

    /// <summary>The computer came back: a song that needs it really plays (Previous restarted it).
    /// "Unreachable" was cleared only by a new queue or Play from stopped, so at its end every
    /// later desktop song was still passed over and playback stopped with "Can't reach".</summary>
    [Fact]
    public void ADesktopSongThatPlays_ProvesTheComputerIsBack()
    {
        var t = Tracks("ss");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        player.RaisePlaybackError(Timeout);                       // computer off: stops on s0
        Assert.Same(t[0], vm.CurrentTrack);

        vm.PreviousCommand.Execute(null);                         // computer on again: s0 streams
        player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        player.RaiseTrackEnded();

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(t[1].FilePath, player.PlayedPaths[^1]);
    }

    [Fact]
    public void ErrorStreak_IsNotClearedByTicksThatNeverLeaveTheStart()
    {
        var t = Tracks("llllllllll");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);

        for (var i = 0; i < 5; i++)
        {
            // What the Android player does while a song is still connecting: Playing, 00:00.
            player.RaisePositionChanged(TimeSpan.Zero);
            player.RaisePositionChanged(TimeSpan.Zero);
            player.RaisePlaybackError("ERROR_CODE_IO_FILE_NOT_FOUND: Source error");
        }

        Assert.False(vm.IsPlaying);
        Assert.Equal(5, player.PlayedPaths.Count);   // t0 + four skips, then the breaker
        Assert.NotEqual(string.Empty, vm.ErrorText);
    }

    [Fact]
    public void ErrorStreak_ClearsOnceAudioActuallyMoves()
    {
        var t = Tracks("llllllllll");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);

        for (var i = 0; i < 4; i++) player.RaisePlaybackError("ERROR_CODE_IO_FILE_NOT_FOUND: Source error");
        player.RaisePositionChanged(TimeSpan.FromSeconds(1));   // t4 really plays
        for (var i = 0; i < 4; i++) player.RaisePlaybackError("ERROR_CODE_IO_FILE_NOT_FOUND: Source error");

        Assert.True(vm.IsPlaying);
        Assert.Equal(9, player.PlayedPaths.Count);
    }

    [Fact]
    public void ErrorSkip_KeepsTheReasonOnScreen()
    {
        var t = Tracks("lll");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);

        player.RaisePlaybackError("ERROR_CODE_IO_FILE_NOT_FOUND: Source error");

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.NotEqual(string.Empty, vm.ErrorText);   // was cleared by the skip's own StartTrack
    }

    [Fact]
    public void UnreachableComputer_SkipsStraightToTheNextSongOnThePhone_AndSaysWhy()
    {
        var t = Tracks("sssls");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);

        player.RaisePlaybackError(Timeout);

        Assert.Same(t[3], vm.CurrentTrack);
        Assert.True(vm.IsPlaying);
        Assert.Equal(new[] { t[0].FilePath, t[3].FilePath }, player.PlayedPaths);   // s1, s2 never tried
        Assert.Contains("computer", vm.ErrorText);
        Assert.Contains("computer", vm.Notice);
        Assert.Equal(new[] { t[4] }, vm.UpNext);
    }

    [Fact]
    public void UnreachableComputer_NothingOnThePhone_StopsOnTheSong_WithTheQueueIntact()
    {
        var t = Tracks("ssss");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);

        player.RaisePlaybackError(Timeout);

        Assert.False(vm.IsPlaying);
        Assert.Equal(PlaybackState.Stopped, player.State);
        Assert.Equal(new[] { t[0].FilePath }, player.PlayedPaths);
        Assert.Same(t[0], vm.CurrentTrack);
        Assert.Equal(new[] { t[1], t[2], t[3] }, vm.UpNext);
        Assert.Equal("Track 1 of 4", vm.QueuePositionText);
        Assert.Contains("computer", vm.ErrorText);
        Assert.Contains("computer", vm.Notice);
    }

    [Fact]
    public void UnreachableComputer_RepeatAll_DoesNotGoRoundForever()
    {
        var t = Tracks("sss");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        vm.CycleRepeatCommand.Execute(null);   // Off -> All

        player.RaisePlaybackError(Timeout);

        Assert.False(vm.IsPlaying);
        Assert.Single(player.PlayedPaths);
        Assert.Same(t[0], vm.CurrentTrack);
    }

    [Fact]
    public void UnreachableComputer_RepeatAll_WrapsToASongOnThePhoneEarlierInTheQueue()
    {
        var t = Tracks("lss");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 1);   // s1, s2 queued; l0 only through the Repeat All cycle
        vm.CycleRepeatCommand.Execute(null);

        player.RaisePlaybackError(Timeout);

        Assert.Equal(new[] { t[1].FilePath, t[0].FilePath }, player.PlayedPaths);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void WhileTheComputerIsUnreachable_ANaturalEndSkipsSongsThatNeedIt_AndGaplessPreparesTheRightOne()
    {
        var t = Tracks("slsl");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        player.RaisePlaybackError(Timeout);                  // -> l1
        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(t[3].FilePath, player.PreparedPaths.Last());   // not s2

        player.RaisePositionChanged(TimeSpan.FromSeconds(1));
        player.RaiseTrackEnded();

        Assert.Same(t[3], vm.CurrentTrack);
        Assert.DoesNotContain(t[2].FilePath, player.PlayedPaths);
    }

    [Fact]
    public void WhileTheComputerIsUnreachable_NextWithNothingOnThePhone_KeepsTheCurrentSongPlaying()
    {
        var t = Tracks("sls");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        player.RaisePlaybackError(Timeout);                  // -> l1
        vm.DismissNotice();

        vm.NextCommand.Execute(null);

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.True(vm.IsPlaying);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.DoesNotContain(t[2].FilePath, player.PlayedPaths);
        Assert.Contains("computer", vm.Notice);
    }

    [Fact]
    public void ChoosingASongThatNeedsTheComputer_TriesItAgain()
    {
        var t = Tracks("ssl");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        player.RaisePlaybackError(Timeout);                  // -> l2

        vm.PlayTracks(t, 1);                                 // the user taps s1: the computer may be back

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(t[1].FilePath, player.PlayedPaths.Last());
        Assert.Equal(string.Empty, vm.ErrorText);
    }

    [Fact]
    public void PlayAfterTheStop_RetriesTheSong()
    {
        var t = Tracks("ss");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);
        player.RaisePlaybackError(Timeout);                  // stops on s0

        vm.TogglePlayPauseCommand.Execute(null);

        Assert.Equal(new[] { t[0].FilePath, t[0].FilePath }, player.PlayedPaths);
        Assert.True(vm.IsPlaying);
    }

    [Fact]
    public void ASongThatNeedsTheComputer_FailingForAnotherReason_IsAnOrdinarySkip()
    {
        var t = Tracks("sss");
        var (vm, player) = Make(t);
        vm.PlayTracks(t, 0);

        player.RaisePlaybackError("ERROR_CODE_DECODING_FAILED: boom");

        Assert.Same(t[1], vm.CurrentTrack);
        Assert.Equal(string.Empty, vm.Notice);
    }

    [AvaloniaFact]
    public void TheShellBanner_SaysWhy_WhereverTheUserIs()
    {
        var t = Tracks("ss");
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(t);
        var persistence = new PersistenceService(_root);
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a())
        {
            NeedsComputer = x => x.Title.StartsWith('s'),
        };
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())));
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();
        var banner = view.FindControl<Control>("NoticeBanner")!;
        Assert.False(banner.IsVisible);

        nowPlaying.PlayTracks(t, 0);
        player.RaisePlaybackError(Timeout);
        window.UpdateLayout();

        Assert.True(banner.IsVisible);
        Assert.Equal(NowPlayingViewModel.ComputerUnreachableStopped, view.FindControl<TextBlock>("NoticeText")!.Text);
        Assert.True(banner.Bounds.Height > 0);

        nowPlaying.DismissNotice();
        window.UpdateLayout();
        Assert.False(banner.IsVisible);
        window.Close();
    }

    private sealed class NoPicker : Noctis.Mobile.Services.IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    [Fact]
    public void WithoutAnAccount_AConnectionErrorIsAnOrdinarySkip()
    {
        var t = Tracks("sss");
        var player = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        library.TrackList.AddRange(t);
        var vm = new NowPlayingViewModel(player, library, new PersistenceService(_root), marshal: a => a());
        vm.PlayTracks(t, 0);

        player.RaisePlaybackError(Timeout);

        Assert.Same(t[1], vm.CurrentTrack);
    }
}
