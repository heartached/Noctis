using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class SidebarRowBuilderTests
{
    private static PlaylistNavItem Item(string name, bool pinned = false, string folder = "")
        => new() { Key = $"playlist:{name}", Label = name, PlaylistId = Guid.NewGuid(), IsPinned = pinned, Folder = folder };

    private static HashSet<string> NoCollapsed => new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void PinnedPlaylistsComeFirst_EvenWhenInFolder()
    {
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("Loose"),
            Item("Pinned Late", pinned: true, folder: "Metal"),
            Item("In Folder", folder: "Metal"),
        }, NoCollapsed);

        Assert.Equal("Pinned Late", rows[0].Label);
        Assert.False(rows[0].IsInFolder);
    }

    [Fact]
    public void FoldersAreAlphabetical_WithIndentedChildren()
    {
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("Zeta", folder: "Rock"),
            Item("Alpha", folder: "Ambient"),
            Item("Loose"),
        }, NoCollapsed);

        Assert.True(rows[0].IsFolder);
        Assert.Equal("Ambient", rows[0].Label);
        Assert.Equal("Alpha", rows[1].Label);
        Assert.True(rows[1].IsInFolder);
        Assert.True(rows[2].IsFolder);
        Assert.Equal("Rock", rows[2].Label);
        Assert.Equal("Zeta", rows[3].Label);
        // Loose playlists come after folders, unindented.
        Assert.Equal("Loose", rows[4].Label);
        Assert.False(rows[4].IsInFolder);
    }

    [Fact]
    public void CollapsedFolderHidesChildren_ButKeepsHeaderCount()
    {
        var collapsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "metal" };
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("A", folder: "Metal"),
            Item("B", folder: "Metal"),
        }, collapsed);

        Assert.Single(rows);
        Assert.True(rows[0].IsFolder);
        Assert.False(rows[0].IsExpanded);
        Assert.Equal(2, rows[0].TrackCount);
    }

    [Fact]
    public void FolderGroupingIsCaseInsensitive()
    {
        var rows = SidebarViewModel.BuildRows(new[]
        {
            Item("A", folder: "metal"),
            Item("B", folder: "Metal"),
        }, NoCollapsed);

        Assert.Equal(3, rows.Count); // one folder header + two children
        Assert.True(rows[0].IsFolder);
    }
}
