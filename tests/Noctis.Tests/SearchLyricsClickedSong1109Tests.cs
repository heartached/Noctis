using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #208: a song menu's Search Lyrics on a song that isn't playing loaded that song's
/// lyrics into the lyrics page, which follows the playing song. It now opens the Search Lyrics
/// dialog for the clicked song and leaves the page alone.
/// </summary>
public class SearchLyricsClickedSong1109Tests
{
    private sealed class NoLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);

        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class NoNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    [AvaloniaFact]
    public async Task OtherSong_OpensTheSearchDialogForThatSong()
    {
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var playing = new Track { Title = "Playing", Artist = "A" };
        var clicked = new Track { Title = "Clicked", Artist = "B" };
        player.CurrentTrack = playing;
        LyricsSearchViewModel? shown = null;
        var vm = new LyricsViewModel(player, new NoLrcLib(), new NoNetEase(), new MetadataService(), new TestPersistenceService(), new FakeLibraryService())
        {
            ShowLyricsSearchDialog = s => { shown = s; return Task.CompletedTask; },
        };

        Assert.True(vm.IsPlayerTrack(playing));
        Assert.False(vm.IsPlayerTrack(clicked));

        await vm.OpenLyricsSearchForTrackAsync(clicked);

        Assert.NotNull(shown);
        Assert.Equal("Clicked", shown!.Title);
        Assert.Equal("B", shown.Artist);
    }
}
