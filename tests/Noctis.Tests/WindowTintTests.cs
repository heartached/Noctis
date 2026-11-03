using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Discord 1v1ctus: "Tint Whole Window" paints the album page colour behind the
/// islands, only on a tinted album page and only while the option is on.</summary>
public class WindowTintTests
{
    private sealed class OtherPage : ViewModelBase { }

    [Fact]
    public void Off_ByDefault()
    {
        Assert.False(new AppSettings().AlbumPageTintWholeWindow);
    }

    [Fact]
    public void NoTint_OnNonAlbumPages()
    {
        Assert.Null(MainWindowViewModel.WindowTintFor(true, null));
        Assert.Null(MainWindowViewModel.WindowTintFor(true, new OtherPage()));
    }

    // GitHub #142: the lyrics page lends its own background to the window too.

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

    private static LyricsViewModel MountLyrics()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }

    [AvaloniaFact]
    public void LyricsPage_TintsWindow_OnlyWhileOptionOn()
    {
        var lyrics = MountLyrics();
        Assert.Same(lyrics.PageTintBrush, MainWindowViewModel.WindowTintFor(true, lyrics));
        Assert.Null(MainWindowViewModel.WindowTintFor(false, lyrics));
    }

    [AvaloniaFact]
    public void LyricsPage_ColourMode_TintIsThePageBackground()
    {
        var lyrics = MountLyrics();
        var raised = new List<string?>();
        lyrics.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        lyrics.IsColorModeArtwork = false;
        Assert.Contains(nameof(LyricsViewModel.PageTintBrush), raised);
        Assert.Same(lyrics.FullBackgroundBrush, lyrics.PageTintBrush);
    }
}
