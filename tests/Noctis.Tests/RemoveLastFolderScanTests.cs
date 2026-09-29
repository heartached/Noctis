using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 09-23 (owner): removing the last media folder and rescanning left every album in the
/// app ("2144 tracks found" with no folders). The scan's "never replace a populated
/// library with nothing" guard treated it like a failed enumeration. With no folders
/// configured — in the scan call AND the saved settings — the library must empty.
/// </summary>
[Collection("MetadataServiceStatics")]
public class RemoveLastFolderScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public RemoveLastFolderScanTests()
    {
        _music = Path.Combine(_root, "music");
        var album = Path.Combine(_music, "Album");
        Directory.CreateDirectory(album);
        WriteMp3(Path.Combine(album, "01 - First.mp3"), "First");
        WriteMp3(Path.Combine(album, "02 - Second.mp3"), "Second");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title)
    {
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = "Fixture Album";
        f.Save();
    }

    private async Task<(LibraryService Library, PersistenceService Persistence)> ScannedLibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await library.ScanAsync(new[] { _music });
        Assert.Equal(2, library.Tracks.Count);
        return (library, persistence);
    }

    [Fact]
    public async Task RemovingTheLastFolder_EmptiesTheLibrary()
    {
        var (library, persistence) = await ScannedLibraryAsync();

        // Settings → Remove folder saves the empty list, then rescans with it.
        await persistence.SaveSettingsAsync(new AppSettings());
        await library.ScanAsync(Array.Empty<string>());

        Assert.Empty(library.Tracks);
        Assert.Empty(library.Albums);
    }

    [Fact]
    public async Task EmptyFolderList_WhileSettingsStillListFolders_KeepsTheLibrary()
    {
        // A caller handing in no folders before settings loaded must not wipe anything.
        var (library, _) = await ScannedLibraryAsync();

        await library.ScanAsync(Array.Empty<string>());

        Assert.Equal(2, library.Tracks.Count);
    }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
