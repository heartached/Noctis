using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit S15: "Remove from Playlist" on a smart playlist dropped the row from the page
/// and saved an unchanged playlist; the contents come from the rules, so the song came
/// back on the next reload. Remove is now a no-op there (and hidden from the menu).
/// </summary>
public class SmartPlaylistRemoveTests
{
    private sealed class CountingPersistence : TestPersistenceService
    {
        public int PlaylistSaves;
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            PlaylistSaves++;
            return Task.CompletedTask;
        }
    }

    private static Track Trk(string title) => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = "Artist",
        Rating = 5,
        FilePath = TestPaths.Primary("smartrm", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public async Task RemoveTrack_OnSmartPlaylist_LeavesRowsAndDoesNotSave()
    {
        var tracks = new List<Track> { Trk("A"), Trk("B") };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new CountingPersistence();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var playlist = new Playlist
        {
            Name = "Top rated",
            IsSmartPlaylist = true,
            MatchAll = true,
            Rules = [new SmartPlaylistRule { Field = RuleField.Rating, Operator = RuleOperator.GreaterThan, Value = "3" }],
        };
        sidebar.Playlists.Add(playlist);
        var vm = new PlaylistViewModel(playlist, player, lib, persistence, sidebar);
        await PumpUntil(() => vm.Tracks.Count == tracks.Count);
        Assert.Equal(tracks.Count, vm.Tracks.Count);
        var savesBefore = persistence.PlaylistSaves;

        await vm.RemoveTrackCommand.ExecuteAsync(tracks[0]);

        Assert.Equal(tracks.Count, vm.Tracks.Count);
        Assert.Equal(tracks.Count, vm.TrackCount);
        Assert.Equal(savesBefore, persistence.PlaylistSaves);
    }
}
