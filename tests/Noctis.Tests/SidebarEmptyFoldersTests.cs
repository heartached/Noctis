using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Views;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-23 sidebar folders: "New Folder…" (the PLAYLISTS header's menu, and a right-click on the
/// empty part of the playlist area) makes a folder that shows while it is still empty, until it
/// is removed. A folder is otherwise only the Folder string on its playlists, so an empty one is
/// kept in playlist_folders.json beside playlists.json (local: playlist sync carries no folders).
/// "Move to Folder" is a submenu of the folders, No Folder and New Folder… instead of a typed name.
/// </summary>
[Collection("Localization")]
public class SidebarEmptyFoldersTests : IDisposable
{
    public SidebarEmptyFoldersTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

    private static HashSet<string> NoCollapsed => new(StringComparer.OrdinalIgnoreCase);

    private static PlaylistNavItem Item(string name, string folder = "")
        => new() { Key = $"playlist:{name}", Label = name, PlaylistId = Guid.NewGuid(), Folder = folder };

    private sealed class Prompts
    {
        public Queue<string?> Answers = new();
        public List<string> Titles = new();
        public int Confirms;
        public bool ConfirmAnswer = true;
    }

    private static (SidebarViewModel Vm, TestPersistenceService Store, Prompts Prompts) Sidebar(params Playlist[] playlists)
    {
        var store = new TestPersistenceService();
        var vm = new SidebarViewModel(store, new FakeLibraryService());
        var prompts = new Prompts();
        vm.PromptText = (title, _, _, _) => { prompts.Titles.Add(title); return Task.FromResult(prompts.Answers.Dequeue()); };
        vm.ConfirmAction = _ => { prompts.Confirms++; return Task.FromResult(prompts.ConfirmAnswer); };
        foreach (var p in playlists)
        {
            vm.Playlists.Add(p);
            vm.PlaylistItems.Add(new PlaylistNavItem { Key = $"playlist:{p.Id}", Label = p.Name, PlaylistId = p.Id, Folder = p.Folder });
        }
        vm.RebuildSidebarRows();
        return (vm, store, prompts);
    }

    private static PlaylistNavItem? FolderRow(SidebarViewModel vm, string name)
        => vm.SidebarRows.FirstOrDefault(r => r.IsFolder && string.Equals(r.Label, name, StringComparison.OrdinalIgnoreCase));

    // ── Rows ──

    [Fact]
    public void BuildRows_ShowsAnEmptyFolder_AmongTheFolders_WithNoTray()
    {
        var rows = SidebarViewModel.BuildRows(new[] { Item("Loose"), Item("Gym", folder: "Rock") }, NoCollapsed,
            emptyFolders: new[] { "Chill", "rock" });

        Assert.Equal(new[] { "Chill", "Rock", "Gym", "Loose" }, rows.Select(r => r.Label));
        var chill = rows[0];
        Assert.True(chill.IsFolder);
        Assert.Equal("folder:Chill", chill.Key);
        Assert.Equal(0, chill.TrackCount);
        // No playlists under it: no open tray drawn under the header.
        Assert.Equal(SidebarGroupPosition.None, chill.GroupPosition);
        Assert.Single(rows, r => r.IsFolder && r.Label.Equals("Rock", StringComparison.OrdinalIgnoreCase));
    }

    // ── New Folder… ──

    [AvaloniaFact]
    public async Task NewFolder_ShowsAtOnce_IsSaved_AndComesBackAfterAReload()
    {
        var (vm, store, prompts) = Sidebar(new Playlist { Name = "Loose" });
        prompts.Answers.Enqueue("  Chill  ");

        await vm.NewFolderCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("Sidebar.NewFolderTitle"), prompts.Titles.Single());
        Assert.NotNull(FolderRow(vm, "Chill"));
        Assert.Equal(new[] { "Chill" }, store.PlaylistFolders);
        Assert.Contains("Chill", vm.GetFolderNames());

        var again = new SidebarViewModel(store, new FakeLibraryService());
        await again.LoadPlaylistsAsync();
        Assert.NotNull(FolderRow(again, "Chill"));
    }

    [AvaloniaFact]
    public async Task NewFolder_BlankOrCancelledOrExisting_AddsNothing()
    {
        var (vm, store, prompts) = Sidebar(new Playlist { Name = "Gym", Folder = "Rock" });
        prompts.Answers.Enqueue(null);
        prompts.Answers.Enqueue("   ");
        prompts.Answers.Enqueue("rock");
        for (var i = 0; i < 3; i++) await vm.NewFolderCommand.ExecuteAsync(null);

        Assert.Empty(store.PlaylistFolders);
        Assert.Single(vm.SidebarRows, r => r.IsFolder);
    }

    [AvaloniaFact]
    public async Task AnEmptyFolder_TakesAPlaylist_AndStaysWhenItLeavesAgain()
    {
        var road = new Playlist { Name = "Road" };
        var (vm, store, prompts) = Sidebar(road);
        prompts.Answers.Enqueue("Chill");
        await vm.NewFolderCommand.ExecuteAsync(null);

        var item = vm.PlaylistItems.Single();
        var into = vm.FolderChoicesFor(item).Single(c => c.Folder == "Chill");
        await vm.MoveToFolderChoiceCommand.ExecuteAsync(into);
        Assert.Equal("Chill", road.Folder);
        var rows = vm.SidebarRows.Select(r => r.Label).ToList();
        Assert.Equal(new[] { "Chill", "Road" }, rows);
        Assert.Single(vm.SidebarRows, r => r.IsFolder);

        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(item).Single(c => c.Folder == null && !c.IsNew));
        Assert.Equal("", road.Folder);
        // Made with New Folder…: it stays (empty) until it is removed.
        Assert.NotNull(FolderRow(vm, "Chill"));
    }

    [AvaloniaFact]
    public async Task RenamingAndRemovingAnEmptyFolder_UpdateTheSavedList()
    {
        var (vm, store, prompts) = Sidebar();
        prompts.Answers.Enqueue("Chill");
        await vm.NewFolderCommand.ExecuteAsync(null);

        prompts.Answers.Enqueue("Calm");
        await vm.RenameFolderCommand.ExecuteAsync(FolderRow(vm, "Chill"));
        Assert.Null(FolderRow(vm, "Chill"));
        Assert.NotNull(FolderRow(vm, "Calm"));
        Assert.Equal(new[] { "Calm" }, store.PlaylistFolders);

        // Nothing in it to keep: removed without asking.
        await vm.DissolveFolderCommand.ExecuteAsync(FolderRow(vm, "Calm"));
        Assert.Equal(0, prompts.Confirms);
        Assert.Null(FolderRow(vm, "Calm"));
        Assert.Empty(store.PlaylistFolders);
    }

    [AvaloniaFact]
    public async Task RemovingAFolderWithPlaylists_StillAsks_AndForgetsTheSavedName()
    {
        var gym = new Playlist { Name = "Gym" };
        var (vm, store, prompts) = Sidebar(gym);
        prompts.Answers.Enqueue("Rock");
        await vm.NewFolderCommand.ExecuteAsync(null);
        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(vm.PlaylistItems.Single()).Single(c => c.Folder == "Rock"));

        await vm.DissolveFolderCommand.ExecuteAsync(FolderRow(vm, "Rock"));
        Assert.Equal(1, prompts.Confirms);
        Assert.Equal("", gym.Folder);
        Assert.Null(FolderRow(vm, "Rock"));
        Assert.Empty(store.PlaylistFolders);
    }

    // ── Move to Folder submenu ──

    [AvaloniaFact]
    public void FolderChoices_AreTheFolders_ThenNoFolder_ThenNewFolder_WithTheCurrentOneMarked()
    {
        var gym = new Playlist { Name = "Gym", Folder = "Rock" };
        var (vm, store, _) = Sidebar(gym, new Playlist { Name = "Calm", Folder = "Ambient" });
        vm.UseSavedFolders(new[] { "Zen" });

        var item = vm.PlaylistItems.First(i => i.PlaylistId == gym.Id);
        var choices = vm.FolderChoicesFor(item);
        Assert.Equal(new[] { "Ambient", "Rock", "Zen", Loc.T("Sidebar.NoFolder"), Loc.T("Sidebar.NewFolder") },
            choices.Select(c => c.Label));
        Assert.Equal(new[] { false, true, false, false, false }, choices.Select(c => c.IsCurrent));
        Assert.True(choices[^1].IsNew);
        Assert.All(choices, c => Assert.Same(item, c.Item));
    }

    [AvaloniaFact]
    public async Task FolderChoice_NewFolder_AsksForTheNameOnly_AndMovesThePlaylist()
    {
        var gym = new Playlist { Name = "Gym" };
        var (vm, _, prompts) = Sidebar(gym);
        prompts.Answers.Enqueue("Workout");

        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(vm.PlaylistItems.Single()).Single(c => c.IsNew));

        Assert.Equal(Loc.T("Sidebar.NewFolderTitle"), prompts.Titles.Single());
        Assert.Equal("Workout", gym.Folder);
        Assert.NotNull(FolderRow(vm, "Workout"));
    }

    // ── View ──

    [AvaloniaFact]
    public void View_MoveToFolder_IsASubmenu_AndAnEmptyAreaRightClickIsThePlaylistsMenu()
    {
        ContextMenuV2ViewsTests.EnsureAppStyles();
        var road = new Playlist { Name = "Road" };
        var (vm, _, _) = Sidebar(road, new Playlist { Name = "Gym", Folder = "Rock" });
        vm.TopBar = new TopBarViewModel();
        var view = new SidebarView { DataContext = vm };
        var win = new Avalonia.Controls.Window { Width = 300, Height = 900, Content = view };
        win.Show();
        for (var i = 0; i < 4; i++) Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        try
        {
            var list = view.FindControl<Avalonia.Controls.ListBox>("PlaylistList")!;
            var item = vm.PlaylistItems.First(p => p.PlaylistId == road.Id);
            var row = list.GetRealizedContainers().OfType<Avalonia.Controls.ListBoxItem>().First(c => c.DataContext == item);
            var menu = row.ContextMenu!;
            menu.DataContext = item;
            menu.Open(row);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var move = menu.Items.OfType<Avalonia.Controls.MenuItem>().Single(m => m.Classes.Contains("move-to-folder"));
            Assert.Null(move.Command);
            Assert.Equal(new[] { "Rock", "-", Loc.T("Sidebar.NoFolder"), Loc.T("Sidebar.NewFolder") },
                move.Items.Select(o => o is Avalonia.Controls.MenuItem m ? (string)m.Header! : "-"));
            Assert.All(move.Items.OfType<Avalonia.Controls.MenuItem>(), m => Assert.Same(vm.MoveToFolderChoiceCommand, m.Command));
            // Road is in no folder: No Folder carries the tick.
            var noFolder = move.Items.OfType<Avalonia.Controls.MenuItem>().ElementAt(1);
            Assert.Same(MenuV2Geometry(view, "MenuLineCheck"), Noctis.Helpers.MenuV2.IconPath(noFolder.Icon)!.Data);
            menu.Close();

            // Empty space: the scroller's own background, under the PLAYLISTS header.
            var presenter = view.FindControl<Avalonia.Controls.ScrollViewer>("NavScroller")!
                .GetVisualDescendants().OfType<Avalonia.Controls.Presenters.ScrollContentPresenter>().First();
            Assert.True(view.IsEmptyPlaylistAreaPress(presenter, 400));
            Assert.False(view.IsEmptyPlaylistAreaPress(presenter, -20)); // up in the main navigation
            Assert.False(view.IsEmptyPlaylistAreaPress(row, 10));          // a playlist row has its own menu
        }
        finally { win.Close(); }
    }

    private static Avalonia.Media.Geometry? MenuV2Geometry(Avalonia.Controls.Control host, string key)
        => Noctis.Helpers.MenuV2.FindGeometry(host, key);

    // ── New Playlist and Folder Position + Remove Empty Folders (Discord 11-24, sina) ──

    /// <summary>Keeps the saved playlists (as JSON copies), so a reload reads what was saved.</summary>
    private sealed class PlaylistStore : TestPersistenceService
    {
        private string _json = "[]";
        public override Task<List<Playlist>> LoadPlaylistsAsync()
            => Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<List<Playlist>>(_json)!);
        public override Task SavePlaylistsAsync(List<Playlist> playlists)
        {
            _json = System.Text.Json.JsonSerializer.Serialize(playlists);
            return Task.CompletedTask;
        }
    }

    private static async Task<(SidebarViewModel Vm, PlaylistStore Store, Prompts Prompts)> StoredSidebar(params Playlist[] playlists)
    {
        var store = new PlaylistStore();
        await store.SavePlaylistsAsync(playlists.ToList());
        var vm = new SidebarViewModel(store, new FakeLibraryService());
        var prompts = new Prompts();
        vm.PromptText = (title, _, _, _) => { prompts.Titles.Add(title); return Task.FromResult(prompts.Answers.Dequeue()); };
        vm.ConfirmAction = _ => { prompts.Confirms++; return Task.FromResult(prompts.ConfirmAnswer); };
        await vm.LoadPlaylistsAsync();
        return (vm, store, prompts);
    }

    private static async Task<List<string>> ReloadedRows(TestPersistenceService store)
    {
        var again = new SidebarViewModel(store, new FakeLibraryService());
        await again.LoadPlaylistsAsync();
        return again.SidebarRows.Select(r => r.Label).ToList();
    }

    [AvaloniaFact]
    public async Task NewPlaylist_Bottom_IsLast_AsBefore()
    {
        var (vm, store, _) = await StoredSidebar(new Playlist { Name = "Gym", Folder = "Rock" }, new Playlist { Name = "Road" });

        await vm.CreatePlaylistFromTracksAsync("Fresh", Array.Empty<Track>());

        Assert.Equal(new[] { "Rock", "Gym", "Road", "Fresh" }, vm.SidebarRows.Select(r => r.Label));
        Assert.Equal(new[] { "Rock", "Gym", "Road", "Fresh" }, await ReloadedRows(store));
    }

    [AvaloniaFact]
    public async Task NewPlaylist_Top_IsFirst_BelowThePinnedOnes_AndStaysThereAfterAReload()
    {
        var (vm, store, _) = await StoredSidebar(new Playlist { Name = "Pinned", IsPinned = true },
            new Playlist { Name = "Gym", Folder = "Rock" }, new Playlist { Name = "Road" });
        vm.NewPlaylistsAtTop = true;

        await vm.CreatePlaylistFromTracksAsync("Fresh", Array.Empty<Track>());
        await vm.CreatePlaylistFromTracksAsync("Newest", Array.Empty<Track>());

        var expected = new[] { "Pinned", "Newest", "Fresh", "Rock", "Gym", "Road" };
        Assert.Equal(expected, vm.SidebarRows.Select(r => r.Label));
        Assert.Equal(expected, await ReloadedRows(store));
    }

    [AvaloniaFact]
    public async Task NewFolder_Top_IsFirst_StaysThereAfterAReload_AndWhenFilled()
    {
        var road = new Playlist { Name = "Road" };
        var (vm, store, prompts) = await StoredSidebar(new Playlist { Name = "Gym", Folder = "Rock" }, road);
        vm.NewPlaylistsAtTop = true;
        prompts.Answers.Enqueue("Chill");

        await vm.NewFolderCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Chill", "Rock", "Gym", "Road" }, vm.SidebarRows.Select(r => r.Label));
        Assert.Equal(new[] { "Chill", "Rock", "Gym", "Road" }, await ReloadedRows(store));

        // Filled, it keeps its place (the playlist takes the folder's place, not the other way round).
        var item = vm.PlaylistItems.Single(i => i.PlaylistId == road.Id);
        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(item).Single(c => c.Folder == "Chill"));
        Assert.Equal(new[] { "Chill", "Road", "Rock", "Gym" }, vm.SidebarRows.Select(r => r.Label));
        Assert.Equal(new[] { "Chill", "Road", "Rock", "Gym" }, await ReloadedRows(store));
    }

    [AvaloniaFact]
    public async Task NewFolder_Bottom_KeepsTheOldPlace()
    {
        var (vm, _, prompts) = await StoredSidebar(new Playlist { Name = "Gym", Folder = "Rock" }, new Playlist { Name = "Road" });
        prompts.Answers.Enqueue("Zen");

        await vm.NewFolderCommand.ExecuteAsync(null);

        Assert.Equal(new[] { "Rock", "Gym", "Zen", "Road" }, vm.SidebarRows.Select(r => r.Label));
    }

    [AvaloniaFact]
    public async Task RemoveEmptyFolders_On_ABrandNewFolderStays_ButGoesWhenItsLastPlaylistLeaves()
    {
        var road = new Playlist { Name = "Road" };
        var (vm, store, prompts) = await StoredSidebar(road);
        vm.RemoveEmptyFolders = true;
        prompts.Answers.Enqueue("Chill");

        await vm.NewFolderCommand.ExecuteAsync(null);
        // Never held a playlist yet: it stays, so it can be filled.
        Assert.NotNull(FolderRow(vm, "Chill"));

        var item = vm.PlaylistItems.Single();
        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(item).Single(c => c.Folder == "Chill"));
        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(item).Single(c => c.Folder == null && !c.IsNew));

        Assert.Equal("", road.Folder);
        Assert.Null(FolderRow(vm, "Chill"));
        Assert.Empty(store.PlaylistFolders);
        Assert.DoesNotContain("Chill", vm.GetFolderNames());
    }

    [AvaloniaFact]
    public async Task RemoveEmptyFolders_On_DeletingTheLastPlaylist_RemovesTheFolder_OnlyThen()
    {
        var gym = new Playlist { Name = "Gym", Folder = "Rock" };
        var run = new Playlist { Name = "Run", Folder = "Rock" };
        var (vm, store, _) = await StoredSidebar(gym, run);
        vm.UseSavedFolders(new[] { "Rock" });
        vm.RemoveEmptyFolders = true;

        await vm.DeletePlaylistAsync(gym.Id);
        Assert.NotNull(FolderRow(vm, "Rock"));

        await vm.DeletePlaylistAsync(run.Id);
        Assert.Null(FolderRow(vm, "Rock"));
        Assert.Empty(store.PlaylistFolders);
    }

    [AvaloniaFact]
    public async Task RemoveEmptyFolders_Off_ASavedFolderStaysEmpty_AtItsPlace()
    {
        var gym = new Playlist { Name = "Gym" };
        var (vm, store, prompts) = await StoredSidebar(new Playlist { Name = "Road" }, gym);
        vm.NewPlaylistsAtTop = true;
        prompts.Answers.Enqueue("Chill");
        await vm.NewFolderCommand.ExecuteAsync(null);
        var item = vm.PlaylistItems.Single(i => i.PlaylistId == gym.Id);
        await vm.MoveToFolderChoiceCommand.ExecuteAsync(vm.FolderChoicesFor(item).Single(c => c.Folder == "Chill"));

        await vm.DeletePlaylistAsync(gym.Id);

        Assert.Equal(new[] { "Chill", "Road" }, vm.SidebarRows.Select(r => r.Label));
        Assert.Equal(new[] { "Chill" }, store.PlaylistFolders);
        Assert.Equal(new[] { "Chill", "Road" }, await ReloadedRows(store));
    }

    // ── Persistence ──

    [Fact]
    public async Task PersistenceService_KeepsTheEmptyFolders_InTheirOwnFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            IPersistenceService p = new PersistenceService(dir);
            Assert.Empty(await p.LoadPlaylistFoldersAsync());
            await p.SavePlaylistFoldersAsync(new List<string> { "Chill", "Zen" });
            Assert.True(File.Exists(Path.Combine(dir, "playlist_folders.json")));
            Assert.Equal(new[] { "Chill", "Zen" }, await new PersistenceService(dir).LoadPlaylistFoldersAsync());
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
