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
/// GitHub #108: files added to the library one by one from outside every music folder (a
/// drop with "Import dropped files" off, a YouTube download or a conversion saved
/// elsewhere) must stay in the library through a full scan. The scan used to publish only
/// what its folder walk found, so the next "Scan library" or startup scan removed them —
/// and with them their place in every playlist.
/// </summary>
[Collection("MetadataServiceStatics")]
public class AddedFilesScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    private readonly string _music;
    private readonly string _outside;
    private readonly RecordingAudit _audit = new();

    public AddedFilesScanTests()
    {
        _music = Path.Combine(_root, "music");
        _outside = Path.Combine(_root, "Downloads", "hftfviceusgskinconcept");
        Directory.CreateDirectory(Path.Combine(_music, "Album"));
        Directory.CreateDirectory(_outside);
        WriteMp3(Path.Combine(_music, "Album", "01 - First.mp3"), "First");
        WriteMp3(Path.Combine(_music, "Album", "02 - Second.mp3"), "Second");
        WriteMp3(Path.Combine(_outside, "loose one.mp3"), "Loose One");
        WriteMp3(Path.Combine(_outside, "loose two.mp3"), "Loose Two");
    }

    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private string Loose(int n) => Path.Combine(_outside, n == 1 ? "loose one.mp3" : "loose two.mp3");
    private string DataDir => Path.Combine(_root, "data");

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
        // A rescan notices a change by size + mtime; make sure the clock moved.
        File.SetLastWriteTimeUtc(path, File.GetLastWriteTimeUtc(path).AddMinutes(1));
    }

    private LibraryService NewLibrary(PersistenceService persistence) =>
        new(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), _audit);

    private async Task<(LibraryService Library, PersistenceService Persistence)> LibraryAsync(params string[] musicFolders)
    {
        var persistence = new PersistenceService(DataDir);
        var settings = new AppSettings();
        settings.MusicFolders.AddRange(musicFolders);
        await persistence.SaveSettingsAsync(settings);
        return (NewLibrary(persistence), persistence);
    }

    private async Task<(LibraryService Library, PersistenceService Persistence)> ScannedLibraryAsync()
    {
        var (library, persistence) = await LibraryAsync(_music);
        await library.ScanAsync(new[] { _music });
        Assert.Equal(2, library.Tracks.Count);
        return (library, persistence);
    }

    private static Track? ByTitle(ILibraryService library, string title)
        => library.Tracks.FirstOrDefault(t => t.Title == title);

    [Fact]
    public async Task FileImportedFromOutsideTheMusicFolders_SurvivesAFullScan()
    {
        var (library, _) = await ScannedLibraryAsync();

        await library.ImportFilesAsync(new[] { Loose(1) });
        Assert.NotNull(ByTitle(library, "Loose One"));

        await library.ScanAsync(new[] { _music });

        Assert.Equal(3, library.Tracks.Count);
        Assert.NotNull(ByTitle(library, "Loose One"));
    }

    [Fact]
    public async Task ImportMarksOnlyTheFilesNoFolderWalkReaches()
    {
        var (library, _) = await ScannedLibraryAsync();

        await library.ImportFilesAsync(new[] { Loose(1), Path.Combine(_music, "Album", "01 - First.mp3") });

        Assert.True(ByTitle(library, "Loose One")!.AddedIndividually);
        Assert.False(ByTitle(library, "First")!.AddedIndividually);
        Assert.True(File.Exists(Loose(1))); // imported where it is: nothing moved or copied
        Assert.Equal(Loose(1), ByTitle(library, "Loose One")!.FilePath);
    }

    /// <summary>The mark lives in library.json, written only when set: an older library.json
    /// (no such field) reads as unmarked, and folder tracks are written exactly as before.</summary>
    [Fact]
    public async Task TheMark_SurvivesARestart_AndOnlyMarkedTracksCarryIt()
    {
        var (library, persistence) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });

        var json = await File.ReadAllTextAsync(Path.Combine(DataDir, "library.json"));
        Assert.Equal(1, json.Split("\"addedIndividually\":true").Length - 1);
        Assert.DoesNotContain("\"addedIndividually\":false", json);

        var restarted = NewLibrary(persistence);
        await restarted.LoadAsync();
        await restarted.BackgroundInit;
        Assert.True(ByTitle(restarted, "Loose One")!.AddedIndividually);
        Assert.False(ByTitle(restarted, "First")!.AddedIndividually);

        await restarted.ScanAsync(new[] { _music });
        Assert.NotNull(ByTitle(restarted, "Loose One"));
    }

    [Fact]
    public async Task FileUnderAnExcludedSubfolder_IsMarkedToo_AndSurvivesAScan()
    {
        var (library, persistence) = await ScannedLibraryAsync();
        var hidden = Path.Combine(_music, "Skipped");
        Directory.CreateDirectory(hidden);
        var file = Path.Combine(hidden, "Kept Anyway.mp3");
        WriteMp3(file, "Kept Anyway");
        var settings = await persistence.LoadSettingsAsync();
        settings.FolderRules.Add(new FolderRule { Path = hidden, Include = false, Enabled = true });
        await persistence.SaveSettingsAsync(settings);

        await library.ImportFilesAsync(new[] { file });
        await library.ScanAsync(new[] { _music });

        Assert.True(ByTitle(library, "Kept Anyway")?.AddedIndividually);
    }

    [Fact]
    public async Task UnchangedAddedFile_ScanIsANoOp_AndKeepsUserState()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });
        var before = ByTitle(library, "Loose One")!;
        before.PlayCount = 9;
        before.IsFavorite = true;
        var dateAdded = before.DateAdded;
        _audit.Events.Clear();

        await library.ScanAsync(new[] { _music });

        Assert.Contains("scan.noop", _audit.Events);
        var after = ByTitle(library, "Loose One")!;
        Assert.Same(before, after);
        Assert.Equal(9, after.PlayCount);
        Assert.True(after.IsFavorite);
        Assert.Equal(dateAdded, after.DateAdded);
    }

    [Fact]
    public async Task RetaggedAddedFile_IsReReadByTheScan_KeepingUserStateAndTheMark()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });
        var before = ByTitle(library, "Loose One")!;
        before.PlayCount = 4;
        before.Rating = 5;

        Retag(Loose(1), "Loose One (Remaster)");
        await library.ScanAsync(new[] { _music });

        Assert.Null(ByTitle(library, "Loose One"));
        var after = ByTitle(library, "Loose One (Remaster)")!;
        Assert.NotSame(before, after);
        Assert.Equal(before.Id, after.Id);
        Assert.Equal(4, after.PlayCount);
        Assert.Equal(5, after.Rating);
        Assert.True(after.AddedIndividually);
        Assert.Equal(3, library.Tracks.Count);

        // Marked still, so the scan after that keeps it too.
        await library.ScanAsync(new[] { _music });
        Assert.NotNull(ByTitle(library, "Loose One (Remaster)"));
    }

    [Fact]
    public async Task DeletedAddedFile_DropsOut()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1), Loose(2) });
        Assert.Equal(4, library.Tracks.Count);

        File.Delete(Loose(1)); // its folder still lists fine: the file is gone
        await library.ScanAsync(new[] { _music });

        Assert.Null(ByTitle(library, "Loose One"));
        Assert.NotNull(ByTitle(library, "Loose Two"));
        Assert.Equal(3, library.Tracks.Count);
    }

    [Fact]
    public async Task AddedFileOnAFolderThatIsNotThere_IsKept_UntilTheFolderComesBack()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });
        ByTitle(library, "Loose One")!.PlayCount = 3;

        // An unplugged drive / offline share looks like this: the whole folder is missing.
        var parked = _outside + " (unplugged)";
        Directory.Move(_outside, parked);
        await library.ScanAsync(new[] { _music });

        Assert.Equal(3, ByTitle(library, "Loose One")?.PlayCount);
        Assert.True(ByTitle(library, "Loose One")!.AddedIndividually);

        Directory.Move(parked, _outside);
        await library.ScanAsync(new[] { _music });
        Assert.Equal(3, ByTitle(library, "Loose One")?.PlayCount);
    }

    [Fact]
    public async Task RemovedAddedTrack_StaysRemovedAfterAScan()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1), Loose(2) });

        await library.RemoveTracksAsync(new[] { ByTitle(library, "Loose One")!.Id });
        await library.ScanAsync(new[] { _music });

        Assert.Null(ByTitle(library, "Loose One"));
        Assert.NotNull(ByTitle(library, "Loose Two"));
        Assert.True(File.Exists(Loose(1))); // "Keep files": the file itself is untouched
    }

    [Fact]
    public async Task SingleTrackRemoval_StaysRemovedToo()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });

        await library.RemoveTrackAsync(ByTitle(library, "Loose One")!.Id);
        await library.ScanAsync(new[] { _music });

        Assert.Null(ByTitle(library, "Loose One"));
        Assert.Equal(2, library.Tracks.Count);
    }

    [Fact]
    public async Task ExcludedAddedFile_IsNotKept()
    {
        var (library, persistence) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });
        var settings = await persistence.LoadSettingsAsync();
        settings.ExcludedFilePaths.Add(Loose(1));
        await persistence.SaveSettingsAsync(settings);

        await library.ScanAsync(new[] { _music });

        Assert.Null(ByTitle(library, "Loose One"));
    }

    [Fact]
    public async Task AddedFileWhoseFolderBecomesAMusicFolder_IsNotDuplicated()
    {
        var (library, persistence) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });
        var id = ByTitle(library, "Loose One")!.Id;

        var settings = await persistence.LoadSettingsAsync();
        settings.MusicFolders.Add(_outside);
        await persistence.SaveSettingsAsync(settings);
        await library.ScanAsync(new[] { _music, _outside });

        Assert.Single(library.Tracks, t => t.Title == "Loose One");
        Assert.Equal(id, ByTitle(library, "Loose One")!.Id);
        Assert.Equal(4, library.Tracks.Count); // + loose two, now under a folder
    }

    [Fact]
    public async Task NoMusicFolders_AScanKeepsTheAddedFiles()
    {
        var (library, _) = await LibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });

        await library.ScanAsync(Array.Empty<string>());

        Assert.Single(library.Tracks);
        Assert.NotNull(ByTitle(library, "Loose One"));
    }

    [Fact]
    public async Task EmptyFolderList_WhileSettingsListFolders_StillKeepsTheFolderTracks()
    {
        // The "never replace a populated library with nothing" guard must not be defeated
        // by the added files alone surviving a scan that walked nothing.
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });

        await library.ScanAsync(Array.Empty<string>());

        Assert.Equal(3, library.Tracks.Count);
    }

    [Fact]
    public async Task RelocatedAddedFile_FollowsItsNewPath()
    {
        var (library, _) = await ScannedLibraryAsync();
        await library.ImportFilesAsync(new[] { Loose(1) });
        ByTitle(library, "Loose One")!.PlayCount = 2;

        var renamed = Path.Combine(_outside, "Loose One (renamed).mp3");
        File.Move(Loose(1), renamed);
        await library.RelocateTracksAsync(new[] { (Loose(1), renamed) });
        await library.ScanAsync(new[] { _music });

        Assert.Equal(renamed, ByTitle(library, "Loose One")?.FilePath);
        Assert.Equal(2, ByTitle(library, "Loose One")?.PlayCount);
    }

    private sealed class RecordingAudit : IAuditTrailService
    {
        public readonly List<string> Events = new();
        public Task AppendAsync(AuditEvent auditEvent, CancellationToken ct = default)
        {
            lock (Events) Events.Add(auditEvent.EventType);
            return Task.CompletedTask;
        }
    }
}
