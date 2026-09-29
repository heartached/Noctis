using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Lyrics Background Video › Modify: the search box is debounced, so typing
/// does not scan the whole library per keystroke, and the scan still finds songs and
/// albums through the accent/punctuation-insensitive match.
/// </summary>
public class LyricsBackgroundPickerViewModelTests
{
    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [AvaloniaFact]
    public async Task Typing_SearchesOnceAfterThePause_WithTheNormalizedMatch()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var lib = new FakeLibraryService();
            var albumId = Guid.NewGuid();
            var song = new Track { Title = "Don’t You (Taylor’s Version)", Artist = "Taylor Swift", Album = "Lover", AlbumId = albumId };
            var other = new Track { Title = "Cruel Summer", Artist = "Taylor Swift", Album = "Lover", AlbumId = albumId };
            lib.TrackList.AddRange(new[] { song, other });
            ((List<Album>)lib.Albums).Add(new Album { Id = albumId, Name = "Lover", Artist = "Taylor Swift", Tracks = new List<Track> { song, other } });
            var vm = new LyricsBackgroundPickerViewModel(new SettingsViewModel(new PersistenceService(root), lib, new NoOpPlayHistory()), lib);
            Assert.True(vm.ShowPrompt);

            vm.SearchText = "dont";
            vm.SearchText = "dont you";
            Assert.Empty(vm.Results); // nothing scanned while the user is still typing

            await vm.SearchRefresh;
            var row = Assert.Single(vm.Results); // curly apostrophes fold away
            Assert.Equal(LyricsBackgroundOverrides.KeyForTrack(song), row.Key);
            Assert.True(vm.IsSearching);

            vm.SearchText = "lover";
            await vm.SearchRefresh;
            Assert.Equal(3, vm.Results.Count); // the album row, then both songs
            Assert.True(vm.Results[0].IsAlbum);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }
}
