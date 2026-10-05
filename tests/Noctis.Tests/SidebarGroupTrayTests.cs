using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// An open sidebar folder draws one tray behind its header and playlists (Discord Luwi
/// 10-04: in the collapsed rail a folder's playlists looked like loose ones). Each row's
/// slice comes from its GroupPosition, set where the rows are built.
/// </summary>
public class SidebarGroupTrayTests
{
    private static PlaylistNavItem Item(string name, bool pinned = false, string folder = "")
        => new() { Key = $"playlist:{name}", Label = name, PlaylistId = Guid.NewGuid(), IsPinned = pinned, Folder = folder };

    private static HashSet<string> NoCollapsed => new(StringComparer.OrdinalIgnoreCase);

    private static SidebarGroupPosition[] Positions(IEnumerable<PlaylistNavItem> rows)
        => rows.Select(r => r.GroupPosition).ToArray();

    [Fact]
    public void OpenFolder_HeaderMembersAndLast()
    {
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("A", folder: "Metal"),
            Item("B", folder: "Metal"),
            Item("C", folder: "Metal"),
            Item("Loose"),
        }, NoCollapsed);

        Assert.Equal(new[]
        {
            SidebarGroupPosition.Header,
            SidebarGroupPosition.Member,
            SidebarGroupPosition.Member,
            SidebarGroupPosition.Last,
            SidebarGroupPosition.None,
        }, Positions(rows));
    }

    [Fact]
    public void SinglePlaylistFolder_ItsPlaylistIsLast()
    {
        var rows = SidebarViewModel.BuildRows(new[] { Item("A", folder: "Metal") }, NoCollapsed);

        Assert.Equal(new[] { SidebarGroupPosition.Header, SidebarGroupPosition.Last }, Positions(rows));
    }

    [Fact]
    public void FoldedFolder_ShowsNoTray_AndItsPlaylistsAreCleared()
    {
        var a = Item("A", folder: "Metal");
        var b = Item("B", folder: "Metal");
        SidebarViewModel.BuildRows(new[] { a, b }, NoCollapsed);
        Assert.Equal(SidebarGroupPosition.Last, b.GroupPosition);

        var collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metal" };
        var rows = SidebarViewModel.BuildRows(new[] { a, b }, collapsed);

        Assert.Single(rows);
        Assert.Equal(SidebarGroupPosition.None, rows[0].GroupPosition);
        Assert.Equal(SidebarGroupPosition.None, a.GroupPosition);
        Assert.Equal(SidebarGroupPosition.None, b.GroupPosition);
    }

    [Fact]
    public void PinnedAndLoosePlaylists_AreNeverInATray()
    {
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("Loose 1"),
            Item("Pinned", pinned: true, folder: "Metal"),
            Item("In Folder", folder: "Metal"),
            Item("Loose 2"),
        }, NoCollapsed);

        foreach (var row in rows.Where(r => !r.IsFolder && !r.IsInFolder))
            Assert.Equal(SidebarGroupPosition.None, row.GroupPosition);
        Assert.Equal(SidebarGroupPosition.None, rows.Single(r => r.Label == "Pinned").GroupPosition);
    }

    [Fact]
    public void TwoFolders_EachGetTheirOwnTray()
    {
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("A1", folder: "Alpha"),
            Item("A2", folder: "Alpha"),
            Item("B1", folder: "Beta"),
        }, NoCollapsed);

        Assert.Equal(new[]
        {
            SidebarGroupPosition.Header,
            SidebarGroupPosition.Member,
            SidebarGroupPosition.Last,
            SidebarGroupPosition.Header,
            SidebarGroupPosition.Last,
        }, Positions(rows));
    }

    // ── View model + view ──

    private sealed class PlaylistPersistence : TestPersistenceService
    {
        public List<Playlist> Initial { get; } = new()
        {
            new Playlist { Id = Guid.NewGuid(), Name = "A1", Folder = "Alpha" },
            new Playlist { Id = Guid.NewGuid(), Name = "A2", Folder = "Alpha" },
            new Playlist { Id = Guid.NewGuid(), Name = "Loose" },
        };
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(Initial.ToList());
        public override Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
    }

    private static PlaylistNavItem Row(SidebarViewModel vm, string label) => vm.SidebarRows.Single(r => r.Label == label);

    [Fact]
    public async Task Reorder_MovesTheLastPlaylistAndRepaintsTheTray()
    {
        var vm = new SidebarViewModel(new PlaylistPersistence(), new FakeLibraryService());
        await vm.LoadPlaylistsAsync();
        Assert.Equal(SidebarGroupPosition.Member, Row(vm, "A1").GroupPosition);
        Assert.Equal(SidebarGroupPosition.Last, Row(vm, "A2").GroupPosition);

        // A2 above A1: A1 now closes the folder.
        await vm.MovePlaylistAsync(Row(vm, "A2").PlaylistId!.Value, Row(vm, "A1"), placeAfter: false);
        Assert.Equal(SidebarGroupPosition.Member, Row(vm, "A2").GroupPosition);
        Assert.Equal(SidebarGroupPosition.Last, Row(vm, "A1").GroupPosition);

        // Loose playlist dropped after A1 joins the folder as its last playlist.
        await vm.MovePlaylistAsync(Row(vm, "Loose").PlaylistId!.Value, Row(vm, "A1"), placeAfter: true);
        Assert.Equal(SidebarGroupPosition.Member, Row(vm, "A1").GroupPosition);
        Assert.Equal(SidebarGroupPosition.Last, Row(vm, "Loose").GroupPosition);

        // And dragged out beside the folder it leaves the tray.
        var header = vm.SidebarRows.Single(r => r.IsFolder);
        await vm.MovePlaylistNextToFolderAsync(Row(vm, "Loose").PlaylistId!.Value, header, placeAfter: true);
        Assert.Equal(SidebarGroupPosition.None, Row(vm, "Loose").GroupPosition);
        Assert.Equal(SidebarGroupPosition.Last, Row(vm, "A1").GroupPosition);
    }

    [Fact]
    public async Task FoldToggle_KeepsTheLiveHeader_AndUpdatesItsPosition()
    {
        var vm = new SidebarViewModel(new PlaylistPersistence(), new FakeLibraryService());
        await vm.LoadPlaylistsAsync();
        var header = vm.SidebarRows.Single(r => r.IsFolder);
        Assert.Equal(SidebarGroupPosition.Header, header.GroupPosition);

        vm.ToggleFolderExpansion("Alpha");
        Assert.Same(header, vm.SidebarRows.Single(r => r.IsFolder));
        Assert.Equal(SidebarGroupPosition.None, header.GroupPosition);

        vm.ToggleFolderExpansion("Alpha");
        Assert.Equal(SidebarGroupPosition.Header, header.GroupPosition);
        Assert.Equal(SidebarGroupPosition.Last, Row(vm, "A2").GroupPosition);
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static Border Tray(ListBox list, int index)
        => ((Control)list.ContainerFromIndex(index)!).GetVisualDescendants().OfType<Border>()
            .Single(b => b.Classes.Contains("group-tray"));

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MountedRows_CarryTheirTraySlice(bool expandedRail)
    {
        EnsureAppStyles();
        var vm = new SidebarViewModel(new PlaylistPersistence(), new FakeLibraryService()) { IsExpanded = expandedRail };
        await vm.LoadPlaylistsAsync();
        var view = new SidebarView { DataContext = vm };
        var win = new Window { Width = 260, Height = 1400, Content = view };
        win.Show();
        // Past the fold timing, so the trays' fade has settled.
        Pump(30);
        var list = view.FindControl<ListBox>("PlaylistList")!;

        // Rows: Alpha header, A1, A2, Loose.
        var header = Tray(list, 0);
        var a1 = Tray(list, 1);
        var a2 = Tray(list, 2);
        var loose = Tray(list, 3);
        Assert.True(header.Classes.Contains("header") && header.Classes.Contains("open"));
        Assert.True(a1.Classes.Contains("member"));
        Assert.True(a2.Classes.Contains("last"));
        Assert.False(loose.Classes.Contains("header") || loose.Classes.Contains("member") || loose.Classes.Contains("last"));

        Assert.True(header.Opacity > 0);
        Assert.True(a1.Opacity > 0);
        Assert.True(a2.Opacity > 0);
        Assert.Equal(0, loose.Opacity);
        // The slices span the row pill and meet across the 4px gap between rows.
        var row1 = (Control)list.ContainerFromIndex(1)!;
        Assert.Equal(row1.Bounds.Width, a1.Bounds.Width, 1);
        Assert.Equal(row1.Bounds.Height + 4, a1.Bounds.Height, 1);
        Assert.Equal(new CornerRadius(24, 24, 0, 0), header.CornerRadius);
        Assert.Equal(new CornerRadius(0, 0, 24, 24), a2.CornerRadius);
        win.Close();
    }
}
