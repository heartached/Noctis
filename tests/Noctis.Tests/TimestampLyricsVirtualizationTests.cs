using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// Clicking the metadata editor's "Timestamp Lyrics" tab froze in proportion to
/// the lyric count: the tab materializes lazily on first selection, and its row
/// list was a plain ItemsControl wrapped in a StackPanel inside the ScrollViewer
/// — a shape a virtualizing panel cannot own the viewport in (see the
/// AlbumDetailView track-list comment), so every line (a pill TextBox plus three
/// nudge buttons each) inflated synchronously on the click. 48 lines was a
/// visible hitch; hundreds are worse. The list must virtualize: only rows in and
/// near the viewport may be realized.
/// </summary>
public class TimestampLyricsVirtualizationTests
{
    private readonly ITestOutputHelper _output;

    public TimestampLyricsVirtualizationTests(ITestOutputHelper output) => _output = output;

    private const int LineCount = 200;

    private static string BuildLrc()
    {
        var sb = new StringBuilder();
        for (var i = 0; i < LineCount; i++)
            sb.AppendLine($"[{i / 60:00}:{i % 60:00}.00]Line {i} of the song");
        return sb.ToString();
    }

    [AvaloniaFact]
    public void TimestampTab_RealizesOnlyViewportRows()
    {
        var track = new Track
        {
            Title = "T",
            Artist = "A",
            Album = "X",
            FilePath = Path.Combine(Path.GetTempPath(), "NoctisTests", "virt-probe.flac"),
            SyncedLyrics = BuildLrc(),
        };
        using var persistence = new TestPersistenceService();
        var vm = new MetadataViewModel(track, new FakeMetadataService(),
            new FakeLibraryService { TrackList = { track } }, persistence,
            new FakeAnimatedCoverService(), albumScoped: false, albumTracks: null);

        // Precondition: the load path parsed the LRC into editor lines.
        Assert.Equal(LineCount, vm.SyncedLyricLines.Count);

        var window = new MetadataWindow { DataContext = vm, Width = 1100, Height = 760 };
        window.Show();

        var tabs = window.GetVisualDescendants().OfType<TabControl>().First();
        var timestampTab = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, "Timestamp Lyrics"));
        tabs.SelectedItem = timestampTab;
        window.UpdateLayout();

        var realized = window.GetVisualDescendants()
            .OfType<Border>()
            .Count(b => b.Classes.Contains("synced-row"));
        _output.WriteLine($"realized rows: {realized} of {LineCount}");

        Assert.True(realized > 0, "no rows realized — the tab content never materialized");
        Assert.True(realized < 60,
            $"{realized} of {LineCount} rows are realized — the Timestamp Lyrics list is not virtualizing");
    }

    /// <summary>Write-free stand-in; the dialog is never saved in these tests.</summary>
    private sealed class FakeMetadataService : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt)
        {
            embeddedArt = null;
            return null;
        }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => true;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => true;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => true;
        bool IMetadataService.WriteAdvancedFields(string filePath,
            Noctis.Services.AdvancedTagIO.AdvancedFields fields,
            Noctis.Services.AdvancedTagIO.AdvancedFields original) => true;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => true;
    }
}
