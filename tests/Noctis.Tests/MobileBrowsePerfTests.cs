using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone testers (11-22), "the app is still laggy": what Home, the album and artist pages and the
/// covers cost on a big library, measured headless (desktop CPU; a mid-range phone core is several
/// times slower). Each test holds one of the fixes to its number.
/// </summary>
public class MobileBrowsePerfTests
{
    private readonly ITestOutputHelper _out;
    public MobileBrowsePerfTests(ITestOutputHelper output) => _out = output;

    /// <summary>Every Home rail full (twelve tiles each), as on a phone with a long play log.</summary>
    private static void FillHomeRails(MobileFixtures.Rig rig)
    {
        var lib = rig.Shell.Library;
        var albums = rig.Library.Albums.Select(RailItem.ForAlbum).ToList();
        var songs = rig.Library.TrackList.Select(RailItem.ForTrack).ToList();
        var artists = rig.Library.ArtistList.Select(a => RailItem.ForArtist(a, null)).ToList();
        lib.PinnedRail.ReplaceAll(albums.Skip(0).Take(12));
        lib.RecentlyPlayedRail.ReplaceAll(albums.Skip(12).Take(12));
        lib.OnRepeatRail.ReplaceAll(songs.Skip(0).Take(12));
        lib.MostPlayedRail.ReplaceAll(songs.Skip(12).Take(12));
        lib.TopArtistsRail.ReplaceAll(artists.Take(12));
        lib.DownloadedRail.ReplaceAll(albums.Skip(24).Take(12));
        lib.HasPinned = lib.HasShelf = lib.HasOnRepeat = lib.HasMostPlayed = lib.HasTopArtists = lib.HasDownloaded = true;
    }

    private static List<RailTile> Tiles(Visual root) =>
        root.GetVisualDescendants().OfType<RailTile>().Where(t => t.IsVisible).ToList();

    /// <summary>
    /// Home, the start tab: each rail was a plain horizontal panel, so every tile of every rail
    /// (up to 84) was built, styled and loaded its cover as the app came up, though a screen shows
    /// three and a peek of each of the first few rails. Rails now virtualise sideways; a new pin
    /// still glides in.
    /// </summary>
    [AvaloniaFact]
    public void Home_BuildsOnlyTheRailTilesOnScreen_AndANewPinStillGlidesIn()
    {
        using (var warm = MobileSearchPerfTests.BigLibrary(1_200, 100))   // warm-up: the JIT, the styles
        {
            FillHomeRails(warm);
            MobileFixtures.Mount(warm.Shell, out _).Close();
        }
        using var rig = MobileSearchPerfTests.BigLibrary(4_800, 300);
        FillHomeRails(rig);
        var start = Stopwatch.GetTimestamp();
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        Dispatcher.UIThread.RunJobs();
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var home = MobileFixtures.Find<HomePage>(view);
        var tiles = Tiles(home);
        _out.WriteLine($"home, 6 rails x 12: {tiles.Count} tiles built, {ms:F0} ms to the first frame");
        // Before: 72 tiles (every item of every rail), 679 ms; after: 14, 191 ms. The first three rails fill the screen.
        Assert.InRange(tiles.Count, 6, 40);

        // The tiles on screen as Home comes up glide in (Appear), as before.
        Assert.All(tiles, t => Assert.NotNull(((Control)t.Content!).Transitions));

        // Every rail keeps its height as Home scrolls down to it (a rail off screen still measures
        // a tile), and shows its tiles there.
        string[] rails = { "PinnedRail", "RecentlyPlayedRail", "OnRepeatRail", "MostPlayedRail", "TopArtistsRail", "DownloadedRail" };
        var heights = rails.Select(r => MobileFixtures.Named<ItemsControl>(home, r).Bounds.Height).ToList();
        Assert.All(heights, h => Assert.True(h > 150, $"rail {h} dp tall"));
        var homeScroll = MobileFixtures.Named<ScrollViewer>(home, "HomeScroll");
        homeScroll.Offset = new Avalonia.Vector(0, homeScroll.Extent.Height);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(heights, rails.Select(r => MobileFixtures.Named<ItemsControl>(home, r).Bounds.Height));
        Assert.InRange(Tiles(MobileFixtures.Named<ItemsControl>(home, "DownloadedRail")).Count, 3, 12);
        homeScroll.Offset = default;
        window.UpdateLayout();

        // A rail scrolled sideways shows its later tiles, in order, each opening its own.
        var rail = MobileFixtures.Named<ItemsControl>(home, "RecentlyPlayedRail");
        var railScroll = rail.FindAncestorOfType<ScrollViewer>()!;
        var railTiles = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var scrollReplays = 0;
        void Watch()
        {
            foreach (var t in Tiles(rail))
                if (railTiles.Add((Control)t.Content!))
                    ((Control)t.Content!).PropertyChanged += (_, e) =>
                    {
                        if (e.Property == Avalonia.Animation.Animatable.TransitionsProperty && e.NewValue == null) scrollReplays++;
                    };
        }
        Watch();
        for (var x = 0.0; x <= railScroll.Extent.Width; x += 60)
        {
            railScroll.Offset = new Avalonia.Vector(x, 0);
            window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
            Watch();
        }
        Assert.Equal(0, scrollReplays);   // a tile scrolled on had arrived already
        Assert.InRange(railTiles.Count, 3, 8);
        var last = rail.GetRealizedContainers().OrderBy(rail.IndexFromContainer).Last();
        var button = last.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row"));
        Assert.Same(rig.Shell.Library.RecentlyPlayedRail[^1], button.CommandParameter);
        Assert.Same(rig.Shell.OpenRailItemCommand, button.Command);

        // Pinning an album glides the Pinned rail's tiles in again (the rail is handed a new list),
        // as the plain panel's rebuilt tiles did.
        var pinned = MobileFixtures.Named<ItemsControl>(home, "PinnedRail");
        var replayed = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        var seen = new HashSet<Control>(ReferenceEqualityComparer.Instance);
        foreach (var t in Tiles(pinned))
        {
            var content = (Control)t.Content!;
            content.Transitions = new Avalonia.Animation.Transitions();   // its arrival long finished
            seen.Add(content);
            content.PropertyChanged += (_, e) =>
            {
                if (e.Property == Avalonia.Animation.Animatable.TransitionsProperty && e.NewValue == null) replayed.Add(content);
            };
        }
        var next = rig.Shell.Library.PinnedRail.ToList();
        next.Insert(0, RailItem.ForAlbum(rig.Library.Albums[100]));
        rig.Shell.Library.PinnedRail.ReplaceAll(next);
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        var first = pinned.GetRealizedContainers().OrderBy(pinned.IndexFromContainer).First();
        Assert.Same(next[0], first.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("row")).CommandParameter);
        var pinnedTiles = Tiles(pinned).Select(t => (Control)t.Content!).ToList();
        var glided = pinnedTiles.Count(c => seen.Contains(c) ? replayed.Contains(c) : c.Transitions != null);
        _out.WriteLine($"new pin: {glided} of {pinnedTiles.Count} pinned tiles glided in");
        Assert.Equal(pinnedTiles.Count, glided);
        window.Close();
    }
}
