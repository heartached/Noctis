using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Mistery, 10-10) "Already on playlist" indicator: a playlist holds a song once, so
/// picking one in Add to Playlist that had the song already silently added nothing. A row whose
/// playlist has every song now reads "… · Already on playlist" and is greyed and not pickable
/// (as the Add Songs picker's in-playlist rows); one with only some reads "… · N already on
/// playlist" and stays pickable (the rest go in).
/// </summary>
[Collection("Localization")]
public class AddToPlaylistAlreadyOnTests : IDisposable
{
    public AddToPlaylistAlreadyOnTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

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
        FilePath = TestPaths.Primary("alreadyon", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static PlaylistNavItem Nav(string label, Guid id, string meta = "67 tracks · 3 hr 17 min")
        => new() { Key = label, Label = label, PlaylistId = id, MetaText = meta };

    /// <summary>Three playlists: one with both songs, one with one of them, one with neither.</summary>
    private static (AddToPlaylistDialogViewModel Vm, PlaylistNavItem All, PlaylistNavItem Some, PlaylistNavItem None)
        TwoSongs()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var all = Nav("All", Guid.NewGuid());
        var some = Nav("Some", Guid.NewGuid());
        var none = Nav("None", Guid.NewGuid());
        var contents = new Dictionary<Guid, List<Guid>>
        {
            [all.PlaylistId!.Value] = new() { Guid.NewGuid(), b, a },
            [some.PlaylistId!.Value] = new() { a, Guid.NewGuid() },
            [none.PlaylistId!.Value] = new() { Guid.NewGuid() },
        };
        var vm = new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem> { all, some, none }, 2,
            new[] { a, b }, id => contents.GetValueOrDefault(id));
        return (vm, all, some, none);
    }

    private static AddToPlaylistRow RowOf(AddToPlaylistDialogViewModel vm, PlaylistNavItem p)
        => vm.Rows.Single(r => r.Playlist == p);

    [AvaloniaFact]
    public async Task AddTracksToPlaylist_SongAlreadyIn_AddsNothing()
    {
        // The behaviour the indicator explains: the add skips ids already in the playlist
        // (SidebarViewModel.AddTracksToPlaylist's HashSet), with no message to the user.
        var persistence = new CountingPersistence();
        var sidebar = new SidebarViewModel(persistence, new FakeLibraryService());
        var song = Trk("A");
        var playlist = new Playlist { Name = "Mix" };
        playlist.TrackIds.Add(song.Id);
        sidebar.Playlists.Add(playlist);

        await sidebar.AddTracksToPlaylist(playlist.Id, new[] { song });

        Assert.Equal(new[] { song.Id }, playlist.TrackIds);
    }

    [Fact]
    public void Rows_MarkAll_Some_None()
    {
        var (vm, all, some, none) = TwoSongs();

        var rAll = RowOf(vm, all);
        Assert.True(rAll.IsAlreadyOnPlaylist);
        Assert.Equal(2, rAll.AlreadyCount);
        Assert.Equal("67 tracks · 3 hr 17 min · Already on playlist", rAll.SubtitleText);

        var rSome = RowOf(vm, some);
        Assert.False(rSome.IsAlreadyOnPlaylist);
        Assert.Equal(1, rSome.AlreadyCount);
        Assert.Equal("67 tracks · 3 hr 17 min · 1 already on playlist", rSome.SubtitleText);

        var rNone = RowOf(vm, none);
        Assert.False(rNone.IsAlreadyOnPlaylist);
        Assert.Equal(0, rNone.AlreadyCount);
        Assert.Equal("67 tracks · 3 hr 17 min", rNone.SubtitleText);
    }

    [Fact]
    public void Rows_DuplicateIdsInPlaylist_CountOnce()
    {
        // An older playlist could hold an id twice; it still has one of the two songs.
        var a = Guid.NewGuid();
        var p = Nav("Dup", Guid.NewGuid(), meta: "");
        var vm = new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem> { p }, 2,
            new[] { a, Guid.NewGuid() }, _ => new[] { a, a });
        var row = vm.Rows.Single();
        Assert.Equal(1, row.AlreadyCount);
        Assert.False(row.IsAlreadyOnPlaylist);
        Assert.Equal("1 already on playlist", row.SubtitleText);
    }

    [Fact]
    public void Rows_WithoutTrackIds_MarkNothing()
    {
        var vm = new AddToPlaylistDialogViewModel(AddToPlaylistDialogTests.Lists(2, withSmart: true), 1);
        Assert.Equal(2, vm.Rows.Count);
        Assert.All(vm.Rows, r => Assert.False(r.IsAlreadyOnPlaylist));
        Assert.Equal(vm.Playlists, vm.Rows.Select(r => r.Playlist));
    }

    [Fact]
    public void SelectPlaylist_OnAFullRow_DoesNothing_OtherRowsStillPick()
    {
        var (vm, all, some, _) = TwoSongs();
        var picked = new List<PlaylistNavItem>();
        var closes = 0;
        vm.PlaylistSelected += (_, p) => picked.Add(p);
        vm.CloseRequested += (_, _) => closes++;

        vm.SelectPlaylistCommand.Execute(all);
        Assert.Empty(picked);
        Assert.Equal(0, closes);

        // A partial row still takes the pick (only the missing song goes in).
        vm.SelectPlaylistCommand.Execute(some);
        Assert.Equal(new[] { some }, picked);
        Assert.Equal(1, closes);
    }

    [AvaloniaFact]
    public void Sidebar_PassesEachPlaylistsTracks_ToTheDialog()
    {
        var sidebar = new SidebarViewModel(new CountingPersistence(), new FakeLibraryService());
        var a = Trk("A");
        var b = Trk("B");
        var full = new Playlist { Name = "Full" };
        full.TrackIds.AddRange(new[] { a.Id, b.Id });
        var half = new Playlist { Name = "Half" };
        half.TrackIds.Add(b.Id);
        var smart = new Playlist { Name = "Smart", IsSmartPlaylist = true };
        foreach (var p in new[] { full, half, smart })
        {
            sidebar.Playlists.Add(p);
            sidebar.PlaylistItems.Add(new PlaylistNavItem
            {
                Key = p.Name, Label = p.Name, PlaylistId = p.Id, IsSmartPlaylist = p.IsSmartPlaylist,
            });
        }

        var vm = sidebar.CreateAddToPlaylistViewModel(new List<Track> { a, b });

        // Smart playlists stay out of the list (filled by their rules).
        Assert.Equal(new[] { "Full", "Half" }, vm.Rows.Select(r => r.Playlist.Label));
        Assert.True(vm.Rows[0].IsAlreadyOnPlaylist);
        Assert.False(vm.Rows[1].IsAlreadyOnPlaylist);
        Assert.Equal(1, vm.Rows[1].AlreadyCount);
    }

    [AvaloniaFact]
    public void View_FullRow_IsMutedAndUnpickable_PartialRowIsNot()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var (vm, _, _, _) = TwoSongs();
        var win = new AddToPlaylistDialog { DataContext = vm, Width = 1100, Height = 820 };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var rows = win.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("atp-row")).ToList();
            Assert.Equal(3, rows.Count);

            TextBlock Subtitle(Button row) => row.GetVisualDescendants().OfType<TextBlock>().ElementAt(1);
            double CoverOpacity(Button row) => row.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("atp-cover")).Opacity;
            double TextOpacity(Button row) => row.GetVisualDescendants().OfType<StackPanel>().First(s => s.Classes.Contains("atp-text")).Opacity;

            var full = rows[0];
            Assert.Contains("on-playlist", full.Classes);
            Assert.False(full.IsHitTestVisible);
            Assert.False(full.Focusable);
            Assert.Equal(0.5, CoverOpacity(full));
            Assert.Equal(0.5, TextOpacity(full));
            Assert.Equal("67 tracks · 3 hr 17 min · Already on playlist", Subtitle(full).Text);

            foreach (var row in rows.Skip(1))
            {
                Assert.DoesNotContain("on-playlist", row.Classes);
                Assert.True(row.IsHitTestVisible);
                Assert.Equal(1, CoverOpacity(row));
                Assert.Equal(1, TextOpacity(row));
            }
            Assert.Equal("67 tracks · 3 hr 17 min · 1 already on playlist", Subtitle(rows[1]).Text);
            Assert.Equal("67 tracks · 3 hr 17 min", Subtitle(rows[2]).Text);
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
