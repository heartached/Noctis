using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.Views;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Scrolling the phone's Songs list (tester 11-05: freezes three times a second). A row that leaves
/// the screen is reused for the one that comes in: not rebuilt (Avalonia's ContentPresenter
/// containers dropped their row on every recycle), and not re-styled (a row that leaves the logical
/// tree loses every style). See RowItemsControl.
/// </summary>
public class MobileSongListScrollTests
{
    private readonly ITestOutputHelper _out;
    public MobileSongListScrollTests(ITestOutputHelper output) => _out = output;

    [AvaloniaFact]
    public void ScrollingTheSongList_ReusesRows_KeepsThemStyled_AndShowsTheRightSongs()
    {
        var songs = Enumerable.Range(0, 600).Select(i => MobileFixtures.Song($"Song {i:D3}")).ToArray();
        using var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenSongsCommand.Execute(null);
        MobileFixtures.Land(window);
        var scroll = MobileFixtures.Named<ScrollViewer>(view, "SongScroll");
        var list = MobileFixtures.Named<ItemsControl>(view, "SongList");

        var rows = new HashSet<SongRow>(ReferenceEqualityComparer.Instance);
        var maxRealized = 0;
        var watch = System.Diagnostics.Stopwatch.StartNew();
        for (var y = 0; y <= 20000; y += 45)
        {
            scroll.Offset = new Vector(0, y);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            var realized = list.GetRealizedContainers().ToList();
            maxRealized = System.Math.Max(maxRealized, realized.Count);
            foreach (var container in realized)
                foreach (var row in container.GetVisualDescendants().OfType<SongRow>()) rows.Add(row);
        }
        _out.WriteLine($"offset {scroll.Offset.Y}, realized at most {maxRealized}, rows built {rows.Count}, {watch.ElapsedMilliseconds} ms");
        Assert.True(scroll.Offset.Y > 15000);
        // Every row ever shown was one of the few realized at once (a handful of spares at most);
        // before, each of the ~330 songs scrolled past got a row of its own.
        Assert.InRange(rows.Count, 1, maxRealized + 4);

        foreach (var container in list.GetRealizedContainers())
        {
            var index = list.IndexFromContainer(container);
            var button = container.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row"));
            // Reused rows show the song at their place, with its command parameter...
            Assert.Same(songs[index], button.DataContext);
            Assert.Same(songs[index], button.CommandParameter);
            var title = container.GetVisualDescendants().OfType<TextBlock>().First(t => t.Name == "RowTitle");
            Assert.Equal(songs[index].Title, title.Text);
            // ...and stay in the logical tree under the list, styled: the theme's Button template
            // is applied (a re-styled row would have gone through the theme again on every recycle).
            Assert.Same(list, ((ILogical)button).LogicalParent);
            Assert.True(((ILogical)button).IsAttachedToLogicalTree);
            Assert.NotNull(button.Template);
        }
        window.Close();
    }
}
