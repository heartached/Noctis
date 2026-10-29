using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #148: <see cref="ILibraryService.TracksAdded"/> names only the files the library
/// did not know — a rescan's or the watcher import's new files — never re-read known ones.
/// </summary>
[Collection("MetadataServiceStatics")]
public class LibraryTracksAddedTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "rg148lib-" + Guid.NewGuid().ToString("N"));
    private string Music => Path.Combine(_root, "music");

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
            f.Save();
        }
        // A rescan notices a change by size + mtime; make sure the clock moved.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    private async Task<LibraryService> LibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var settings = new AppSettings();
        settings.MusicFolders.Add(Music);
        await persistence.SaveSettingsAsync(settings);
        return new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NullAudit());
    }

    [Fact]
    public async Task Rescan_NamesOnlyTheNewFile()
    {
        WriteMp3(Path.Combine(Music, "Album", "01.mp3"), "First");
        var library = await LibraryAsync();
        await library.ScanAsync(new[] { Music });

        var raised = new List<IReadOnlyList<Track>>();
        library.TracksAdded += (_, t) => raised.Add(t);

        // Unchanged rescan: nothing new.
        await library.ScanAsync(new[] { Music });
        Assert.Empty(raised);

        // A re-tagged known file plus a new one: only the new one.
        WriteMp3(Path.Combine(Music, "Album", "01.mp3"), "First (Remaster)");
        WriteMp3(Path.Combine(Music, "Album", "02.mp3"), "Second");
        await library.ScanAsync(new[] { Music });
        Assert.Equal("Second", Assert.Single(Assert.Single(raised)).Title);
    }

    [Fact]
    public async Task WatcherImport_NamesTheNewFile_NotAReimportedKnownOne()
    {
        var known = Path.Combine(Music, "Album", "01.mp3");
        WriteMp3(known, "First");
        var library = await LibraryAsync();
        await library.ScanAsync(new[] { Music });

        var raised = new List<IReadOnlyList<Track>>();
        library.TracksAdded += (_, t) => raised.Add(t);

        WriteMp3(known, "First (Remaster)");
        var fresh = Path.Combine(Music, "Album", "02.mp3");
        WriteMp3(fresh, "Second");
        await library.ImportDiscoveredFilesAsync(new[] { known, fresh });

        Assert.Equal(fresh, Assert.Single(Assert.Single(raised)).FilePath, ignoreCase: true);
    }

    private sealed class NullAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
