using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Issue #31 (lag switching between Settings/Home/Folders).
///
/// Reading the storage figures means a recursive walk of the artwork cache —
/// EnumerateFiles(AllDirectories) plus a FileInfo.Length stat per file, memoized for
/// only 5 seconds. <see cref="SettingsViewModel.RefreshStorageInfo"/> does that on the
/// calling thread, so running it from a navigation handler stalls the UI thread for as
/// long as the walk takes.
///
/// The Settings *modal* path already knew this and used the async variant
/// ("so none of it blocks the click"); the Settings *navigation* path did not, and
/// v1.3.8 shipped the blocking call. These tests pin the swap: identical output, and
/// no blocking call left on a navigation path.
/// </summary>
public class SettingsStorageRefreshTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private SettingsViewModel CreateViewModel() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>Populates the data root the way a real install looks: the four JSON files
    /// plus a nested artwork cache, so the walk has something to measure.</summary>
    private void SeedDataDirectory(int artworkFiles)
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "library.json", "queue.json", "playlists.json", "settings.json" })
            File.WriteAllText(Path.Combine(_root, name), new string('x', 512));

        var artwork = Path.Combine(_root, "artwork");
        var artists = Path.Combine(artwork, "artists");
        Directory.CreateDirectory(artists);
        for (var i = 0; i < artworkFiles; i++)
        {
            // Split across the nested folder so AllDirectories recursion is exercised.
            var dir = i % 2 == 0 ? artwork : artists;
            File.WriteAllText(Path.Combine(dir, $"cover{i}.jpg"), new string('y', 128));
        }
    }

    // ── 1. The swap is behaviour-preserving ──

    [AvaloniaFact]
    public async Task AsyncStorageRefresh_ReportsTheSameFiguresAsTheBlockingOne()
    {
        SeedDataDirectory(artworkFiles: 40);

        var sync = CreateViewModel();
        sync.RefreshStorageInfo();

        var async = CreateViewModel();
        await async.RefreshStorageInfoAsync();

        Assert.Equal(sync.StorageLibraryData, async.StorageLibraryData);
        Assert.Equal(sync.StorageArtwork, async.StorageArtwork);
        Assert.Equal(sync.StoragePlaylists, async.StoragePlaylists);
        Assert.Equal(sync.StorageSettings, async.StorageSettings);
        Assert.Equal(sync.StorageTotal, async.StorageTotal);

        // Guard against both simply reporting "0 B" and trivially agreeing.
        Assert.NotEqual("0 B", async.StorageArtwork);
        Assert.NotEqual("0 B", async.StorageTotal);
    }

    // ── 2. No blocking walk left on a navigation path ──

    /// <summary>
    /// Source-level guard, in the spirit of IconResourceReferenceTests: the defect is a
    /// UI-thread stall, which no unit assertion observes directly, but the cause is
    /// exactly "this method calls the blocking overload". Pin that.
    /// </summary>
    [Fact]
    public void SettingsNavigationPath_DoesNotCallTheBlockingStorageRefresh()
    {
        var source = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Noctis", "ViewModels", "MainWindowViewModel.cs"));

        var method = Regex.Match(
            source,
            @"private SettingsViewModel RefreshAndReturnSettings\(\)\s*\{(?<body>[^}]*)\}",
            RegexOptions.Singleline);

        Assert.True(method.Success, "RefreshAndReturnSettings not found — update this test");

        var body = method.Groups["body"].Value;
        Assert.DoesNotContain("RefreshStorageInfo()", body);
        Assert.Contains("RefreshStorageInfoAsync()", body);
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        return dir!;
    }
}
