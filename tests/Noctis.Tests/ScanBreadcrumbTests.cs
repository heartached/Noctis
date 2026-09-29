using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #97 (Fedora AppImage): adding new albums crashed Noctis on startup, and the
/// session log ended at the scan's publish with no exception. The steps after that
/// publish only run when a scan finds changes, and their failures were swallowed into
/// Debug.WriteLine (compiled out of Release). These pin the session-log breadcrumbs that
/// name the step (and, in Developer Mode, the file) that was running when a process ends.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ScanBreadcrumbTests : IDisposable
{
    private readonly string _musicDir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ScanBreadcrumbTests() => Directory.CreateDirectory(_musicDir);

    public void Dispose()
    {
        try { Directory.Delete(_musicDir, true); } catch { }
    }

    /// <summary>The session log written after <paramref name="marker"/> (other tests share it).</summary>
    private static string LogAfter(string marker)
    {
        var log = DebugLog.Snapshot();
        var at = log.LastIndexOf(marker, StringComparison.Ordinal);
        Assert.True(at >= 0, "marker line fell out of the session log");
        return log[(at + marker.Length)..];
    }

    [Fact]
    public void CappedBreadcrumb_WritesUpToTheCapThenOneSuppressedLine()
    {
        var label = "crumb-" + Guid.NewGuid().ToString("N");
        var crumb = new CappedBreadcrumb("Scan", label, cap: 3, isEnabled: () => true);

        for (var i = 0; i < 10; i++)
            crumb.Note($"file{i}.flac");

        var lines = DebugLog.Snapshot().Split('\n').Where(l => l.Contains(label)).ToList();
        Assert.Equal(4, lines.Count);
        Assert.Contains("file0.flac", lines[0]);
        Assert.Contains("file2.flac", lines[2]);
        Assert.Contains("(further suppressed)", lines[3]);
    }

    [Fact]
    public void CappedBreadcrumb_WritesNothingWithDeveloperModeOff()
    {
        var label = "crumb-" + Guid.NewGuid().ToString("N");
        var crumb = new CappedBreadcrumb("Scan", label, cap: 3, isEnabled: () => false);

        crumb.Note("file.flac");

        Assert.DoesNotContain(label, DebugLog.Snapshot());
    }

    [Fact]
    public void LargeCoverDecode_IsNamedInTheSessionLog_UsualSizesStayQuiet()
    {
        var big = Path.Combine(_musicDir, Guid.NewGuid().ToString("N") + ".png");
        var usual = Path.Combine(_musicDir, Guid.NewGuid().ToString("N") + ".png");

        Assert.True(SkiaArtworkDecoder.NoteLargeDecode(5001, 5000, SKEncodedImageFormat.Png, big));
        Assert.False(SkiaArtworkDecoder.NoteLargeDecode(5000, 5000, SKEncodedImageFormat.Png, usual));

        var log = DebugLog.Snapshot();
        Assert.Contains($"decoding large cover 5001x5000 Png: {big}", log);
        Assert.DoesNotContain(usual, log);
    }

    [Fact]
    public async Task ScanWithNewFiles_BracketsEachPostPublishStep_AndLogsSwallowedSaveFailures()
    {
        var path = Path.Combine(_musicDir, "new.wav");
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Album = "New Album";
            f.Tag.Performers = new[] { "Breadcrumb Artist" };
            f.Save();
        }

        var saveError = "library save " + Guid.NewGuid().ToString("N");
        var indexError = "index save " + Guid.NewGuid().ToString("N");
        using var persistence = new FailingSavePersistence(saveError, indexError);
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new FakeAuditTrail());

        var marker = "scan-start " + Guid.NewGuid().ToString("N");
        DebugLog.Write("Test", marker);
        await library.ScanAsync(new[] { _musicDir }, TestContext.Current.CancellationToken);

        var log = LogAfter(marker);
        string[] steps =
        {
            "[Scan] published 1 tracks (changed=1, unchanged=0, skipped=0",
            "[Scan] artwork extract: start",
            "[Scan] artwork extract: done",
            "[Scan] stale folder art: start",
            "[Scan] stale folder art: done",
            "[Scan] per-track covers: 1 album(s)",
            "[Scan] per-track covers: done",
            "[Scan] rebuild: start",
            "[Scan] rebuild: done",
            "[Scan] save: start",
            "[Scan] save: done",
            "[Scan] done (1 tracks",
        };
        var last = -1;
        foreach (var step in steps)
        {
            var at = log.IndexOf(step, StringComparison.Ordinal);
            Assert.True(at > last, $"'{step}' missing or out of order");
            last = at;
        }

        // The save failure is swallowed so the scan finishes, but the log now says so.
        Assert.Contains($"[Library] Failed to save library: {saveError}", log);
        // The index cache is written fire-and-forget from the rebuild.
        for (var i = 0; i < 100 && !DebugLog.Snapshot().Contains(indexError); i++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Contains($"[Library] Failed to save index cache: {indexError}", DebugLog.Snapshot());

        // A launch that finds nothing new takes the fast path and adds no stage lines.
        var marker2 = "scan-again " + Guid.NewGuid().ToString("N");
        DebugLog.Write("Test", marker2);
        await library.ScanAsync(new[] { _musicDir }, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("[Scan] published", LogAfter(marker2));
    }

    private sealed class FailingSavePersistence : IPersistenceService, IDisposable
    {
        private readonly string _saveError;
        private readonly string _indexError;

        public string DataDirectory { get; }
        public AppSettings Settings { get; set; } = new() { MetadataSchemaVersion = int.MaxValue };

        public FailingSavePersistence(string saveError, string indexError)
        {
            _saveError = saveError;
            _indexError = indexError;
            DataDirectory = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(DataDirectory, "artwork"));
        }

        public bool LibraryLoadFailed => false;
        public string? LastCorruptFilePath => null;
        public bool SettingsLoadFailed => false;

        public Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
        public Task SaveSettingsAsync(AppSettings settings) => Task.CompletedTask;
        public Task<List<Track>?> LoadLibraryAsync() => Task.FromResult<List<Track>?>(new List<Track>());
        public Task SaveLibraryAsync(List<Track> tracks) => Task.FromException(new IOException(_saveError));
        public Task<List<Playlist>> LoadPlaylistsAsync() => Task.FromResult(new List<Playlist>());
        public Task SavePlaylistsAsync(List<Playlist> playlists) => Task.CompletedTask;
        public Task<QueueState?> LoadQueueStateAsync() => Task.FromResult<QueueState?>(null);
        public Task SaveQueueStateAsync(QueueState state) => Task.CompletedTask;
        public Task SaveQueuePositionAsync(Guid? currentTrackId, double positionSeconds) => Task.CompletedTask;
        public Task<LibraryIndexCache?> LoadIndexCacheAsync() => Task.FromResult<LibraryIndexCache?>(null);
        public Task SaveIndexCacheAsync(LibraryIndexCache cache) => Task.FromException(new IOException(_indexError));

        public string GetArtworkPath(Guid albumId) => Path.Combine(DataDirectory, "artwork", $"{albumId}.jpg");
        public void SaveArtwork(Guid albumId, byte[] imageData) => File.WriteAllBytes(GetArtworkPath(albumId), imageData);

        public string GetAnimatedCoverPath(Guid albumId, Guid? trackId, string extension)
            => Path.Combine(DataDirectory, "animated_covers", $"{albumId}.mp4");
        public void EnsureAnimatedCoverDir() { }

        public void Dispose()
        {
            try { if (Directory.Exists(DataDirectory)) Directory.Delete(DataDirectory, true); } catch { }
        }
    }

    private sealed class FakeAuditTrail : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
