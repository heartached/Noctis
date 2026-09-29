using System.Collections;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The selection-sized tool dialogs (Convert, Fetch Lyrics, Find Metadata, Find Duplicates,
/// Send to Folder) list one row per track. Their lists sat on a plain StackPanel, so a Ctrl+A
/// selection built every row on the UI thread the moment the dialog opened, the stall the
/// ReplayGain scanner's list was already virtualized against. Only rows in and near the
/// viewport may be realized. Organize Files lists every local library track the same way.
/// </summary>
public class ToolDialogVirtualizationTests
{
    private readonly ITestOutputHelper _output;

    public ToolDialogVirtualizationTests(ITestOutputHelper output) => _output = output;

    private const int RowCount = 400;

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("CheckmarkIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
    }

    private static Track[] Tracks() => Enumerable.Range(0, RowCount)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(),
            Title = $"Song {i}",
            Artist = "Artist",
            Album = "Album",
            FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
        })
        .ToArray();

    private int RealizedRows(Window window, IEnumerable items)
    {
        window.Width = 1000;
        window.Height = 800;
        window.Show();
        try
        {
            window.UpdateLayout();
            var list = window.GetVisualDescendants().OfType<ItemsControl>()
                .First(ic => ReferenceEquals(ic.ItemsSource, items));
            var realized = list.GetRealizedContainers().Count();
            _output.WriteLine($"realized rows: {realized} of {RowCount}");
            return realized;
        }
        finally { window.Close(); }
    }

    private static void AssertVirtualized(int realized)
    {
        Assert.True(realized > 0, "no rows realized: the list never materialized");
        Assert.True(realized < 60, $"{realized} of {RowCount} rows are realized: the list is not virtualizing");
    }

    [AvaloniaFact]
    public void AudioConverter_RealizesOnlyViewportRows()
    {
        EnsureAppResources();
        var vm = new AudioConverterViewModel(Tracks(), new StubConverter(), new FakeLibraryService());
        AssertVirtualized(RealizedRows(new AudioConverterDialog(vm), vm.Jobs));
    }

    [AvaloniaFact]
    public void BulkLyrics_RealizesOnlyViewportRows()
    {
        EnsureAppResources();
        var vm = new BulkLyricsViewModel(Tracks(), new StubLyricsBulk(), remove: false);
        AssertVirtualized(RealizedRows(new BulkLyricsDialog(vm), vm.Rows));
    }

    [AvaloniaFact]
    public void MetadataFinder_RealizesOnlyViewportRows()
    {
        EnsureAppResources();
        var vm = new MetadataFinderViewModel(Tracks(), null!, null!, new FakeLibraryService());
        AssertVirtualized(RealizedRows(new MetadataFinderDialog(vm), vm.Rows));
    }

    [AvaloniaFact]
    public void DuplicateFinder_RealizesOnlyViewportGroups()
    {
        EnsureAppResources();
        var groups = Tracks().Select(t => new DuplicateGroup(new[] { t, t }, t.Id)).ToList();
        var vm = new DuplicateFinderViewModel(new StubDuplicates(groups));
        Assert.Equal(RowCount, vm.Groups.Count); // the stub scan completed synchronously
        AssertVirtualized(RealizedRows(new DuplicateFinderDialog(vm), vm.Groups));
    }

    [AvaloniaFact]
    public async Task SendToFolder_RealizesOnlyViewportRows()
    {
        EnsureAppResources();
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var vm = new SendToFolderViewModel(Tracks(), new StubSendTo(), FileOrganizePlanner.DefaultPattern, root);
            await vm.PlanRebuild;
            Assert.Equal(RowCount, vm.Rows.Count);
            AssertVirtualized(RealizedRows(new SendToFolderDialog(vm), vm.Rows));
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    [AvaloniaFact]
    public async Task OrganizeFiles_RealizesOnlyViewportRows_AndFillsInOneReset()
    {
        EnsureAppResources();
        using var persistence = new TestPersistenceService();
        var settings = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpPlayHistory());
        settings.OrganizeTargetRoot = TestPaths.Primary("Organized");
        var vm = new OrganizeFilesViewModel(Tracks(), new StubOrganizer(), settings);
        for (var i = 0; i < 500 && vm.Rows.Count < RowCount; i++) await Task.Delay(10);
        Assert.Equal(RowCount, vm.Rows.Count);

        // Update Preview rebuilds the whole list: one notification, not Clear + one Add per track.
        var changes = 0;
        vm.Rows.CollectionChanged += (_, _) => changes++;
        await vm.PreviewCommand.ExecuteAsync(null);
        Assert.Equal(RowCount, vm.Rows.Count);
        Assert.Equal(1, changes);

        AssertVirtualized(RealizedRows(new OrganizeFilesDialog(vm), vm.Rows));
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private sealed class StubOrganizer : IFileOrganizerService
    {
        public IReadOnlyList<OrganizeMove> Plan(IEnumerable<Track> tracks, string pattern, string targetRoot)
            => tracks.Select(t => new OrganizeMove(t.Id, t.FilePath,
                Path.Combine(targetRoot, Path.GetFileName(t.FilePath)), OrganizeAction.Move)).ToList();
        public Task<OrganizeResult> ApplyAsync(IReadOnlyList<OrganizeMove> moves, CancellationToken ct = default)
            => throw new NotSupportedException();
        public bool CanUndo => false;
        public Task<OrganizeResult> UndoLastAsync(CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubConverter : IAudioConverterService
    {
        public string? GetFfmpegPath() => null;
        public Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<ConvertSummary> ConvertAsync(IReadOnlyList<Track> tracks, AudioConvertOptions options,
            IProgress<ConvertProgress> progress, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class StubLyricsBulk : ILyricsBulkService
    {
        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }

    private sealed class StubDuplicates(IReadOnlyList<DuplicateGroup> groups) : IDuplicateFinderService
    {
        public Task<IReadOnlyList<DuplicateGroup>> FindAsync(int durationToleranceSeconds = DuplicateMatcher.DefaultDurationToleranceSeconds,
            CancellationToken ct = default) => Task.FromResult(groups);
        public Task<int> DeleteAsync(IReadOnlyList<Guid> trackIds, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    private sealed class StubSendTo : ISendToFolderService
    {
        public IReadOnlyList<SendToFolderItem> Plan(IEnumerable<Track> tracks, string targetRoot, string? organizePattern, bool includeLyrics)
            => tracks.Select(t => new SendToFolderItem(t, t.FilePath, Path.Combine(targetRoot, Path.GetFileName(t.FilePath)),
                SendToFolderAction.Copy, null, null)).ToList();
        public Task<SendToFolderResult> CopyAsync(IReadOnlyList<SendToFolderItem> plan, IProgress<SendToFolderProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
