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
/// GitHub #167: "is animated artwork deleted when the album is removed from the library?" It
/// was not: removal deleted the album's cached still cover only, so every saved loop
/// (animated_covers/&lt;album&gt;.mp4, the per-track &lt;album&gt;__&lt;track&gt;.mp4 and the
/// auto-fetch cache) stayed in the data folder for good.
/// </summary>
[Collection("MetadataServiceStatics")]
public class AnimatedArtworkRemovalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "animremove-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public AnimatedArtworkRemovalTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(Path.Combine(_music, "A"));
        Directory.CreateDirectory(Path.Combine(_music, "B"));
        WriteMp3(Path.Combine(_music, "A", "01.mp3"), "One", "Album A");
        WriteMp3(Path.Combine(_music, "A", "02.mp3"), "Two", "Album A");
        WriteMp3(Path.Combine(_music, "B", "01.mp3"), "Three", "Album B");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private static void WriteMp3(string path, string title, string album)
    {
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.Album = album;
        f.Save();
    }

    private async Task<(LibraryService Library, PersistenceService Persistence)> ScannedAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var settings = new AppSettings();
        settings.MusicFolders.Add(_music);
        await persistence.SaveSettingsAsync(settings);
        var library = new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NullAudit());
        await library.ScanAsync(new[] { _music });
        Assert.Equal(3, library.Tracks.Count);
        return (library, persistence);
    }

    private static string Touch(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "loop");
        return path;
    }

    [Fact]
    public async Task RemovingAnAlbum_DeletesItsSavedAndCachedLoops_AndLeavesOtherAlbumsAlone()
    {
        var (library, persistence) = await ScannedAsync();
        var a = library.Tracks.Where(t => t.Album == "Album A").ToList();
        var b = library.Tracks.Single(t => t.Album == "Album B");

        var albumLoop = Touch(persistence.GetAnimatedCoverPath(a[0].AlbumId, null, ".mp4"));
        var trackLoop = Touch(persistence.GetAnimatedCoverPath(a[0].AlbumId, a[1].Id, ".webm"));
        var cachedLoop = Touch(persistence.GetCachedAnimatedCoverPath(a[0].AlbumId));
        var otherLoop = Touch(persistence.GetAnimatedCoverPath(b.AlbumId, null, ".mp4"));
        var otherCached = Touch(persistence.GetCachedAnimatedCoverPath(b.AlbumId));

        await library.RemoveTracksAsync(a.Select(t => t.Id));

        Assert.False(File.Exists(albumLoop));
        Assert.False(File.Exists(trackLoop));
        Assert.False(File.Exists(cachedLoop));
        Assert.True(File.Exists(otherLoop));
        Assert.True(File.Exists(otherCached));
    }

    [Fact]
    public async Task RemovingOneSongOfAnAlbum_DeletesOnlyThatSongsLoop()
    {
        var (library, persistence) = await ScannedAsync();
        var a = library.Tracks.Where(t => t.Album == "Album A").OrderBy(t => t.Title).ToList();

        var albumLoop = Touch(persistence.GetAnimatedCoverPath(a[0].AlbumId, null, ".mp4"));
        var removedSongLoop = Touch(persistence.GetAnimatedCoverPath(a[0].AlbumId, a[0].Id, ".mp4"));
        var keptSongLoop = Touch(persistence.GetAnimatedCoverPath(a[0].AlbumId, a[1].Id, ".mp4"));

        await library.RemoveTrackAsync(a[0].Id);

        Assert.True(File.Exists(albumLoop));
        Assert.False(File.Exists(removedSongLoop));
        Assert.True(File.Exists(keptSongLoop));
    }

    private sealed class NullAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
