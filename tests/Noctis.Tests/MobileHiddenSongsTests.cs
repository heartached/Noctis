using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord #mobile-beta (Duckyvictus): "a blacklist for audio that you don't want to show up on
/// your lists". Hide Song puts the song's path in the desktop's hidden-folder list
/// (LibraryService.SetFolderHiddenAsync, which hides that one file); Settings › Library › Hidden lists it
/// with Unhide. The rig's library is the fake, which hides by path the way LibraryService does.
/// </summary>
public class MobileHiddenSongsTests
{
    private static Track Song(string title, string path) =>
        new() { Title = title, Artist = "Someone", Album = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };

    [AvaloniaFact]
    public async Task HideSong_LeavesItOutOfSongsSearchAndShuffle_AndSettingsBringsItBack()
    {
        var voice = Song("PTT-20261107-WA0003", "content://media/external/audio/media/41");
        var song = Song("Real Song", "content://media/external/audio/media/42");
        using var rig = MobileFixtures.MakeRig(new[] { voice, song });
        var shell = rig.Shell;

        var menu = ContextSheetViewModel.ForTrack(shell, voice);
        Assert.True(menu.CanHide);
        await menu.HideCommand.ExecuteAsync(null);

        Assert.False(shell.IsSheetOpen);
        Assert.Equal(new[] { voice.FilePath }, rig.Library.HiddenFolders);
        Assert.DoesNotContain(voice, shell.Library.Songs);
        Assert.Contains(song, shell.Library.Songs);
        Assert.Equal(1, shell.Library.SongCount);
        Assert.Empty(MobileSearch.Find(rig.Library, "PTT", 10, 10, 10).Songs);
        Assert.Equal("Hidden. Settings › Library › Hidden brings it back.", shell.Player.Notice);

        shell.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
        Assert.True(settings.HasHidden);
        Assert.Equal("1", settings.HiddenValue);
        Assert.Equal("Hidden, 1", settings.HiddenRowName);
        settings.OpenPaneCommand.Execute(SettingsPane.Hidden);
        Assert.True(settings.ShowsHiddenPane);
        Assert.Equal("Hidden", settings.HeaderTitle);
        var row = Assert.Single(settings.HiddenRows);
        Assert.Equal("PTT-20261107-WA0003", row.Title);
        Assert.Equal("Someone", row.Subtitle);
        Assert.Equal("Unhide PTT-20261107-WA0003", row.UnhideName);

        await settings.UnhideCommand.ExecuteAsync(row);

        Assert.Empty(rig.Library.HiddenFolders);
        Assert.Contains(voice, shell.Library.Songs);
        Assert.False(settings.HasHidden);
        Assert.Empty(settings.HiddenRows);
        Assert.Equal(string.Empty, settings.HiddenValue);
    }

    [AvaloniaFact]
    public void TheHiddenPage_NamesAFolder_AndASongNoLongerHere()
    {
        var song = Song("Kept", "content://media/external/audio/media/7");
        using var rig = MobileFixtures.MakeRig(new[] { song });
        rig.Library.HiddenList.Add("content://com.android.externalstorage.documents/tree/primary%3ARecordings%2FVoice");
        rig.Library.HiddenList.Add("content://media/external/audio/media/99");
        rig.Shell.OpenSettingsCommand.Execute(null);
        var settings = Assert.IsType<SettingsPageViewModel>(rig.Shell.CurrentPage);

        Assert.Collection(settings.HiddenRows,
            folder =>
            {
                Assert.Equal("Recordings/Voice", folder.Title);
                Assert.Equal("Folder", folder.Subtitle);
            },
            gone => Assert.Equal("Not on this phone now", gone.Subtitle));
    }

    [Fact]
    public void OnlyASongsMenu_OffersHideSong()
    {
        var a = Song("One", "content://media/external/audio/media/1");
        var album = MobileFixtures.MakeAlbum("Alpha", "Band", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { album });

        Assert.True(ContextSheetViewModel.ForTrack(rig.Shell, a).CanHide);
        Assert.False(ContextSheetViewModel.ForAlbum(rig.Shell, album).CanHide);
    }
}
