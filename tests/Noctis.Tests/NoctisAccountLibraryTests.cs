using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Sync;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Phone account, Core side: the desktop's songs live in the shared LibraryService as
/// SourceType.NoctisServer tracks. They survive rescans (without costing the no-change fast
/// path), are replaced as one set by a catalog sync, keep phone-side state across metadata
/// changes, persist through a reload (journal included), leave with sign-out, and reach the
/// sync recorder — while the desktop's own ledger keeps ignoring anything but local files.
/// </summary>
[Collection("MetadataServiceStatics")]
public class NoctisAccountLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "acc-lib-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public NoctisAccountLibraryTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
        WriteMp3(Path.Combine(_music, "Album", "01 - Local.mp3"), "Local song");
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    internal static void WriteMp3(string path, string title)
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

    internal static Track Remote(Guid id, string title, string album = "Desk Album", string artist = "Desk Artist") => new()
    {
        Id = id,
        FilePath = "noctis-remote://tr-" + id.ToString("N"),
        SourceType = SourceType.NoctisServer,
        Title = title,
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        AlbumId = Track.ComputeAlbumId(artist, album),
        Duration = TimeSpan.FromSeconds(100),
        TrackNumber = 1,
    };

    private LibraryService NewLibrary(out PersistenceService persistence, ITrackStateRecorder? recorder = null)
    {
        persistence = new PersistenceService(Path.Combine(_root, "data"));
        return new LibraryService(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NoOpAudit(),
            syncRecorder: recorder);
    }

    [Fact]
    public void IsRemoteStream_CoversDesktopSongs()
    {
        Assert.True(Remote(Guid.NewGuid(), "x").IsRemoteStream);
        Assert.False(Remote(Guid.NewGuid(), "x").HasFilesystemPath);
        Assert.Equal(7, (int)SourceType.NoctisServer);
    }

    [Fact]
    public async Task RemoteTracks_SurviveARescan_AndTheNoChangeScanStaysOnItsFastPath()
    {
        var library = NewLibrary(out var persistence);
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await library.ScanAsync(new[] { _music });
        Assert.Single(library.Tracks);

        var a = Remote(Guid.NewGuid(), "Desk one");
        var b = Remote(Guid.NewGuid(), "Desk two");
        await library.ReplaceRemoteTracksAsync(new[] { a, b });
        Assert.Equal(3, library.Tracks.Count);
        Assert.Contains(library.Albums, al => al.Id == a.AlbumId && al.Tracks.Count == 2);

        var libraryJson = Path.Combine(_root, "data", "library.json");
        var stamp = File.GetLastWriteTimeUtc(libraryJson);

        await library.ScanAsync(new[] { _music });

        Assert.Equal(3, library.Tracks.Count);
        Assert.Same(a, library.GetTrackById(a.Id));
        Assert.Same(b, library.GetTrackById(b.Id));
        // Nothing on disk changed: the scan must not count the remote songs as "missing"
        // and fall off its no-change fast path into a full publish + save.
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(libraryJson));
    }

    [Fact]
    public async Task RemovingTheLastFolder_DropsLocalSongs_KeepsDesktopSongs()
    {
        var library = NewLibrary(out var persistence);
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await library.ScanAsync(new[] { _music });
        var remote = Remote(Guid.NewGuid(), "Desk one");
        await library.ReplaceRemoteTracksAsync(new[] { remote });

        await persistence.SaveSettingsAsync(new AppSettings());
        await library.ScanAsync(Array.Empty<string>());

        Assert.Same(remote, Assert.Single(library.Tracks));
    }

    [Fact]
    public async Task Replace_SwapsTheWholeRemoteSet_KeepsLocalTracks_AndPhoneStateAcrossMetadataChanges()
    {
        var library = NewLibrary(out var persistence);
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await library.ScanAsync(new[] { _music });
        var local = Assert.Single(library.Tracks);

        var keep = Remote(Guid.NewGuid(), "Unchanged");
        var retagged = Remote(Guid.NewGuid(), "Old title");
        var dropped = Remote(Guid.NewGuid(), "Deleted on the desktop");
        await library.ReplaceRemoteTracksAsync(new[] { keep, retagged, dropped });
        retagged.Rating = 4;
        retagged.Badge = "Gym";
        retagged.PlayCount = 7;

        var updates = 0;
        library.LibraryUpdated += (_, _) => Interlocked.Increment(ref updates);
        var keepCopy = Remote(keep.Id, "Unchanged");
        var retaggedNew = Remote(retagged.Id, "New title");
        var added = Remote(Guid.NewGuid(), "Brand new");
        added.SourceType = SourceType.Local; // callers need not set it: the set is NoctisServer by definition
        await library.ReplaceRemoteTracksAsync(new[] { keepCopy, retaggedNew, added });

        Assert.Equal(4, library.Tracks.Count);
        Assert.Same(local, library.GetTrackById(local.Id));
        Assert.Same(keep, library.GetTrackById(keep.Id));          // unchanged: same instance
        var now = library.GetTrackById(retagged.Id)!;
        Assert.Equal("New title", now.Title);                      // changed: new metadata…
        Assert.Equal(4, now.Rating);                                // …phone-side state kept
        Assert.Equal("Gym", now.Badge);
        Assert.Equal(7, now.PlayCount);
        Assert.Null(library.GetTrackById(dropped.Id));
        Assert.Equal(SourceType.NoctisServer, library.GetTrackById(added.Id)!.SourceType);
        Assert.True(updates >= 1);
    }

    [Fact]
    public async Task RemoveRemoteTracks_DropsOnlyDesktopSongs()
    {
        var library = NewLibrary(out var persistence);
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        await library.ScanAsync(new[] { _music });
        var local = Assert.Single(library.Tracks);
        await library.ReplaceRemoteTracksAsync(new[] { Remote(Guid.NewGuid(), "a"), Remote(Guid.NewGuid(), "b") });

        await library.RemoveRemoteTracksAsync();

        Assert.Same(local, Assert.Single(library.Tracks));
        Assert.DoesNotContain(library.Albums, a => a.Name == "Desk Album");
    }

    [Fact]
    public async Task RemoteSongsAndTheirState_SurviveAReload_AndAStaleJournalRowCannotRevertThem()
    {
        var id = Guid.NewGuid();
        var library = NewLibrary(out _);
        var first = Remote(id, "Desk one");
        first.IsFavorite = true;
        first.Rating = 5;
        await library.ReplaceRemoteTracksAsync(new[] { first });
        await library.SaveTrackUserStateAsync(new[] { first }); // journal row: favorite, 5 stars

        // Sign out, sign in again: the desktop now says not a favorite, 2 stars.
        await library.RemoveRemoteTracksAsync();
        var again = Remote(id, "Desk one");
        again.Rating = 2;
        await library.ReplaceRemoteTracksAsync(new[] { again });

        var reloaded = NewLibrary(out _);
        await reloaded.LoadAsync();
        await reloaded.BackgroundInit;
        var track = Assert.Single(reloaded.Tracks);
        Assert.Equal(SourceType.NoctisServer, track.SourceType);
        Assert.Equal("noctis-remote://tr-" + id.ToString("N"), track.FilePath);
        Assert.False(track.IsFavorite);
        Assert.Equal(2, track.Rating);
    }

    [Fact]
    public async Task UserStateSaves_ReachTheRecorder_ButTheDesktopLedgerIgnoresDesktopSongs()
    {
        var recorder = new CapturingRecorder();
        var library = NewLibrary(out var persistence, recorder);
        var remote = Remote(Guid.NewGuid(), "Desk one");
        await library.ReplaceRemoteTracksAsync(new[] { remote });

        remote.IsFavorite = true;
        await library.SaveTrackUserStateAsync(new[] { remote });
        var recorded = await recorder.Next.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Contains(remote, recorded);

        // The desktop's ledger records only its own files.
        var settings = new AppSettings { SyncEnabled = true, SyncDeviceId = "desktop-test" };
        var sync = new LibrarySyncService(() => settings, persistence);
        sync.RecordTrackStates(new[] { remote });
        Assert.Equal(0, sync.CurrentSeq);
        sync.RecordTrackStates(new[] { new Track { Id = Guid.NewGuid(), FilePath = Path.Combine(_music, "x.mp3"), IsFavorite = true } });
        Assert.Equal(1, sync.CurrentSeq);
    }

    private sealed class CapturingRecorder : ITrackStateRecorder
    {
        private TaskCompletionSource<List<Track>> _next = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<List<Track>> Next => _next.Task;
        public void RecordTrackStates(IEnumerable<Track> tracks) => _next.TrySetResult(tracks.ToList());
    }

    internal sealed class NoOpAudit : IAuditTrailService
    {
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default) => Task.CompletedTask;
    }
}
