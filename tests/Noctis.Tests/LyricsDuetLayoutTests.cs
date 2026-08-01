using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Duet rendering (ELRC "v1:"/"v2:"/"v3:" voice markers) on the lyrics page:
/// voice-2 line blocks anchor right, group lines center, word rows follow the
/// same edge, and duet files pin the lyric column to full MaxWidth so the right
/// edge is stable. Files without markers must keep today's layout exactly.
/// </summary>
public class LyricsDuetLayoutTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private static LyricsViewModel MakeViewModel()
    {
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        return new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
    }

    private static List<Button> LineButtons(LyricsView view) =>
        view.GetVisualDescendants().OfType<Button>()
            .Where(b => b.Classes.Contains("lyric-line-btn")).ToList();

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void DuetVoices_AlignLineBlocksPerVoice()
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(
            "[00:01.00]Left line\n" +
            "[00:02.00]v2: <00:02.00>Right <00:02.50>side\n" +
            "[00:03.00]v3: Both of us"));

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800 };
        try
        {
            win.Show();
            win.Content = view;
            Pump();

            var buttons = LineButtons(view);
            Assert.Equal(3, buttons.Count);
            Assert.Equal(HorizontalAlignment.Left, buttons[0].HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Right, buttons[1].HorizontalAlignment);
            Assert.Equal(HorizontalAlignment.Center, buttons[2].HorizontalAlignment);

            // Karaoke word rows follow the line's edge (binding must reach the
            // WrapPanel inside the ItemsPanelTemplate).
            var v2Row = buttons[1].GetVisualDescendants().OfType<WrapPanel>().First();
            Assert.Equal(WrapPanelItemsAlignment.End, v2Row.ItemsAlignment);

            // Duet files pin the column to full MaxWidth for a stable right edge,
            // and the v2 block actually sits against it.
            Assert.True(vm.HasDuetLines);
            var column = view.FindControl<ItemsControl>("LyricsItemsControl")!;
            Assert.Equal(column.MaxWidth, column.MinWidth);
            Assert.Equal(column.Bounds.Width, buttons[1].Bounds.Right, 1);
        }
        finally
        {
            win.Close();
        }
    }

    [AvaloniaFact]
    public void NoVoiceMarkers_KeepsDefaultLayout()
    {
        var vm = MakeViewModel();
        vm.LyricLines.ReplaceAll(LyricsViewModel.ParseLrcContent(
            "[00:01.00]First line\n[00:02.00]Second line"));

        var view = new LyricsView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 800 };
        try
        {
            win.Show();
            win.Content = view;
            Pump();

            Assert.False(vm.HasDuetLines);
            var column = view.FindControl<ItemsControl>("LyricsItemsControl")!;
            Assert.Equal(0, column.MinWidth);
            Assert.All(LineButtons(view), b =>
                Assert.Equal(HorizontalAlignment.Left, b.HorizontalAlignment));
        }
        finally
        {
            win.Close();
        }
    }
}
