using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// An import that landed while a scan was still walking the folders (a drop, a watcher
/// batch, a finished download) was overwritten by the scan's publish — it replaces the whole
/// track list with what it walked — so the imported track vanished until the next scan.
/// Imports now wait for a running scan.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ImportDuringScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title)
    {
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        Retag(path, title);
    }

    private static void Retag(string path, string title)
    {
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
            f.Save();
        }
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    [Fact]
    public async Task ImportDuringAScan_IsNotLostWhenTheScanPublishes()
    {
        var music = Path.Combine(_root, "music");
        var second = Path.Combine(_root, "music2");
        Directory.CreateDirectory(Path.Combine(music, "Album"));
        Directory.CreateDirectory(second);
        WriteMp3(Path.Combine(music, "Album", "01 - First.mp3"), "First");
        WriteMp3(Path.Combine(music, "Album", "02 - Second.mp3"), "Second");
        WriteMp3(Path.Combine(second, "Other.mp3"), "Other");

        var source = new GatedSource(second);
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit(), fileSystem: source);
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music, second } });
        await library.ScanAsync(new[] { music, second });
        Assert.Equal(3, library.Tracks.Count);

        // A changed file makes the scan publish the list it walked (a no-change scan keeps
        // the current list as it is).
        Retag(Path.Combine(second, "Other.mp3"), "Other (edit)");
        source.Gate = new SemaphoreSlim(0);
        var scan = library.ScanAsync(new[] { music, second });
        Assert.True(await source.Entered.WaitAsync(TimeSpan.FromSeconds(10)), "scan never reached the gated folder");

        // The first folder was walked already; this file appears there only now.
        var late = Path.Combine(music, "Album", "03 - Late.mp3");
        WriteMp3(late, "Late");
        var import = library.ImportFilesAsync(new[] { late });
        await Task.WhenAny(import, Task.Delay(1000));

        source.Gate.Release();
        await scan;
        await import;

        Assert.Contains(library.Tracks, t => t.Title == "Late");
        Assert.Equal(4, library.Tracks.Count);
    }

    /// <summary>The local walk, holding the scan inside one root until released.</summary>
    private sealed class GatedSource : IFileSystemSource
    {
        private readonly LocalFileSystemSource _inner = new();
        private readonly string _gatedRoot;
        public SemaphoreSlim? Gate;
        public readonly SemaphoreSlim Entered = new(0);
        public GatedSource(string gatedRoot) => _gatedRoot = gatedRoot;

        public bool RootExists(string root) => _inner.RootExists(root);

        public IEnumerable<ScanEntry> EnumerateAudioFiles(string root, IReadOnlyCollection<string> excludedRoots,
            IReadOnlySet<string> ignoredFolderNames, Action<string> reportFailedDirectory)
        {
            if (Gate != null && string.Equals(root, _gatedRoot, StringComparison.OrdinalIgnoreCase))
            {
                Entered.Release();
                Gate.Wait();
            }
            return _inner.EnumerateAudioFiles(root, excludedRoots, ignoredFolderNames, reportFailedDirectory);
        }
    }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
