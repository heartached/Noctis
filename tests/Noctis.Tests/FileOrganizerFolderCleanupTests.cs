using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #235: Organize Files moved the tracks but left the old album folders behind
/// (empty, or holding only cover.jpg), and Undo left the folders Organize had created.
/// The cleanup only looked at each track's own folder, only when it was completely
/// empty, and never at the parent artist folder.
/// These tests pin the cleanup rules, above all the ones that must NOT delete anything.
/// All paths live in a fresh temp directory; the OS trash is replaced by a fake.
/// </summary>
public sealed class FileOrganizerFolderCleanupTests : IDisposable
{
    private readonly TestPersistenceService _persistence = new();
    private readonly string _root;
    private readonly List<string> _trashed = new();
    private bool _trashFails;
    private bool _trashDeclined;
    private HashSet<string> _protected = new(StringComparer.OrdinalIgnoreCase);

    public FileOrganizerFolderCleanupTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "organize-" + Guid.NewGuid().ToString("N"), "Music");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        _persistence.Dispose();
        try
        {
            var parent = Path.GetDirectoryName(_root)!;
            foreach (var f in Directory.EnumerateFiles(parent, "*", SearchOption.AllDirectories))
                File.SetAttributes(f, FileAttributes.Normal);
            foreach (var d in Directory.EnumerateDirectories(parent, "*", SearchOption.AllDirectories))
                new DirectoryInfo(d).Attributes = FileAttributes.Directory;
            Directory.Delete(parent, recursive: true);
        }
        catch { /* best effort */ }
    }

    private FileOrganizerService CreateService()
    {
        var service = new FileOrganizerService(new FakeLibraryService(), _persistence)
        {
            ProtectedRootsProvider = () => Task.FromResult(_protected),
        };
        service.TrashDirectory = (string path, out bool declined) =>
        {
            declined = _trashDeclined;
            if (_trashFails || _trashDeclined) return false;
            _trashed.Add(path);
            Directory.Delete(path, recursive: true);
            return true;
        };
        return service;
    }

    private string P(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    private static string Write(string path, int bytes = 16)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private static OrganizeMove Move(string from, string to) => new(Guid.NewGuid(), from, to, OrganizeAction.Move);

    private static Task<OrganizeResult> Apply(FileOrganizerService s, params OrganizeMove[] moves)
        => s.ApplyAsync(moves, TestContext.Current.CancellationToken);

    // ── After Organize: the folders the tracks left ──────────────────────

    [Fact]
    public async Task Organize_OldAlbumWithOnlyCover_CoverFollowsTrack_OldAlbumAndArtistFoldersRemoved()
    {
        var from1 = Write(P("Old Artist", "Old Album", "01.flac"));
        var from2 = Write(P("Old Artist", "Old Album", "02.flac"));
        Write(P("Old Artist", "Old Album", "cover.jpg"), 2000);
        var to1 = P("Artist", "Album", "01 One.flac");
        var to2 = P("Artist", "Album", "02 Two.flac");

        var result = await Apply(CreateService(), Move(from1, to1), Move(from2, to2));

        Assert.Equal(2, result.Moved);
        Assert.True(File.Exists(to1));
        Assert.True(File.Exists(to2));
        // Folder art is read for tracks without embedded art, so it travels with the album.
        Assert.True(File.Exists(P("Artist", "Album", "cover.jpg")));
        Assert.False(Directory.Exists(P("Old Artist", "Old Album")));
        Assert.False(Directory.Exists(P("Old Artist")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Organize_LeftoverNonArtImageAndOsJunk_UnderOneMb_FolderTrashed()
    {
        var from = Write(P("Old", "Album", "01.flac"));
        Write(P("Old", "Album", "back.jpg"), 4000);
        Write(P("Old", "Album", "Thumbs.db"));
        Write(P("Old", "Album", "desktop.ini"));
        Write(P("Old", "Album", ".DS_Store"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.False(Directory.Exists(P("Old", "Album")));
        Assert.False(Directory.Exists(P("Old")));
        Assert.Contains(P("Old", "Album"), _trashed, StringComparer.OrdinalIgnoreCase);
        Assert.True(File.Exists(P("A", "B", "01.flac")));
    }

    [Fact]
    public async Task Organize_LeftoverImagesOverOneMb_FolderKept()
    {
        var from = Write(P("Old", "Album", "01.flac"));
        Write(P("Old", "Album", "scan1.jpg"), 600 * 1024);
        Write(P("Old", "Album", "scan2.png"), 600 * 1024);

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.True(File.Exists(P("Old", "Album", "scan1.jpg")));
        Assert.True(File.Exists(P("Old", "Album", "scan2.png")));
        Assert.Empty(_trashed);
    }

    [Theory]
    [InlineData("album.cue")]
    [InlineData("playlist.m3u8")]
    [InlineData("info.nfo")]
    [InlineData("rip.log")]
    [InlineData("notes.txt")]
    [InlineData("booklet.pdf")]
    public async Task Organize_LeftoverUserFile_FolderAndFileKept(string leftover)
    {
        var from = Write(P("Old", "Album", "01.flac"));
        Write(P("Old", "Album", leftover));
        Write(P("Old", "Album", "back.jpg"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.True(File.Exists(P("Old", "Album", leftover)));
        Assert.True(File.Exists(P("Old", "Album", "back.jpg")));
        Assert.Empty(_trashed);
    }

    [Theory]
    [InlineData(".lrc")]
    [InlineData(".elrc")]
    [InlineData(".ttml")]
    [InlineData(".lyricsfile")]
    public async Task Organize_LyricsOfAnotherSong_NeverDeleted(string ext)
    {
        var from = Write(P("Old", "Album", "01.flac"));
        var orphan = Write(P("Old", "Album", "some other song" + ext));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.True(File.Exists(orphan));
        Assert.Empty(_trashed);
    }

    [Theory]
    [InlineData(".lrc")]
    [InlineData(".elrc")]
    [InlineData(".ttml")]
    [InlineData(".lyricsfile")]
    [InlineData(".txt")]
    public async Task Organize_SameNameLyrics_FollowTheTrack_AndFolderIsCleaned(string ext)
    {
        var from = Write(P("Old", "Album", "01.flac"));
        Write(P("Old", "Album", "01" + ext));

        await Apply(CreateService(), Move(from, P("A", "B", "01 Song.flac")));

        Assert.True(File.Exists(P("A", "B", "01 Song" + ext)));
        Assert.False(Directory.Exists(P("Old", "Album")));
    }

    [Fact]
    public async Task Organize_FolderStillHoldsAudio_KeptWithItsCover()
    {
        var moved = Write(P("Old", "Album", "01.flac"));
        var stays = Write(P("Old", "Album", "02.mp3"));
        Write(P("Old", "Album", "cover.jpg"));

        await Apply(CreateService(), Move(moved, P("A", "B", "01.flac")));

        Assert.True(File.Exists(stays));
        // The track still there needs the folder art: it does not move away.
        Assert.True(File.Exists(P("Old", "Album", "cover.jpg")));
        Assert.False(File.Exists(P("A", "B", "cover.jpg")));
        Assert.Empty(_trashed);
    }

    [Fact]
    public async Task Organize_AudioInASubfolder_ParentKept()
    {
        var cd1 = Write(P("Old", "Album", "CD1", "01.flac"));
        var cd2 = Write(P("Old", "Album", "CD2", "01.flac"));
        Write(P("Old", "Album", "cover.jpg"));

        await Apply(CreateService(), Move(cd1, P("A", "B", "1-01.flac")));

        Assert.False(Directory.Exists(P("Old", "Album", "CD1")));
        Assert.True(File.Exists(cd2));
        Assert.True(File.Exists(P("Old", "Album", "cover.jpg")));
    }

    [Fact]
    public async Task Organize_FolderArtAlreadyAtTarget_NotOverwritten()
    {
        var from = Write(P("Old", "Album", "01.flac"));
        Write(P("Old", "Album", "cover.jpg"), 100);
        Write(P("A", "B", "cover.jpg"), 300);

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.Equal(300, new FileInfo(P("A", "B", "cover.jpg")).Length);
        // The duplicate cover (under 1 MB) goes to the trash with its folder.
        Assert.False(Directory.Exists(P("Old", "Album")));
        Assert.Contains(P("Old", "Album"), _trashed, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Organize_TracksSplitOverTwoTargets_CoverGoesWithTheFirst_NeverTrashed()
    {
        var a = Write(P("Mixed", "01.flac"));
        var b = Write(P("Mixed", "02.flac"));
        Write(P("Mixed", "cover.jpg"), 2000);

        await Apply(CreateService(), Move(a, P("X", "1", "01.flac")), Move(b, P("Y", "2", "02.flac")));

        Assert.True(File.Exists(P("X", "1", "cover.jpg")));
        Assert.False(Directory.Exists(P("Mixed")));
        Assert.Empty(_trashed);
    }

    [Fact]
    public async Task Undo_OfTwoFoldersMergedIntoOne_CoverReturnsWithMostTracks_NotTrashed()
    {
        // Live case: "Old Album" (2 tracks + cover.jpg) and "Dupes" (1 track) organized
        // into one album folder; undo sends its tracks back to two folders.
        var a = Write(P("Artist", "Album", "01.flac"));
        var b = Write(P("Artist", "Album", "02.flac"));
        var c = Write(P("Artist", "Album", "01 (2).flac"));
        Write(P("Artist", "Album", "cover.jpg"), 2000);

        await Apply(CreateService(),
            Move(c, P("Dupes", "copy.flac")),
            Move(a, P("Old Artist", "Old Album", "01.flac")),
            Move(b, P("Old Artist", "Old Album", "02.flac")));

        Assert.True(File.Exists(P("Old Artist", "Old Album", "cover.jpg")));
        Assert.False(File.Exists(P("Dupes", "cover.jpg")));
        Assert.False(Directory.Exists(P("Artist")));
        Assert.Empty(_trashed);
    }

    [Fact]
    public async Task Organize_LooseFilesInLibraryRoot_RootNeverRemoved()
    {
        _protected.Add(_root);
        var from = Write(P("01.flac"));
        Write(P("cover.jpg"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.True(Directory.Exists(_root));
        Assert.True(File.Exists(P("A", "B", "01.flac")));
        // The root's own cover is not an album folder's art: it stays put.
        Assert.True(File.Exists(P("cover.jpg")));
    }

    [Fact]
    public async Task Organize_ProtectedFolder_NeverRemovedEvenWhenEmpty()
    {
        var music2 = P("Music2");
        _protected.Add(music2);
        var from = Write(Path.Combine(music2, "Album", "01.flac"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.False(Directory.Exists(Path.Combine(music2, "Album")));
        Assert.True(Directory.Exists(music2));
    }

    [Fact]
    public async Task Organize_NeverClimbsAboveTheFolderTheMoveStayedIn()
    {
        // Source and target share _root/Lib: Lib (and anything above) is never touched,
        // even though it is not a configured root.
        var from = Write(P("Lib", "Old", "01.flac"));

        await Apply(CreateService(), Move(from, P("Lib", "New", "01.flac")));

        Assert.False(Directory.Exists(P("Lib", "Old")));
        Assert.True(Directory.Exists(P("Lib")));
        Assert.True(Directory.Exists(_root));
    }

    [Fact]
    public async Task Organize_UnrelatedEmptyFolders_NotSwept()
    {
        Directory.CreateDirectory(P("Old", "Empty Sibling"));
        Directory.CreateDirectory(P("Elsewhere"));
        var from = Write(P("Old", "Album", "01.flac"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.False(Directory.Exists(P("Old", "Album")));
        Assert.True(Directory.Exists(P("Old", "Empty Sibling")));
        Assert.True(Directory.Exists(P("Elsewhere")));
    }

    // ── Trash failures, read-only files, long paths ──────────────────────

    [Fact]
    public async Task Organize_TrashUnavailable_SmallLeftoversDeletedPermanently()
    {
        _trashFails = true;
        var from = Write(P("Old", "Album", "01.flac"));
        var back = Write(P("Old", "Album", "back.jpg"));
        File.SetAttributes(back, FileAttributes.ReadOnly);

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.False(Directory.Exists(P("Old", "Album")));
        Assert.False(Directory.Exists(P("Old")));
    }

    [Fact]
    public async Task Organize_ReadOnlyEmptyFolder_Removed()
    {
        var from = Write(P("Old", "Album", "01.flac"));
        new DirectoryInfo(P("Old", "Album")).Attributes |= FileAttributes.ReadOnly;

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.False(Directory.Exists(P("Old")));
    }

    [Fact]
    public async Task Organize_UserDeclinedPermanentDelete_FolderKept()
    {
        _trashDeclined = true;
        var from = Write(P("Old", "Album", "01.flac"));
        Write(P("Old", "Album", "back.jpg"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.True(File.Exists(P("Old", "Album", "back.jpg")));
    }

    [Fact]
    public async Task Organize_EmptyFolders_RemovedEvenWhenTrashUnavailable()
    {
        _trashFails = true;
        var from = Write(P("Old", "Album", "01.flac"));

        await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.False(Directory.Exists(P("Old")));
    }

    [Fact]
    public async Task Organize_LongPaths_CleanedUp()
    {
        var deep = P(new string('a', 120), new string('b', 120), new string('c', 120));
        var from = Write(Path.Combine(deep, "01.flac"));
        Write(Path.Combine(deep, "cover.jpg"));
        Assert.True(from.Length > 260);
        _trashFails = true; // the shell's recycle call can't take long paths

        var result = await Apply(CreateService(), Move(from, P("A", "B", "01.flac")));

        Assert.Equal(1, result.Moved);
        Assert.True(File.Exists(P("A", "B", "cover.jpg")));
        Assert.False(Directory.Exists(P(new string('a', 120))));
    }

    [Fact]
    public async Task Organize_CleanupFailure_KeepsUndoLogAndMoveResult()
    {
        var service = CreateService();
        service.ProtectedRootsProvider = () => throw new InvalidOperationException("boom");
        var from = Write(P("Old", "Album", "01.flac"));

        var result = await Apply(service, Move(from, P("A", "B", "01.flac")));

        Assert.Equal(1, result.Moved);
        Assert.Equal(0, result.Failed);
        Assert.True(service.CanUndo);
        Assert.True(Directory.Exists(P("Old", "Album"))); // fail closed: nothing removed
    }

    // ── After Undo: the folders Organize created ─────────────────────────

    [Fact]
    public async Task Undo_RemovesTheFoldersOrganizeCreated_AndBringsTheCoverBack()
    {
        var service = CreateService();
        var from1 = Write(P("Old Artist", "Old Album", "01.flac"));
        var from2 = Write(P("Old Artist", "Old Album", "02.flac"));
        Write(P("Old Artist", "Old Album", "cover.jpg"));
        await Apply(service, Move(from1, P("Artist", "Album", "01.flac")), Move(from2, P("Artist", "Album", "02.flac")));

        var undo = await service.UndoLastAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, undo.Moved);
        Assert.True(File.Exists(from1));
        Assert.True(File.Exists(from2));
        Assert.True(File.Exists(P("Old Artist", "Old Album", "cover.jpg")));
        Assert.False(Directory.Exists(P("Artist", "Album")));
        Assert.False(Directory.Exists(P("Artist")));
        Assert.False(service.CanUndo);
    }

    [Fact]
    public async Task Undo_CreatedFolderGainedOtherFiles_Kept()
    {
        var service = CreateService();
        var from = Write(P("Old", "01.flac"));
        await Apply(service, Move(from, P("Artist", "Album", "01.flac")));
        var newSong = Write(P("Artist", "Album", "bonus.flac"));
        var lyrics = Write(P("Artist", "Other", "x.lrc"));

        await service.UndoLastAsync(TestContext.Current.CancellationToken);

        Assert.True(File.Exists(from));
        Assert.True(File.Exists(newSong));
        Assert.True(File.Exists(lyrics));
    }
}
