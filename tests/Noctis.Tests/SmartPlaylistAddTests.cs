using System.Collections.ObjectModel;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Audit S22: the "Add to Playlist" dialog listed smart playlists, and picking one
/// appended track ids that never showed (contents come from the rules) while the
/// tile's track count changed. The dialog now lists only manual playlists and
/// AddTracksToPlaylist ignores smart ones.
/// </summary>
public class SmartPlaylistAddTests
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
        FilePath = TestPaths.Primary("smartadd", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    [Fact]
    public void Dialog_ListsOnlyManualPlaylists()
    {
        var manual = new PlaylistNavItem { Key = "m", Label = "Manual", PlaylistId = Guid.NewGuid() };
        var smart = new PlaylistNavItem { Key = "s", Label = "Smart", PlaylistId = Guid.NewGuid(), IsSmartPlaylist = true };

        var vm = new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem> { manual, smart }, 1);

        Assert.Equal(new[] { manual }, vm.Playlists);
    }

    [AvaloniaFact]
    public async Task AddTracksToPlaylist_OnSmartPlaylist_IsIgnored()
    {
        var persistence = new CountingPersistence();
        var sidebar = new SidebarViewModel(persistence, new FakeLibraryService());
        var playlist = new Playlist
        {
            Name = "Top rated",
            IsSmartPlaylist = true,
            MatchAll = true,
            Rules = [new SmartPlaylistRule { Field = RuleField.Rating, Operator = RuleOperator.GreaterThan, Value = "3" }],
        };
        sidebar.Playlists.Add(playlist);

        await sidebar.AddTracksToPlaylist(playlist.Id, new[] { Trk("A"), Trk("B") });

        Assert.Empty(playlist.TrackIds);
        Assert.Equal(0, persistence.PlaylistSaves);
    }
}
