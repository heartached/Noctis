using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Library › Media Folder Locations: the ✕ on a folder asks first, naming how many
/// songs leave the library. Cancel leaves the folder list, the settings and the library alone.
/// </summary>
public class RemoveMusicFolderConfirmTests
{
    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static readonly string Music = Path.Combine(Path.GetTempPath(), "NoctisTests", "RemoveFolder", "Music");
    private static readonly string Other = Path.Combine(Path.GetTempPath(), "NoctisTests", "RemoveFolder", "Other");

    [AvaloniaFact]
    public async Task RemoveFolder_AsksWithTheSongCount_CancelChangesNothing_ConfirmRemovesAndRescans()
    {
        try
        {
            Loc.Instance.SetCulture("en");
            var library = new FakeLibraryService();
            library.TrackList.Add(new Track { Title = "a", FilePath = Path.Combine(Music, "A", "a.mp3") });
            library.TrackList.Add(new Track { Title = "b", FilePath = Path.Combine(Music, "b.mp3") });
            library.TrackList.Add(new Track { Title = "added", FilePath = Path.Combine(Music, "c.mp3"), AddedIndividually = true });
            library.TrackList.Add(new Track { Title = "other", FilePath = Path.Combine(Other, "o.mp3") });
            library.TrackList.Add(new Track { Title = "sibling", FilePath = Path.Combine(Music + "Videos", "v.mp3") });
            using var persistence = new TestPersistenceService();
            var vm = new SettingsViewModel(persistence, library, new NoOpPlayHistoryService());
            vm.MusicFolders.Add(Music);
            vm.MusicFolders.Add(Other);
            var asked = new List<ConfirmationRequest>();
            var answer = false;
            vm.ShowPluginDialog = r => { asked.Add(r); return Task.FromResult(new ConfirmationResult(answer, false)); };

            await vm.RemoveFolderCommand.ExecuteAsync(Music);

            var request = Assert.Single(asked);
            Assert.Equal("Remove this music folder?", request.Title);
            Assert.Equal($"Your library loses 2 songs from “{Music}”. The files stay where they are.", request.Message);
            Assert.Equal("Add the folder again to bring them back, with their play counts, ratings and favorites.", request.Note);
            Assert.Equal("Remove", request.ConfirmText);
            Assert.Equal(new[] { Music, Other }, vm.MusicFolders);
            Assert.Empty(library.ScanPasses);

            answer = true;
            await vm.RemoveFolderCommand.ExecuteAsync(Music);
            Assert.Equal(new[] { Other }, vm.MusicFolders);
            // The rescan is fire-and-forget.
            for (var i = 0; i < 100 && library.ScanPasses.Count == 0; i++)
                await Task.Delay(20, TestContext.Current.CancellationToken);
            Assert.Equal(new[] { Other }, Assert.Single(library.ScanPasses));

            Loc.Instance.SetCulture("ru");
            Assert.Equal($"Из медиатеки будут убраны песни из «{Other}»: 1 песня. Файлы останутся на месте.",
                Loc.T("Settings.RemoveFolderBody", Loc.Plural("Count.Songs", SettingsViewModel.SongsLeavingWithFolder(library.AllTracks, Other)), Other));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }
}
