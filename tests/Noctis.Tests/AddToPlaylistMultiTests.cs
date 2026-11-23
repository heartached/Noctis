using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Two users (11-23): Add to Playlist into several playlists at once. A click on a row ticks it;
/// the footer's Add puts the songs in every ticked playlist and closes; a double click or Enter
/// on a row still adds at once (with any ticked rows). Each playlist takes only the songs it
/// lacks, a full row never ticks, and the island pill says where the songs went.
/// </summary>
[Collection("Localization")]
public class AddToPlaylistMultiTests : IDisposable
{
    public AddToPlaylistMultiTests() => Loc.Instance.SetCulture("en");
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
        FilePath = TestPaths.Primary("multiadd", $"{Guid.NewGuid():N}.mp3"),
        Duration = TimeSpan.FromMinutes(3),
    };

    private static (AddToPlaylistDialogViewModel Vm, List<IReadOnlyList<PlaylistNavItem>> Picks, Func<int> Closes) Vm(int n)
    {
        var vm = new AddToPlaylistDialogViewModel(AddToPlaylistDialogTests.Lists(n), 2);
        var picks = new List<IReadOnlyList<PlaylistNavItem>>();
        var closes = 0;
        vm.PlaylistsSelected += (_, p) => picks.Add(p);
        vm.CloseRequested += (_, _) => closes++;
        return (vm, picks, () => closes);
    }

    [Fact]
    public void ClickingRows_Ticks_AndAddPutsTheSongsInEveryTickedOne_Once()
    {
        var (vm, picks, closes) = Vm(4);
        Assert.False(vm.AddCheckedCommand.CanExecute(null));

        vm.ToggleRowCommand.Execute(vm.Rows[2]);
        vm.ToggleRowCommand.Execute(vm.Rows[0]);
        vm.ToggleRowCommand.Execute(vm.Rows[3]);
        vm.ToggleRowCommand.Execute(vm.Rows[3]); // untick
        Assert.Empty(picks);
        Assert.Equal(0, closes());
        Assert.Equal(2, vm.CheckedCount);
        Assert.True(vm.Rows[0].IsChecked && vm.Rows[2].IsChecked && !vm.Rows[3].IsChecked);
        Assert.True(vm.AddCheckedCommand.CanExecute(null));

        vm.AddCheckedCommand.Execute(null);
        vm.AddCheckedCommand.Execute(null);
        Assert.Single(picks);
        // In list order, not tick order.
        Assert.Equal(new[] { vm.Playlists[0], vm.Playlists[2] }, picks[0]);
        Assert.Equal(1, closes());
    }

    [Fact]
    public void AddButton_ReadsTheCount()
    {
        var (vm, _, _) = Vm(3);
        Assert.Equal("Add", vm.AddButtonText);
        vm.ToggleRowCommand.Execute(vm.Rows[0]);
        Assert.Equal("Add", vm.AddButtonText);
        vm.ToggleRowCommand.Execute(vm.Rows[1]);
        Assert.Equal("Add to 2 Playlists", vm.AddButtonText);
    }

    [Fact]
    public void DoubleClickOrEnter_AloneAddsJustThatRow_WithTicksAddsThemToo()
    {
        var (vm, picks, closes) = Vm(3);
        vm.SelectPlaylistCommand.Execute(vm.Playlists[1]);
        Assert.Equal(new[] { vm.Playlists[1] }, picks.Single());
        Assert.Equal(1, closes());

        (vm, picks, closes) = Vm(3);
        vm.ToggleRowCommand.Execute(vm.Rows[2]);
        // A double click is two clicks (tick, untick) and then the double tap: still added.
        vm.ToggleRowCommand.Execute(vm.Rows[0]);
        vm.ToggleRowCommand.Execute(vm.Rows[0]);
        vm.SelectPlaylistCommand.Execute(vm.Playlists[0]);
        Assert.Equal(new[] { vm.Playlists[0], vm.Playlists[2] }, picks.Single());
    }

    [Fact]
    public void AFullRow_NeverTicks()
    {
        var song = Guid.NewGuid();
        var lists = AddToPlaylistDialogTests.Lists(2);
        var full = lists[0].PlaylistId!.Value;
        var vm = new AddToPlaylistDialogViewModel(lists, 1, new[] { song },
            id => id == full ? new[] { song } : Array.Empty<Guid>());
        vm.ToggleRowCommand.Execute(vm.Rows[0]);
        Assert.False(vm.Rows[0].IsChecked);
        Assert.Equal(0, vm.CheckedCount);
    }

    [Fact]
    public void WhileTheCreateFormIsOpen_RowsDoNotTick()
    {
        var (vm, _, _) = Vm(2);
        vm.ShowCreateCommand.Execute(null);
        vm.ToggleRowCommand.Execute(vm.Rows[0]);
        Assert.Equal(0, vm.CheckedCount);
    }

    [AvaloniaFact]
    public async Task Sidebar_AddsToEachPickedPlaylist_OnlyWhatEachLacks_SavesOnce_AndConfirms()
    {
        var persistence = new CountingPersistence();
        var sidebar = new SidebarViewModel(persistence, new FakeLibraryService());
        var a = Trk("A");
        var b = Trk("B");
        var gym = new Playlist { Name = "Gym" };
        gym.TrackIds.Add(a.Id);
        var road = new Playlist { Name = "Road" };
        var smart = new Playlist { Name = "Smart", IsSmartPlaylist = true };
        foreach (var p in new[] { gym, road, smart }) sidebar.Playlists.Add(p);
        var changed = new List<Guid>();
        sidebar.PlaylistTracksChanged += (_, id) => changed.Add(id);
        var toasts = new List<(string Headline, string Detail)>();
        sidebar.ToastRequested += (_, t) => toasts.Add(t);

        await sidebar.AddPickedPlaylistsAsync(new[] { gym.Id, road.Id, smart.Id }, new List<Track> { a, b });

        Assert.Equal(new[] { a.Id, b.Id }, gym.TrackIds);   // a was there: not twice
        Assert.Equal(new[] { a.Id, b.Id }, road.TrackIds);
        Assert.Empty(smart.TrackIds);
        Assert.Equal(1, persistence.PlaylistSaves);
        Assert.Equal(new[] { gym.Id, road.Id }, changed);
        Assert.Equal(("Added to 2 playlists", "2 songs"), toasts.Single());

        toasts.Clear();
        await sidebar.AddPickedPlaylistsAsync(new[] { road.Id }, new List<Track> { Trk("Solo") });
        Assert.Equal(("Added to Road", "Solo"), toasts.Single());
    }

    [AvaloniaFact]
    public void View_ClickTicksWithTheCheck_AddButtonAddsAll()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var (vm, picks, _) = Vm(3);
        var win = new AddToPlaylistDialog { DataContext = vm, Width = 1100, Height = 820 };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var rows = win.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("atp-row")).ToList();
            var add = win.FindControl<Button>("AddButton")!;
            Assert.True(add.IsEffectivelyVisible);
            Assert.False(add.IsEffectivelyEnabled);
            Assert.Contains("pill-primary", add.Classes);

            rows[0].Command!.Execute(rows[0].CommandParameter);
            rows[2].Command!.Execute(rows[2].CommandParameter);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("checked", rows[0].Classes);
            Assert.DoesNotContain("checked", rows[1].Classes);
            Assert.Contains("checked", rows[2].Classes);
            var fill = rows[0].GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("atp-check-fill"));
            // The tick eases in (a transition, not a snap).
            Assert.NotNull(fill.Transitions);
            Assert.True(add.IsEffectivelyEnabled);
            Assert.Equal("Add to 2 Playlists", add.Content);

            add.Command!.Execute(null);
            Assert.Equal(new[] { vm.Playlists[0], vm.Playlists[2] }, picks.Single());
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
