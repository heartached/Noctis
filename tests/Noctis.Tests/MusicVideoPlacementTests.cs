using Avalonia.Headless.XUnit;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Where a song's music video shows (Discord, Cosmo 11-02: "music videos in the back that pause
/// with the music, or being able to switch from audio and video"): one place at a time, so only
/// one decoder runs — the cover slot (default), behind the lyrics (setting), or full size (the
/// lyrics page's Audio / Video switch). The user's lyrics background video gives way to a music
/// video that fills the page.
/// </summary>
public class MusicVideoPlacementTests
{
    private static PlayerViewModel Player() =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            MusicVideosEnabled = true,
        };

    [AvaloniaFact]
    public void NoClip_ShowsNoVideoAnywhere_EvenWhileWatching()
    {
        var vm = Player();
        vm.IsWatchingMusicVideo = true;
        vm.MusicVideoAsBackground = true;

        Assert.False(vm.ShowsMusicVideoInCover);
        Assert.False(vm.ShowsMusicVideoBackdrop);
        Assert.False(vm.ShowsMusicVideoFullSize);
    }

    [AvaloniaFact]
    public void Clip_ShowsInTheCoverSlot_ByDefault()
    {
        Assert.False(new AppSettings().MusicVideoAsBackground); // off by default
        var vm = Player();
        vm.CurrentMusicVideoPath = @"C:\Music\song.mp4";

        Assert.True(vm.ShowsMusicVideoInCover);
        Assert.False(vm.ShowsMusicVideoBackdrop);
        Assert.False(vm.ShowsMusicVideoFullSize);
    }

    [AvaloniaFact]
    public void BehindLyrics_MovesTheClipOutOfTheCoverSlot_AndHidesTheBackgroundVideo()
    {
        var vm = Player();
        vm.SetLyricsBackgroundSources(Path.GetTempFileName(), new Dictionary<string, string>());
        vm.CurrentMusicVideoPath = @"C:\Music\song.mp4";
        Assert.True(vm.ShowsLyricsBackgroundMedia); // cover-slot clip leaves the backdrop alone

        vm.MusicVideoAsBackground = true;

        Assert.False(vm.ShowsMusicVideoInCover);
        Assert.True(vm.ShowsMusicVideoBackdrop);
        Assert.False(vm.ShowsLyricsBackgroundMedia);
    }

    [AvaloniaFact]
    public void Watching_ShowsOnlyTheFullSizeClip_FromEitherPlacement()
    {
        var vm = Player();
        vm.SetLyricsBackgroundSources(Path.GetTempFileName(), new Dictionary<string, string>());
        vm.CurrentMusicVideoPath = @"C:\Music\song.mp4";

        foreach (var behind in new[] { false, true })
        {
            vm.MusicVideoAsBackground = behind;
            vm.ShowMusicVideoFullSizeCommand.Execute(null);

            Assert.True(vm.ShowsMusicVideoFullSize);
            Assert.False(vm.ShowsMusicVideoInCover);
            Assert.False(vm.ShowsMusicVideoBackdrop);
            Assert.False(vm.ShowsLyricsBackgroundMedia);

            vm.ShowMusicVideoAudioCommand.Execute(null);
            Assert.False(vm.ShowsMusicVideoFullSize);
            Assert.Equal(!behind, vm.ShowsMusicVideoInCover);
            Assert.Equal(behind, vm.ShowsMusicVideoBackdrop);
        }
    }

    [AvaloniaFact]
    public void Watching_SurvivesASongWithoutAClip()
    {
        var vm = Player();
        vm.CurrentMusicVideoPath = @"C:\Music\a.mp4";
        vm.IsWatchingMusicVideo = true;

        vm.CurrentMusicVideoPath = null;   // next song has no clip: normal page
        Assert.False(vm.ShowsMusicVideoFullSize);

        vm.CurrentMusicVideoPath = @"C:\Music\b.mp4"; // the one after has one: back to the video
        Assert.True(vm.ShowsMusicVideoFullSize);
    }

    [AvaloniaFact]
    public void PlacementFlags_RaiseChange_WhenTheClipOrTheChoiceMoves()
    {
        var vm = Player();
        var raised = new List<string?>();
        vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        vm.CurrentMusicVideoPath = @"C:\Music\song.mp4";
        vm.MusicVideoAsBackground = true;
        vm.IsWatchingMusicVideo = true;

        foreach (var name in new[]
                 {
                     nameof(PlayerViewModel.ShowsMusicVideoInCover), nameof(PlayerViewModel.ShowsMusicVideoBackdrop),
                     nameof(PlayerViewModel.ShowsMusicVideoFullSize), nameof(PlayerViewModel.ShowsLyricsBackgroundMedia),
                 })
            Assert.Equal(3, raised.Count(n => n == name));
    }

    [AvaloniaFact]
    public void IslandWatchItem_OpensTheLyricsPage_WhenElsewhere_AndGoesBackToAudio_OnIt()
    {
        var vm = Player();
        var navigated = new List<string>();
        vm.SetNavigateAction(navigated.Add);
        vm.CurrentMusicVideoPath = @"C:\Music\song.mp4";

        vm.ToggleWatchMusicVideoCommand.Execute(null);
        Assert.True(vm.IsWatchingMusicVideo);
        Assert.Equal(new[] { "lyrics" }, navigated);

        vm.SetLyricsPageActions(() => { }, () => { }, () => { }, () => { }, () => { }, false, false, false, false);
        vm.ToggleWatchMusicVideoCommand.Execute(null);
        Assert.False(vm.IsWatchingMusicVideo);
        Assert.Single(navigated);
    }

    [Theory]
    [InlineData(3840, 2160, 1920, 1080)]
    [InlineData(1280, 720, 1280, 720)]
    [InlineData(0, 0, 1920, 1080)]
    public void FitBuffer_HonoursAPerInstanceCap(int w, int h, int expectedW, int expectedH)
        => Assert.Equal((expectedW, expectedH), VideoBackdrop.FitBuffer(w, h, 1920));

    [Fact]
    public void FitBuffer_ABadCap_FallsBackToTheDefault()
        => Assert.Equal(VideoBackdrop.FitBuffer(3840, 2160), VideoBackdrop.FitBuffer(3840, 2160, 0));
}
