using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #78 in the mini player's lyrics: the translation / romanization rows render
/// under a TTML line and answer the Settings toggles live. The mini player reaches the
/// lyrics VM through its own window DataContext, so its bindings differ from the page's.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerLyricLayerTests
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

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
        });
    }

    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    private const string Ttml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal">
          <head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal">
            <translations><translation xml:lang="en"><text for="L1">The heavy rain</text></translation></translations>
            <transliterations><transliteration xml:lang="ja-Latn">
              <text for="L1"><span begin="1.0" end="1.4">fu</span><span begin="1.4" end="2.0">ri</span></text>
            </transliteration></transliterations>
          </iTunesMetadata></metadata></head>
          <body><div>
            <p begin="1.0" end="3.0" itunes:key="L1"><span begin="1.0" end="1.4">ふ</span><span begin="1.4" end="2.0">り</span></p>
          </div></body>
        </tt>
        """;

    [AvaloniaFact]
    public async Task LayerRows_RenderUnderTheLine_AndFollowTheToggles()
    {
        EnsureAppResources();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NoOpPlayHistoryService());
        var vm = new MiniPlayerViewModel(player, lyrics, settings, library);

        var line = Assert.Single(TtmlParser.Parse(Ttml).Lines!);
        lyrics.LyricLines.ReplaceAll(new[] { line });

        var win = new MiniPlayerWindow { DataContext = vm, Width = 340, Height = 432 };
        win.Show();
        await PumpFor(150);
        try
        {
            vm.ToggleLyricsFormCommand.Execute(null);
            await PumpFor(700);

            var button = win.GetVisualDescendants().OfType<Button>()
                .Single(b => ReferenceEquals(b.DataContext, line));
            var romaji = button.GetVisualDescendants().OfType<ItemsControl>().Single(c => c.Classes.Contains("romanization"));
            var translation = button.GetVisualDescendants().OfType<TextBlock>().Single(c => c.Classes.Contains("translation-text"));
            Assert.True(romaji.IsVisible);
            Assert.True(translation.IsVisible);
            Assert.True(translation.TranslatePoint(default, button)!.Value.Y
                        >= romaji.TranslatePoint(default, button)!.Value.Y + romaji.Bounds.Height - 0.5);

            player.LyricsShowTranslations = false;
            player.LyricsShowRomanization = false;
            await PumpFor(50);
            Assert.False(romaji.IsVisible);
            Assert.False(translation.IsVisible);
        }
        finally { win.Close(); }
    }
}
