using Avalonia;
using Avalonia.Controls;
using AdornerLayer = Avalonia.Controls.Primitives.AdornerLayer;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The playing song's title in the island opens its album (11-23 feedback): the page opened
/// at its top, the song out of sight on a long album. It now glides the song's row into view
/// and washes it once.
/// </summary>
public class AlbumRevealTrackTests
{
    [Theory]
    [InlineData(100, 40, 0, 600, 3000, 82, 0)]       // already in view: no scroll
    [InlineData(2000, 40, 0, 600, 3000, 82, 1786.9)] // far down: a little above the middle
    [InlineData(2950, 40, 0, 600, 3000, 82, 2400)]   // near the end: clamped to the range
    [InlineData(560, 40, 0, 600, 3000, 82, 346.9)]   // under the floating player: scrolls
    public void TargetOffset_centres_rows_out_of_view_only(double top, double height, double current,
        double viewport, double extent, double inset, double expected)
    {
        Assert.Equal(expected, RowReveal.TargetOffset(top, height, current, viewport, extent, inset), 1);
    }

    private static Track Song(int n, Guid albumId) => new()
    {
        Id = Guid.NewGuid(), Title = $"Song {n}", Artist = "Bonobo", AlbumArtist = "Bonobo", Album = "Migration",
        AlbumId = albumId, TrackNumber = n, Duration = TimeSpan.FromMinutes(3),
        FilePath = TestPaths.Primary("reveal", $"{n}.flac"),
    };

    [AvaloniaFact]
    public async Task Album_page_glides_to_the_song_it_was_opened_for_and_washes_it()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var albumId = Guid.NewGuid();
        var tracks = Enumerable.Range(1, 40).Select(n => Song(n, albumId)).ToList();
        var album = new Album
        {
            Id = albumId, Name = "Migration", Artist = "Bonobo", TrackCount = tracks.Count,
            TotalDuration = TimeSpan.FromMinutes(120), Tracks = tracks,
        };
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new AlbumDetailViewModel(album, player, persistence, lib, new SidebarViewModel(persistence, lib), null!)
        {
            RevealTrackId = tracks[34].Id,
        };

        var view = new AlbumDetailView { DataContext = vm };
        var window = new Window { Width = 1200, Height = 700, Content = view };
        window.Show();
        try
        {
            var sv = view.FindControl<ScrollViewer>("TrackScrollViewer")!;
            ListBoxItem? Row() => view.GetVisualDescendants().OfType<ListBoxItem>()
                .FirstOrDefault(i => ReferenceEquals(i.DataContext, tracks[34]));
            Border? Body() => Row()?.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("row-body"));

            var deadline = Environment.TickCount64 + 5000;
            while (Environment.TickCount64 < deadline && (Body() is not { } b || AdornerLayer.GetAdorner(b) == null))
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                await Task.Delay(5);
            }

            Assert.Null(vm.RevealTrackId); // taken: Back to this page restores its scroll instead
            Assert.True(sv.Offset.Y > 0, "the page never scrolled");
            var row = Row()!;
            var top = row.TranslatePoint(default, sv)!.Value.Y;
            Assert.InRange(top, 0, sv.Viewport.Height - row.Bounds.Height);
            Assert.NotNull(AdornerLayer.GetAdorner(Body()!));
        }
        finally
        {
            window.Close();
        }
    }
}
