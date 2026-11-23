using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics page facts line (11-23 feedback): "Rock · Pop · 2018 · 12 tracks" was one plain
/// TextBlock. Each genre is now its own link that opens the genre page, like the album
/// page's genre links. There is no year page in the app, so the year stays plain text.
/// </summary>
public class LyricsPageGenreLinkTests
{
    internal sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    internal sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    internal sealed class StubMetadata : IMetadataService
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

    internal static (LyricsViewModel Vm, PlayerViewModel Player) MakeLyrics()
    {
        var lib = new FakeLibraryService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), lib);
        return (vm, player);
    }

    [Fact]
    public void Facts_line_lists_each_genre_as_a_link_that_opens_it()
    {
        var (vm, player) = MakeLyrics();
        using var _ = vm; // unsubscribes from the app-wide accent event (see LyricsKeyboardScrollTests)
        var opened = new List<string>();
        vm.SetOpenGenreAction(opened.Add);

        player.CurrentTrack = new Track
        {
            Id = Guid.NewGuid(), Title = "Song", Artist = "A", Album = "B", Genre = "Rock; Pop", Year = 2018,
            FilePath = "x.flac",
        };

        Assert.True(vm.HasGenreTokens);
        Assert.True(vm.HasHeaderFacts);
        Assert.Equal(new[] { "Rock", "Pop" }, vm.GenreTokens.Select(t => t.Name));
        Assert.False(vm.GenreTokens[0].IsLast);
        Assert.True(vm.GenreTokens[1].IsLast);

        vm.OpenGenreCommand.Execute(vm.GenreTokens[1].Name);
        Assert.Equal(new[] { "Pop" }, opened);

        player.CurrentTrack = new Track { Id = Guid.NewGuid(), Title = "Untagged", FilePath = "y.flac" };
        Assert.False(vm.HasGenreTokens);
        Assert.False(vm.HasHeaderFacts);
        Assert.Empty(vm.GenreTokens);
    }
}
