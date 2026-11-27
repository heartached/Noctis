using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-27 (Halil): the expanded sidebar's width drags within
/// <see cref="SidebarWidthMetrics"/>'s range. Its rows used to be horizontal StackPanels, which
/// measure their text at infinite width: a long nav label ran past the rail's edge and the
/// rounded clip cut it mid-letter, while playlist / folder / plugin names were capped at a fixed
/// 120 / 130 px whatever the width. Now every label takes the room left in its row and ends in
/// an ellipsis, at each width in the range.
/// </summary>
public class SidebarWidthLayoutTests
{
    private const string LongName = "An Extremely Long Playlist Name That Never Fits Anywhere";
    private const string LongFolder = "A Folder Whose Name Is Much Too Long For Any Sidebar";

    private sealed class Persistence : TestPersistenceService
    {
        public override Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>
        {
            new() { Id = Guid.NewGuid(), Name = LongName, IsPinned = true },
            new() { Id = Guid.NewGuid(), Name = "In folder " + LongName, Folder = LongFolder },
            new() { Id = Guid.NewGuid(), Name = "Short" },
        });
    }

    private static void Pump()
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static async Task<(SidebarView View, Window Win, SidebarViewModel Vm)> ShowAsync(double width)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var vm = new SidebarViewModel(new Persistence(), new FakeLibraryService()) { IsExpanded = true };
        await vm.LoadPlaylistsAsync();
        // A nav label far longer than any language's (the widest real ones trim at the minimum).
        vm.NavItems[0].Label = "Wiedergabelistenverwaltungsübersicht";
        vm.NavItems[0].Count = 1234; // the Inbox count, right of the label
        var view = new SidebarView { DataContext = vm };
        var win = new Window { Width = width, Height = 1400, Content = view };
        win.Show();
        Pump();
        return (view, win, vm);
    }

    private static List<TextBlock> ShownTexts(SidebarView view) =>
        view.GetVisualDescendants().OfType<TextBlock>()
            .Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text) && t.Bounds.Width > 0
                        && t.FindAncestorOfType<TextBox>() == null)
            .ToList();

    [AvaloniaTheory]
    [InlineData(SidebarWidthMetrics.Min)]
    [InlineData(SidebarWidthMetrics.Default)]
    [InlineData(270.0)]
    [InlineData(SidebarWidthMetrics.Max)]
    public async Task EveryLabel_StaysInsideTheSidebar_AtEveryWidth(double width)
    {
        var (view, win, _) = await ShowAsync(width);
        Assert.Equal(width, view.Bounds.Width);
        var texts = ShownTexts(view);
        Assert.Contains(texts, t => t.Text == LongName);
        Assert.Contains(texts, t => t.Text == LongFolder);
        Assert.Contains(texts, t => t.Text == "Wiedergabelistenverwaltungsübersicht");
        foreach (var t in texts)
        {
            var right = t.TranslatePoint(new Point(t.Bounds.Width, 0), view)!.Value.X;
            // 6 px list margin + the row pill's padding: text never reaches the rail's edge.
            Assert.True(right <= width - 6 + 0.5, $"'{t.Text}' ends at {right:0.#} in a {width} px sidebar");
        }

        // The pinned star stays beside its (trimmed) name, inside the row too.
        var star = view.GetVisualDescendants().OfType<PathIcon>()
            .Single(p => p.IsEffectivelyVisible && p.Width == 11);
        Assert.True(star.TranslatePoint(new Point(star.Bounds.Width, 0), view)!.Value.X <= width - 6 + 0.5);
        win.Close();
    }

    [AvaloniaFact]
    public async Task AWiderSidebar_ShowsMoreOfALongName()
    {
        var (narrowView, narrowWin, _) = await ShowAsync(SidebarWidthMetrics.Min);
        var narrow = ShownTexts(narrowView).Single(t => t.Text == LongName).Bounds.Width;
        narrowWin.Close();
        var (wideView, wideWin, _) = await ShowAsync(SidebarWidthMetrics.Max);
        var wide = ShownTexts(wideView).Single(t => t.Text == LongName).Bounds.Width;
        wideWin.Close();
        // The old fixed MaxWidth (120) held every width to the same truncation.
        Assert.True(wide > 120 + 60, $"wide {wide:0.#}");
        Assert.True(wide - narrow >= SidebarWidthMetrics.Max - SidebarWidthMetrics.Min - 40,
            $"narrow {narrow:0.#}, wide {wide:0.#}");
    }

    [AvaloniaFact]
    public async Task TheRail_StillCentresItsIcons()
    {
        var (view, win, vm) = await ShowAsync(SidebarEdgePeek.RailWidth);
        vm.IsExpanded = false;
        Pump();
        var nav = view.FindControl<ListBox>("NavList")!;
        var row = (ListBoxItem)nav.ContainerFromIndex(0)!;
        var content = (Control)row.GetVisualDescendants().OfType<DockPanel>().First();
        var centre = content.TranslatePoint(new Point(content.Bounds.Width / 2, 0), view)!.Value.X;
        Assert.Equal(28, content.Bounds.Width, 1); // the icon only
        Assert.Equal(SidebarEdgePeek.RailWidth / 2, centre, 1);
        win.Close();
    }
}
