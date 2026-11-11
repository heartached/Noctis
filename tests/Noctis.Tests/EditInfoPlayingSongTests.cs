using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (NJZ): "why does the metadata edit itself back when i edit it" — answered "you can't
/// play it while editing it". Every tag save rewrites the file, the folder watcher re-imports it
/// and the library gets a NEW Track for the song; the queue keeps the one it had. So the song
/// that is playing is, after its first save, a stale copy: the player bar and lyrics page kept
/// the old tags, and Edit Info opened from the player bar loaded those old tags and wrote them
/// all back into the file, undoing the edit made from the Songs list.
/// </summary>
[Collection("Tag save replace")] // registers an OpenFileHolders holder, like TagSaveHeldFileTests
public class EditInfoPlayingSongTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "editplaying-" + Guid.NewGuid().ToString("N"));
    private readonly string _music;

    public EditInfoPlayingSongTests()
    {
        _music = Path.Combine(_root, "music");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteMp3(string title, string album)
    {
        var path = Path.Combine(_music, "Album", "01 - Song.mp3");
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = title; f.Tag.Performers = new[] { "Tester" }; f.Tag.AlbumArtists = new[] { "Tester" };
            f.Tag.Album = album;
            f.Save();
        }
        return path;
    }

    private async Task<LibraryService> LibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { _music } });
        var lib = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await lib.ScanAsync(new[] { _music });
        return lib;
    }

    /// <summary>Edit Info the way MetadataHelper opens it on <paramref name="opened"/>: change, Save.</summary>
    private static async Task<MetadataViewModel> EditAsync(Track opened, ILibraryService lib, Action<MetadataViewModel> change)
    {
        var edited = MetadataHelper.EditedInstance(opened, lib);
        var vm = new MetadataViewModel(edited, new MetadataService(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        vm.ChangesSaved += (_, _) => MetadataHelper.MirrorSavedEdit(opened, edited);
        await vm.InitializeAsync();
        change(vm);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        return vm;
    }

    private static string FileAlbum(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.Album;
    }

    [Fact]
    public async Task EditFromThePlayerBar_AfterAnEditFromTheSongsList_KeepsThatEdit()
    {
        var path = WriteMp3("Song", "Old Album");
        var lib = await LibraryAsync();
        var id = lib.Tracks.Single().Id;
        var playing = lib.GetTrackById(id)!; // what the queue holds

        // Edit from the Songs list, then the folder watcher re-imports the rewritten file.
        await EditAsync(lib.GetTrackById(id)!, lib, vm => vm.Album = "First");
        await lib.ImportDiscoveredFilesAsync(new[] { path });
        Assert.NotSame(playing, lib.GetTrackById(id)); // the queue holds a pre-reload copy now

        await EditAsync(lib.GetTrackById(id)!, lib, vm => vm.Album = "Second");
        await lib.ImportDiscoveredFilesAsync(new[] { path });
        Assert.Equal("Second", FileAlbum(path));

        // Edit Info from the player bar opens on the queue's copy: it must show the song as it
        // is now, and saving an unrelated field must not put the old album back.
        var vm3 = await EditAsync(playing, lib, vm =>
        {
            Assert.Equal("Second", vm.Album);
            vm.Comment = "a note";
        });
        await lib.ImportDiscoveredFilesAsync(new[] { path });

        Assert.Equal("Second", FileAlbum(path));
        Assert.Equal("Second", lib.GetTrackById(id)!.Album);
        Assert.Equal("a note", lib.GetTrackById(id)!.Comment);
        // The player's copy shows what was saved (player bar, lyrics page, the next Edit Info).
        Assert.Equal("Second", playing.Album);
        Assert.Equal("a note", playing.Comment);
    }

    [Fact]
    public async Task EditOfTheQueuesCopy_WithoutAWatcher_StillReachesTheLibrary()
    {
        // A song outside the watched folders (or the watcher off): no re-import follows the save,
        // so an edit made on the queue's copy never reached the library's row and the Songs list
        // kept showing the old tags.
        var path = WriteMp3("Song", "Old Album");
        var lib = await LibraryAsync();
        var id = lib.Tracks.Single().Id;
        var playing = lib.GetTrackById(id)!;
        // Any rewrite of the file (another tag editor, a rating or ReplayGain write) re-imports it.
        using (var f = TagLib.File.Create(path)) { f.Tag.Comment = "elsewhere"; f.Save(); }
        await lib.ImportDiscoveredFilesAsync(new[] { path });
        Assert.NotSame(playing, lib.GetTrackById(id));

        await EditAsync(playing, lib, vm => vm.Title = "New Title");

        Assert.Equal("New Title", lib.GetTrackById(id)!.Title);
        Assert.Equal("New Title", playing.Title);
    }

    [AvaloniaFact]
    public void PlayerBar_ShowsTheTags_TheRescanOfThePlayingFileRead()
    {
        var lib = new FakeLibraryService();
        var id = Guid.NewGuid();
        var playing = new Track { Id = id, Title = "Song", Artist = "Tester", Album = "Old Album", FilePath = "/m/song.mp3" };
        lib.TrackList.Add(playing);
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());
        player.CurrentTrack = playing;
        var raised = 0;
        playing.PropertyChanged += (_, e) => { if (string.IsNullOrEmpty(e.PropertyName)) raised++; };

        // The watcher's re-import of the saved file: a new Track for the same song.
        lib.TrackList[0] = new Track { Id = id, Title = "New Title", Artist = "Tester", Album = "New Album", FilePath = "/m/song.mp3" };
        lib.RaiseLibraryUpdated();
        Dispatcher.UIThread.RunJobs();

        Assert.Same(playing, player.CurrentTrack); // the same song keeps playing, no track change
        Assert.Equal("New Title", player.CurrentTrack!.Title);
        Assert.Equal("New Album", player.CurrentTrack.Album);
        Assert.True(raised > 0);
    }

    /// <summary>The file the player has open (shared for read only, as a reader that does not
    /// share delete holds it): the save has the player let go and reopen it, the edit reaches the
    /// file, and the watcher's re-read of it keeps the edit.</summary>
    [Fact]
    public async Task EditOfThePlayingFile_HeldOpenForRead_StaysAfterTheWatcherReReadsIt()
    {
        var path = WriteMp3("Song", "Old Album");
        var lib = await LibraryAsync();
        var id = lib.Tracks.Single().Id;
        using var player = new ReleasingHolder(path);
        OpenFileHolders.Register(player);
        try
        {
            await EditAsync(lib.GetTrackById(id)!, lib, vm => vm.Album = "While Playing");
        }
        finally
        {
            OpenFileHolders.Unregister(player);
        }
        await lib.ImportDiscoveredFilesAsync(new[] { path });

        Assert.Equal("While Playing", FileAlbum(path));
        Assert.Equal("While Playing", lib.GetTrackById(id)!.Album);
        if (OperatingSystem.IsWindows()) Assert.True(player.Releases >= 1);
        Assert.NotNull(player.Handle); // playing again
    }

    private sealed class ReleasingHolder : IOpenFileHolder, IDisposable
    {
        private readonly string _path;
        public FileStream? Handle;
        public int Releases;

        public ReleasingHolder(string path)
        {
            _path = path;
            Handle = Open();
        }

        private FileStream Open() => new(_path, FileMode.Open, FileAccess.Read, FileShare.Read);

        public Action? Release(string path)
        {
            if (Handle == null || !string.Equals(Path.GetFullPath(path), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
                return null;
            Releases++;
            Handle.Dispose();
            Handle = null;
            return () => Handle = Open();
        }

        public void Dispose() => Handle?.Dispose();
    }
}
