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
/// Settings › Library › remove a music folder (11-24, found live): the songs only left the library
/// when the rescan that follows finished. A quit mid-scan saved a checkpoint holding every song
/// the scan started with, and a desktop scan meeting any unreachable folder is abandoned, so the
/// removed folder's songs stayed. <see cref="LibraryService.RemoveFolderTracksAsync"/> takes them
/// out at once, without excluding anything, so adding the folder again brings them back with
/// their play counts and favorites.
/// </summary>
[Collection("MetadataServiceStatics")]
public class RemoveMusicFolderSongsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }

    private static void WriteMp3(string path, string title)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = title + " Album";
        f.Save();
    }

    private static string[] Titles(System.Collections.Generic.IEnumerable<Track> tracks)
        => tracks.Select(t => t.Title).OrderBy(t => t).ToArray();

    [Fact]
    public async Task RemovedFolder_SongsLeaveAtOnce_StayGoneAfterARestart_AndComeBackWithTheirStateWhenReAdded()
    {
        var music = Path.Combine(_root, "Music");
        var other = Path.Combine(_root, "Other");
        var nested = Path.Combine(music, "Kept");
        WriteMp3(Path.Combine(music, "Gone", "g.mp3"), "GoneSong");
        WriteMp3(Path.Combine(nested, "k.mp3"), "NestedSong");
        WriteMp3(Path.Combine(other, "o.mp3"), "OtherSong");
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music, other, nested } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await library.ScanAsync(new[] { music, other, nested });
        Assert.Equal(new[] { "GoneSong", "NestedSong", "OtherSong" }, Titles(library.Tracks));

        var gone = library.Tracks.Single(t => t.Title == "GoneSong");
        gone.IsFavorite = true;
        gone.PlayCount = 7;
        await library.SaveTrackUserStateAsync(new[] { gone });

        // Settings saves the shorter list first, then hands the remaining folders over.
        var remaining = new[] { other, nested };
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { other, nested } });
        var updates = 0;
        library.LibraryUpdated += (_, _) => updates++;
        await library.RemoveFolderTracksAsync(music, remaining);

        Assert.Equal(1, updates);
        // The nested folder is still a music folder: its song stays.
        Assert.Equal(new[] { "NestedSong", "OtherSong" }, Titles(library.Tracks));
        Assert.DoesNotContain(library.Albums, a => a.Name == "GoneSong Album");
        Assert.Empty((await persistence.LoadSettingsAsync()).ExcludedFilePaths);

        // Written through: a restart (no rescan) doesn't bring the song back.
        var restarted = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await restarted.LoadAsync();
        Assert.Equal(new[] { "NestedSong", "OtherSong" }, Titles(restarted.Tracks));

        // A second call (the rescan's turn, or a double click) changes nothing.
        await library.RemoveFolderTracksAsync(music, remaining);
        Assert.Equal(1, updates);

        // Added again: the song comes back with its favorite and play count.
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music, other, nested } });
        await library.ScanAsync(new[] { music, other, nested });
        var back = library.Tracks.Single(t => t.Title == "GoneSong");
        Assert.True(back.IsFavorite);
        Assert.Equal(7, back.PlayCount);
    }

    /// <summary>The rescan alone could not do it while any other music folder was unreachable
    /// (an unplugged drive): the desktop abandons such a scan and keeps the library as it was.</summary>
    [Fact]
    public async Task RemovedFolder_SongsLeave_EvenWhileAnotherMusicFolderIsUnreachable()
    {
        var music = Path.Combine(_root, "Music");
        var offline = Path.Combine(_root, "Unplugged");
        WriteMp3(Path.Combine(music, "g.mp3"), "GoneSong");
        WriteMp3(Path.Combine(offline, "u.mp3"), "OfflineSong");
        var persistence = new PersistenceService(Path.Combine(_root, "data2"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music, offline } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoOpAudit());
        await library.ScanAsync(new[] { music, offline });
        Directory.Move(offline, offline + "-away"); // the drive goes away

        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { offline } });
        await library.ScanAsync(new[] { offline });
        Assert.Contains(library.Tracks, t => t.Title == "GoneSong"); // the scan was abandoned

        await library.RemoveFolderTracksAsync(music, new[] { offline });
        Assert.Equal(new[] { "OfflineSong" }, Titles(library.Tracks));
    }
}
