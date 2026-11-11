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
/// GitHub #225: right after launch the Albums grid flickered and lost its Ctrl-selection.
/// Every scan publish rebuilt every Album object, so the grid (which compares albums by
/// reference) re-filled itself each time. A scan now hands back the same Album object for
/// an album whose songs did not change; a re-read song still gives its album a new one.
/// </summary>
[Collection("MetadataServiceStatics")]
public class ScanKeepsUnchangedAlbumsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public ScanKeepsUnchangedAlbumsTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(Path.Combine(_music, "A"));
        Directory.CreateDirectory(Path.Combine(_music, "B"));
        WriteMp3(Path.Combine(_music, "A", "01.mp3"), "A One", "Album A");
        WriteMp3(Path.Combine(_music, "A", "02.mp3"), "A Two", "Album A");
        WriteMp3(Path.Combine(_music, "B", "01.mp3"), "B One", "Album B");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title, string album)
    {
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        Retag(path, title, album);
    }

    private static void Retag(string path, string title, string album)
    {
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = album;
            f.Save();
        }
        // A rescan notices a change by size + mtime; make sure the clock moved.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    private sealed class NoAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    [Fact]
    public async Task Rescan_KeepsTheAlbumObjectOfAnUnchangedAlbum_AndRebuildsTheChangedOne()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var settings = new AppSettings();
        settings.MusicFolders.Add(_music);
        await persistence.SaveSettingsAsync(settings);
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NoAudit());

        await library.ScanAsync(new[] { _music });
        var a = library.Albums.Single(x => x.Name == "Album A");
        var b = library.Albums.Single(x => x.Name == "Album B");

        // A song of album B was edited outside the app: the rescan re-reads that file only.
        Retag(Path.Combine(_music, "B", "01.mp3"), "B One (edit)", "Album B");
        await library.ScanAsync(new[] { _music });

        var a2 = library.Albums.Single(x => x.Name == "Album A");
        var b2 = library.Albums.Single(x => x.Name == "Album B");
        Assert.Same(a, a2);
        Assert.Same(a, library.GetAlbumById(a.Id));
        Assert.NotSame(b, b2);
        Assert.Equal("B One (edit)", b2.Tracks.Single().Title);
    }

    [Fact]
    public void SameContent_ComparesSongsByObject_AndTheTileFields()
    {
        var t1 = new Track { Id = Guid.NewGuid(), Title = "x" };
        var t2 = new Track { Id = Guid.NewGuid(), Title = "y" };
        Album Make(params Track[] tracks) => new()
        {
            Id = Guid.Empty, Name = "N", Artist = "A", Year = 2001, Genre = "G", TrackCount = tracks.Length,
            ArtworkPath = "p", Tracks = tracks.ToList(),
        };
        Assert.True(Album.SameContent(Make(t1, t2), Make(t1, t2)));
        Assert.False(Album.SameContent(Make(t1, t2), Make(t2, t1)));
        var reRead = new Track { Id = t2.Id, Title = "y" };
        Assert.False(Album.SameContent(Make(t1, t2), Make(t1, reRead)));
        var art = Make(t1, t2); art.ArtworkPath = null;
        Assert.False(Album.SameContent(Make(t1, t2), art));
    }
}
