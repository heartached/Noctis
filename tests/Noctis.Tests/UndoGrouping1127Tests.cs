using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord 11-27 (Halil): "I grouped two albums, now one is hidden and I don't know how to
/// reverse this." Group into One Album (#178) and Move to Album (#239) write the new album into
/// the files and kept no record of what the songs had before. Each changed song now remembers
/// its album tags from before (<see cref="Track.GroupUndo"/>), and the album menu's Undo Grouping
/// puts them back, in the library and in the files.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class UndoGrouping1127Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public UndoGrouping1127Tests() => ArtistCredit.ResetToDefaults();

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try
        {
            foreach (var f in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    // ── Fixtures ──

    private string Music => Path.Combine(_root, "music");

    private string WriteMp3(string relativePath, string album, string artist, string? albumArtist = null,
        uint track = 0, uint trackCount = 0, uint disc = 0, uint discCount = 0, uint year = 0,
        Action<TagLib.File>? more = null)
    {
        var path = Path.Combine(Music, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = Path.GetFileNameWithoutExtension(path);
        f.Tag.Performers = new[] { artist };
        if (albumArtist != null) f.Tag.AlbumArtists = new[] { albumArtist };
        f.Tag.Album = album;
        f.Tag.Track = track;
        f.Tag.TrackCount = trackCount;
        f.Tag.Disc = disc;
        f.Tag.DiscCount = discCount;
        f.Tag.Year = year;
        more?.Invoke(f);
        f.Save();
        return path;
    }

    private async Task<(LibraryService Library, PersistenceService Persistence)> NewLibraryAsync(int expected)
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { Music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != expected) Assert.Fail(ScanMissReport.Describe(library, Music));
        return (library, persistence);
    }

    private static async Task<LibraryService> RestartAsync(PersistenceService persistence)
    {
        var reloaded = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await reloaded.LoadAsync();
        await reloaded.BackgroundInit;
        return reloaded;
    }

    private static TagLib.File Read(string path) => TagLib.File.Create(path);

    private static Track Song(LibraryService library, string path) => library.AllTracks.Single(t => t.FilePath == path);

    // ── Group into One Album ──

    [Fact]
    public async Task Group_ThenUndo_PutsTheDeluxeBack_InTheLibraryAndTheFiles()
    {
        var main = WriteMp3(Path.Combine("Album", "01.mp3"), "Album", "Artist", "Artist", track: 1);
        var deluxe1 = WriteMp3(Path.Combine("Deluxe", "01.mp3"), "Album (Deluxe)", "Artist", "Artist", track: 1);
        var deluxe2 = WriteMp3(Path.Combine("Deluxe", "02.mp3"), "Album (Deluxe)", "Artist", "Artist", track: 2);
        var (library, persistence) = await NewLibraryAsync(3);
        var deluxeId = Song(library, deluxe1).AlbumId;
        persistence.SaveArtwork(deluxeId, new byte[] { 1 });

        var grouped = await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Album", "Artist");

        Assert.Equal(2, grouped.Grouped);
        var one = Assert.Single(library.Albums);
        // Only the songs the grouping changed remember anything; the main album's song did not move.
        Assert.Null(Song(library, main).GroupUndo);
        Assert.Equal(new[] { Song(library, deluxe1), Song(library, deluxe2) }.OrderBy(t => t.FilePath),
            AlbumUndo.UndoableSongs(one.Tracks).OrderBy(t => t.FilePath));
        using (var f = Read(deluxe1)) Assert.Equal("Album", f.Tag.Album);

        var updated = 0;
        library.LibraryUpdated += (_, _) => updated++;
        var undone = await library.UndoAlbumGroupingAsync(one.Tracks.ToList());

        Assert.Equal(2, undone.Grouped);
        Assert.Empty(undone.UnwrittenFiles);
        Assert.Equal(1, updated);
        Assert.Equal(2, library.Albums.Count);
        var back = Assert.Single(library.Albums, a => a.Name == "Album (Deluxe)");
        Assert.Equal(deluxeId, back.Id);
        Assert.Equal(2, back.Tracks.Count);
        Assert.All(library.AllTracks, t => Assert.Null(t.GroupUndo));
        using (var f = Read(deluxe1)) Assert.Equal("Album (Deluxe)", f.Tag.Album);
        using (var f = Read(deluxe2)) Assert.Equal("Album (Deluxe)", f.Tag.Album);
        using (var f = Read(main)) Assert.Equal("Album", f.Tag.Album);
        Assert.Empty(AlbumUndo.UndoableSongs(library.AllTracks)); // nothing left to undo
        Assert.True(File.Exists(persistence.GetArtworkPath(deluxeId)));

        // A rescan that re-reads the files keeps them apart.
        foreach (var t in library.AllTracks) t.LastModified = default;
        await library.ScanAsync(new[] { Music });
        Assert.Equal(2, library.Albums.Count);
    }

    [Fact]
    public async Task Undo_OfASongWithoutAnAlbumArtistTag_TakesTheWrittenTagOutAgain()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "One", "Singer");          // no album artist tag
        WriteMp3(Path.Combine("B", "01.mp3"), "Two", "Other", "Other");
        var (library, _) = await NewLibraryAsync(2);
        Assert.False(Song(library, a).AlbumArtistFromTag);

        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Two", "Other");
        using (var f = Read(a)) Assert.Equal(new[] { "Other" }, f.Tag.AlbumArtists);

        var undone = await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());

        Assert.Equal(1, undone.Grouped);
        using (var f = Read(a))
        {
            Assert.Equal("One", f.Tag.Album);
            Assert.Empty(f.Tag.AlbumArtists);
        }
        var song = Song(library, a);
        Assert.Equal(("One", "Singer"), (song.Album, song.AlbumArtist));
        Assert.False(song.AlbumArtistFromTag);
        Assert.Equal(Track.ComputeAlbumId("Singer", "One"), song.AlbumId);
    }

    [Fact]
    public async Task GroupedTwice_UndoReturnsToTheStateBeforeTheFirstGrouping()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X");
        var b = WriteMp3(Path.Combine("B", "01.mp3"), "B", "X", "X");
        var c = WriteMp3(Path.Combine("C", "01.mp3"), "C", "X", "X");
        var (library, _) = await NewLibraryAsync(3);

        await library.GroupIntoAlbumAsync(new[] { Song(library, a), Song(library, b) }, "AB", "X");
        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "ABC", "X");
        Assert.Single(library.Albums);

        var undone = await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());

        Assert.Equal(3, undone.Grouped);
        Assert.Equal(new[] { "A", "B", "C" }, library.Albums.Select(x => x.Name).OrderBy(n => n));
        using (var f = Read(a)) Assert.Equal("A", f.Tag.Album);
        using (var f = Read(b)) Assert.Equal("B", f.Tag.Album);
    }

    [Fact]
    public async Task GroupedBackOntoTheOriginalAlbum_LeavesNothingToUndo()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X");
        WriteMp3(Path.Combine("B", "01.mp3"), "B", "X", "X");
        var (library, _) = await NewLibraryAsync(2);

        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "B", "X");
        Assert.NotNull(Song(library, a).GroupUndo);

        // Grouped back by hand: the song has its own tags again, nothing is left to put back.
        await library.GroupIntoAlbumAsync(new[] { Song(library, a) }, "A", "X");

        Assert.Null(Song(library, a).GroupUndo);
        Assert.Empty(AlbumUndo.UndoableSongs(library.AllTracks));
    }

    // ── Move to Album ──

    [Fact]
    public async Task Move_ThenUndo_RestoresTheNumberingYearAndReleaseIds()
    {
        WriteMp3(Path.Combine("Target", "01.mp3"), "Target", "Artist", "Artist", track: 1, trackCount: 3, year: 2004,
            more: f => { f.Tag.MusicBrainzReleaseId = "rel-target"; f.Tag.AlbumSort = "Target, The"; });
        WriteMp3(Path.Combine("Target", "02.mp3"), "Target", "Artist", "Artist", track: 2, trackCount: 3, year: 2004);
        var song = WriteMp3(Path.Combine("Old", "05.mp3"), "Old", "Guest", "Guest", track: 5, trackCount: 9, disc: 2, discCount: 2,
            year: 1999, more: f => { f.Tag.MusicBrainzReleaseId = "rel-old"; });
        var undated = WriteMp3(Path.Combine("Old", "06.mp3"), "Old", "Guest", "Guest", track: 6, trackCount: 9);
        var (library, _) = await NewLibraryAsync(4);

        await library.MoveToAlbumAsync(new[] { Song(library, song), Song(library, undated) }, "Target", "Artist");
        using (var f = Read(undated)) Assert.Equal(2004u, f.Tag.Year);
        var target = Assert.Single(library.Albums);

        var undone = await library.UndoAlbumGroupingAsync(target.Tracks.ToList());

        Assert.Equal(2, undone.Grouped);
        var old = Assert.Single(library.Albums, a => a.Name == "Old");
        Assert.Equal(2, old.Tracks.Count);
        var t = Song(library, song);
        Assert.Equal((5, 9, 2, 2, 1999), (t.TrackNumber, t.TrackCount, t.DiscNumber, t.DiscCount, t.Year));
        using (var f = Read(song))
        {
            Assert.Equal(("Old", 5u, 9u, 2u, 2u, 1999u), (f.Tag.Album, f.Tag.Track, f.Tag.TrackCount, f.Tag.Disc, f.Tag.DiscCount, f.Tag.Year));
            Assert.Equal(new[] { "Guest" }, f.Tag.AlbumArtists);
            Assert.Equal("rel-old", f.Tag.MusicBrainzReleaseId);
            Assert.True(string.IsNullOrEmpty(f.Tag.AlbumSort)); // the target album's sort name is gone
        }
        using (var f = Read(undated))
        {
            Assert.Equal(6u, f.Tag.Track);
            Assert.Equal(0u, f.Tag.Year); // it had no year; the move gave it the album's
        }
        Assert.Equal(0, Song(library, undated).Year);
    }

    // ── When the record no longer applies ──

    [Fact]
    public async Task EditedAfterGrouping_UndoIsNotOffered_AndChangesNothing()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X");
        WriteMp3(Path.Combine("B", "01.mp3"), "B", "X", "X");
        var (library, _) = await NewLibraryAsync(2);
        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "B", "X");
        var song = Song(library, a);
        Assert.True(AlbumUndo.Applies(song));

        // Edit Info: the user files the song under another album afterwards.
        song.Album = "Renamed";
        song.AlbumId = Track.ComputeAlbumId(song.AlbumArtist, song.Album);
        Assert.True(new MetadataService().WriteTrackMetadata(song));

        Assert.False(AlbumUndo.Applies(song));
        Assert.Empty(AlbumUndo.UndoableSongs(library.AllTracks));
        var undone = await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());
        Assert.Equal(0, undone.Grouped);
        Assert.Equal("Renamed", song.Album);
        using (var f = Read(a)) Assert.Equal("Renamed", f.Tag.Album);

        // The next rebuild drops the stale record for good.
        await library.SaveAsync();
        var reloaded = await RestartAsync(new PersistenceService(Path.Combine(_root, "data")));
        Assert.Null(reloaded.AllTracks.Single(t => t.FilePath == a).GroupUndo);
    }

    [Fact]
    public async Task ARescanThatFindsOtherTags_DropsTheRecord_AndOneThatFindsTheSameKeepsIt()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X");
        var b = WriteMp3(Path.Combine("B", "01.mp3"), "B", "X", "X");
        var (library, _) = await NewLibraryAsync(2);
        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "B", "X");

        // Every tag save makes the folder watcher re-import the file as a new Track: the
        // grouped file reads back as grouped, and the record must come along.
        foreach (var t in library.AllTracks) t.LastModified = default;
        await library.ScanAsync(new[] { Music });
        Assert.True(AlbumUndo.Applies(Song(library, a)));

        // Re-tagged in another app: the file's new album wins and the record is gone.
        using (var f = TagLib.File.Create(a)) { f.Tag.Album = "Elsewhere"; f.Save(); }
        foreach (var t in library.AllTracks) t.LastModified = default;
        await library.ScanAsync(new[] { Music });
        Assert.Null(Song(library, a).GroupUndo);
        Assert.Null(Song(library, b).GroupUndo); // b was on B already: never changed
    }

    [Fact]
    public async Task TheRecord_SurvivesARestart()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X", track: 4, trackCount: 7);
        WriteMp3(Path.Combine("B", "01.mp3"), "B", "X", "X", track: 1);
        var (library, persistence) = await NewLibraryAsync(2);
        await library.MoveToAlbumAsync(new[] { Song(library, a) }, "B", "X");

        var reloaded = await RestartAsync(persistence);
        var song = reloaded.AllTracks.Single(t => t.FilePath == a);
        Assert.True(AlbumUndo.Applies(song));

        var undone = await reloaded.UndoAlbumGroupingAsync(reloaded.AllTracks.ToList());

        Assert.Equal(1, undone.Grouped);
        Assert.Equal(("A", 4, 7), (song.Album, song.TrackNumber, song.TrackCount));
        using (var f = Read(a)) Assert.Equal(("A", 4u, 7u), (f.Tag.Album, f.Tag.Track, f.Tag.TrackCount));
    }

    [Fact]
    public async Task OldLibraryFile_WithoutTheRecord_Loads_AndOffersNoUndo()
    {
        WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X");
        var (library, persistence) = await NewLibraryAsync(1);
        await library.SaveAsync();
        var json = await File.ReadAllTextAsync(Path.Combine(_root, "data", "library.json"));
        Assert.DoesNotContain("groupUndo", json); // nothing written while there is no record

        var reloaded = await RestartAsync(persistence);
        Assert.Empty(AlbumUndo.UndoableSongs(reloaded.AllTracks));
    }

    [Fact]
    public async Task Undo_AFileThatCannotBeWritten_StillMovesBack_AndIsReported()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "A", "X", "X");
        WriteMp3(Path.Combine("B", "01.mp3"), "B", "X", "X");
        var (library, _) = await NewLibraryAsync(2);
        await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "B", "X");
        File.SetAttributes(a, FileAttributes.ReadOnly);

        var undone = await library.UndoAlbumGroupingAsync(library.AllTracks.ToList());

        Assert.Equal(1, undone.Grouped);
        Assert.Equal(new[] { "01.mp3" }, undone.UnwrittenFiles);
        var song = Song(library, a);
        Assert.Equal("A", song.Album);
        Assert.Null(song.GroupUndo);
        Assert.True(song.AlbumGroupingUnwritten); // written by the next pass, like a grouping
        using (var f = Read(a)) Assert.Equal("B", f.Tag.Album);

        File.SetAttributes(a, FileAttributes.Normal);
        await library.SaveAlbumGroupingToFilesAsync();
        using (var f = Read(a)) Assert.Equal("A", f.Tag.Album);
    }

    // ── Album menu ──

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static (LibraryAlbumsViewModel Vm, FakeLibraryService Library) AlbumsVm()
    {
        var lib = new FakeLibraryService();
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var vm = new LibraryAlbumsViewModel(lib, player, new SidebarViewModel(persistence, lib),
            new SettingsViewModel(persistence, lib, new NoOpPlayHistoryService()));
        return (vm, lib);
    }

    private static Track Grouped(string title, string fromAlbum, string toAlbum = "Album")
    {
        var t = new Track { Title = title, Album = toAlbum, AlbumArtist = "Artist", AlbumArtistFromTag = true, FilePath = TestPaths.Primary("Music", title + ".mp3") };
        t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
        var before = AlbumUndo.Capture(t) with { Album = fromAlbum };
        t.GroupUndo = new AlbumGroupUndo
        {
            Before = before,
            After = AlbumUndo.Capture(t),
            AlbumId = Track.ComputeAlbumId("Artist", fromAlbum),
            AlbumArtistFromTag = true,
        };
        return t;
    }

    [AvaloniaFact]
    public async Task UndoCommand_AsksWithTheSongCountAndAlbums_ThenUndoes()
    {
        var (vm, lib) = AlbumsVm();
        var plain = new Track { Title = "plain", Album = "Album", AlbumArtist = "Artist" };
        plain.AlbumId = Track.ComputeAlbumId("Artist", "Album");
        var g1 = Grouped("g1", "Album (Deluxe)");
        var g2 = Grouped("g2", "Album (Deluxe)");
        var album = new Album { Id = g1.AlbumId, Name = "Album", Artist = "Artist", Tracks = new() { plain, g1, g2 } };
        var plainAlbum = new Album { Id = Guid.NewGuid(), Name = "Other", Tracks = new() { plain } };
        Assert.True(vm.CanUndoGrouping(album));
        Assert.False(vm.CanUndoGrouping(plainAlbum));

        Views.ConfirmationRequest? asked = null;
        vm.ShowGroupNotice = r => { asked = r; return Task.FromResult(new Views.ConfirmationResult(true, false)); };

        await vm.UndoGroupingCommand.ExecuteAsync(album);

        Assert.NotNull(asked);
        Assert.Equal(Loc.T("GroupAlbums.UndoConfirm", 2), asked!.Message);
        Assert.Equal(new[] { "Album (Deluxe) — Artist" }, asked.Details);
        Assert.False(asked.InfoOnly);
        Assert.Equal(new[] { g1, g2 }, Assert.Single(lib.UndoGroupCalls));
    }

    [AvaloniaFact]
    public async Task UndoCommand_Cancelled_UndoesNothing()
    {
        var (vm, lib) = AlbumsVm();
        var g1 = Grouped("g1", "Old");
        var album = new Album { Id = g1.AlbumId, Name = "Album", Tracks = new() { g1 } };
        vm.ShowGroupNotice = _ => Task.FromResult(new Views.ConfirmationResult(false, false));

        await vm.UndoGroupingCommand.ExecuteAsync(album);

        Assert.Empty(lib.UndoGroupCalls);
    }

    [AvaloniaFact]
    public void AlbumMenu_UndoGrouping_ShownOnlyWithACommand()
    {
        var host = new Avalonia.Controls.UserControl();
        var builder = new AlbumContextMenuBuilder();
        builder.Build("Remove from Library", host, v2: true);
        var album = new Album { Id = Guid.NewGuid(), Name = "A" };

        builder.BindUndoGroupAlbums(album, null);
        Assert.False(builder.UndoGroupAlbums.IsVisible);

        var (vm, _) = AlbumsVm();
        builder.BindUndoGroupAlbums(album, vm.UndoGroupingCommand);
        Assert.True(builder.UndoGroupAlbums.IsVisible);
        Assert.Equal(Loc.T("GroupAlbums.Undo"), builder.UndoGroupAlbums.Header);
    }

    [Fact]
    public void GroupHint_SaysTheFilesChange_AndHowToUndo()
    {
        var hint = Loc.T("GroupAlbums.Hint", 2, 25);
        Assert.Contains("one album", hint);
        Assert.Contains(Loc.T("GroupAlbums.Undo"), hint);
    }
}
