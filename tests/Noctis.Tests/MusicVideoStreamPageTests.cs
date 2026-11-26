using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The lyrics page with a music video streamed from YouTube (Discord 11-25): it plays in the
/// layer behind the lyrics, "Wrong video?" sits beside the Audio / Video switch, and only the
/// layer on screen decodes. The hidden layers' VideoBackdrops bound IsActive to their OWN
/// IsVisible, which stays true under a hidden parent Grid: every placement decoded the same
/// clip at once (three LibVLC players for one local clip; three network streams for a stream).
/// </summary>
public class MusicVideoStreamPageTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private static (Window Window, LyricsView View, PlayerViewModel Player) Mount()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            LyricsCenterWhenNoLyrics = false,
            MusicVideosEnabled = true,
        };
        player.CurrentTrack = new Track { Title = "Probe", Artist = "Test", Album = "Test" };
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new MetadataService(),
            new TestPersistenceService(), new FakeLibraryService())
        {
            ShowLyricsSearchDialog = _ => Task.CompletedTask,
            ExtraLyricsSources = Array.Empty<Services.Lyrics.OnlineLyricsSource>(),
        };
        var host = new ContentControl();
        host.DataTemplates.Add(new CachedViewLocator(new Dictionary<Type, Func<Control>>
        {
            [typeof(LyricsViewModel)] = () => new LyricsView(),
        }));
        var win = new Window { Width = 1400, Height = 850, Content = host };
        win.Show();
        host.Content = vm;
        Pump();
        return (win, win.GetVisualDescendants().OfType<LyricsView>().Single(), player);
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    private static VideoBackdrop Layer(LyricsView view, string name) => view.FindControl<VideoBackdrop>(name)!;

    private static string[] ActiveLayers(LyricsView view) =>
        new[] { "MediaBackdrop", "MusicVideoBackdrop", "MusicVideo", "MusicVideoFullSize" }
            .Where(n => Layer(view, n).IsActive).ToArray();

    [AvaloniaFact]
    public void Stream_PlaysBehindTheLyrics_OnlyThatLayerDecodes_AndOffersWrongVideo()
    {
        var (win, view, player) = Mount();
        try
        {
            player.CurrentMusicVideoPath = "https://rr1---sn-x.googlevideo.com/videoplayback?expire=1795679982&itag=137";
            player.IsStreamingMusicVideo = true;
            Pump();

            Assert.Equal(new[] { "MusicVideoBackdrop" }, ActiveLayers(view));
            Assert.True(view.FindControl<Button>("WrongMusicVideoButton")!.IsVisible);

            player.ShowMusicVideoFullSizeCommand.Execute(null);
            Pump();
            Assert.Equal(new[] { "MusicVideoFullSize" }, ActiveLayers(view));
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void LocalClip_InTheCoverSlot_IsTheOnlyDecoder_AndHasNoWrongVideo()
    {
        var (win, view, player) = Mount();
        try
        {
            player.CurrentMusicVideoPath = @"C:\Music\song.mp4";
            Pump();

            Assert.Equal(new[] { "MusicVideo" }, ActiveLayers(view));
            Assert.False(view.FindControl<Button>("WrongMusicVideoButton")!.IsVisible);
        }
        finally { win.Close(); }
    }
}
