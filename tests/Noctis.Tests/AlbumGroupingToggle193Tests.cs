using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #193: the automatic album grouping of #178 cannot be switched off, and it grouped
/// "all my songs into the same album". GitHub #190: the grouping lives in Noctis only, so the
/// same files copied to another player are split again.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class AlbumGroupingToggle193Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AlbumGroupingToggle193Tests() => ArtistCredit.ResetToDefaults();

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

    private string Music => Path.Combine(_root, "music");

    private string WriteMp3(string relativePath, string? album, string[] performers, string[]? albumArtists = null)
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
        f.Tag.Performers = performers;
        if (albumArtists != null) f.Tag.AlbumArtists = albumArtists;
        if (album != null) f.Tag.Album = album;
        f.Save();
        return path;
    }

    private async Task<(LibraryService Library, PersistenceService Persistence)> NewLibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { Music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        return (library, persistence);
    }

    // ── Over-grouping ──

    [Fact]
    public async Task LooseSinglesWithoutAnAlbumTag_AreNotGroupedUnderTheirFolderName()
    {
        // Files with no album tag take their folder's name as album (FolderMetadata), so every
        // loose single in "Singles" carried the same album name, and #178 filed the whole folder
        // as one "Singles — Various Artists" album.
        WriteMp3(Path.Combine("Singles", "a.mp3"), null, new[] { "Artist A" });
        WriteMp3(Path.Combine("Singles", "b.mp3"), null, new[] { "Artist B" });
        WriteMp3(Path.Combine("Singles", "c.mp3"), null, new[] { "Artist C" });
        var (library, _) = await NewLibraryAsync();

        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));

        Assert.All(library.AllTracks, t => Assert.Equal("Singles", t.Album));
        Assert.Equal(3, library.Albums.Count);
        Assert.DoesNotContain(library.Albums, a => a.Artist == Track.VariousArtists);
    }

    [Fact]
    public async Task OlderBuildsGroupedLooseSingles_AreSplitAgain_AfterTheOneTimeRead()
    {
        WriteMp3(Path.Combine("Singles", "a.mp3"), null, new[] { "Artist A" });
        WriteMp3(Path.Combine("Singles", "b.mp3"), null, new[] { "Artist B" });
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));
        // As 1.6.8 stored it: one "Singles" album by Various Artists, nothing else recorded.
        var vaId = Track.ComputeAlbumId(Track.VariousArtists, "Singles");
        foreach (var t in library.AllTracks)
        {
            t.AlbumFromFolder = false;
            t.AlbumArtist = Track.VariousArtists;
            t.AlbumId = vaId;
        }
        persistence.SaveArtwork(vaId, new byte[] { 3 });

        Assert.Equal(2, await library.ResolveUngroupedAlbumsAsync());
        await library.SetAlbumGroupingAsync(true);

        Assert.Equal(2, library.Albums.Count);
        Assert.Equal(new[] { "Artist A", "Artist B" }, library.Albums.Select(a => a.Artist).OrderBy(a => a));
        Assert.All(library.Albums, a => Assert.True(File.Exists(persistence.GetArtworkPath(a.Id)))); // cover carried
        // Once per library.
        Assert.True((await persistence.LoadSettingsAsync()).AlbumGroupingOriginalsRead);
        Assert.Equal(0, await library.ResolveUngroupedAlbumsAsync());
    }

    // ── The toggle (#193) ──

    private List<string> WritePremPoojari() => new()
    {
        WriteMp3(Path.Combine("Prem Poojari", "01.mp3"), "Prem Poojari", new[] { "K. J. Yesudas" }),
        WriteMp3(Path.Combine("Prem Poojari", "02.mp3"), "Prem Poojari", new[] { "K. S. Chithra" }),
        WriteMp3(Path.Combine("Prem Poojari", "03.mp3"), "Prem  poojari", new[] { "M. G. Sreekumar" }),
    };

    [Fact]
    public void Restore_PutsEveryRegroupedSongBackOnItsOwnAlbum()
    {
        Track Song(string file, string album, string artist) => new()
        {
            Title = file, Artist = artist, AlbumArtist = artist, AlbumArtistFromTag = false, Album = album,
            FilePath = TestPaths.Primary("Music", "OST", file + ".mp3"), AlbumId = Track.ComputeAlbumId(artist, album),
        };
        var tracks = new List<Track> { Song("01", "OST", "A"), Song("02", "OST", "B"), Song("03", "ost ", "C") };
        var own = tracks.Select(t => (t.AlbumArtist, t.Album, t.AlbumId)).ToList();
        var grouped = AlbumGrouping.Apply(tracks);
        Assert.Equal(3, grouped.Count);
        Assert.Single(tracks.Select(t => t.AlbumId).Distinct());

        Assert.Empty(AlbumGrouping.Restore(tracks, groupingEnabled: true)); // still grouped
        var moves = AlbumGrouping.Restore(tracks, groupingEnabled: false);

        Assert.Equal(3, moves.Count);
        Assert.Equal(own, tracks.Select(t => (t.AlbumArtist, t.Album, t.AlbumId)).ToList());
        Assert.All(tracks, t => Assert.Null(t.UngroupedAlbumArtist));
        Assert.Empty(AlbumGrouping.Restore(tracks, groupingEnabled: false)); // nothing left
        Assert.Empty(AlbumGrouping.Apply(tracks.Take(0).ToList()));
    }

    [Fact]
    public void Restore_KeepsSongsTheUserTaggedOrRenamedSince()
    {
        var a = new Track { Artist = "A", AlbumArtist = Track.VariousArtists, AlbumArtistFromTag = true, Album = "OST",
            UngroupedAlbumArtist = "A", UngroupedAlbum = "OST", AlbumId = Track.ComputeAlbumId(Track.VariousArtists, "OST") };
        var b = new Track { Artist = "B", AlbumArtist = Track.VariousArtists, AlbumArtistFromTag = false, Album = "Renamed",
            UngroupedAlbumArtist = "B", UngroupedAlbum = "OST", AlbumId = Track.ComputeAlbumId(Track.VariousArtists, "Renamed") };
        var ids = new[] { a.AlbumId, b.AlbumId };

        Assert.Empty(AlbumGrouping.Restore(new[] { a, b }, groupingEnabled: false));

        Assert.Equal(ids, new[] { a.AlbumId, b.AlbumId });
        Assert.Null(a.UngroupedAlbumArtist);
        Assert.Null(b.UngroupedAlbumArtist);
    }

    [Fact]
    public async Task TurningGroupingOff_RestoresTheSplitAlbums_AndOnGroupsThemAgain()
    {
        WritePremPoojari();
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));
        var grouped = Assert.Single(library.Albums);
        Assert.Equal(Track.VariousArtists, grouped.Artist);
        persistence.SaveArtwork(grouped.Id, new byte[] { 1, 2 });
        await persistence.UpdateSettingsAsync(s => s.PinnedAlbumIds = new List<Guid> { grouped.Id });
        var updates = 0;
        library.LibraryUpdated += (_, _) => updates++;

        var changed = await library.SetAlbumGroupingAsync(false);

        Assert.Equal(3, changed);
        Assert.Equal(1, updates);
        Assert.False(library.AlbumGroupingEnabled);
        var split = library.Albums.OrderBy(a => a.Artist).ToList();
        Assert.Equal(new[] { "K. J. Yesudas", "K. S. Chithra", "M. G. Sreekumar" }, split.Select(a => a.Artist));
        // The file's own spelling comes back too ("Prem  poojari").
        Assert.Equal("Prem  poojari", library.AllTracks.Single(t => t.Artist == "M. G. Sreekumar").Album);
        Assert.All(split, a => Assert.Equal(Track.ComputeAlbumId(a.Artist, a.Tracks[0].Album), a.Id));
        Assert.All(split, a => Assert.True(File.Exists(persistence.GetArtworkPath(a.Id)))); // cover carried back
        var pinned = Assert.Single((await persistence.LoadSettingsAsync()).PinnedAlbumIds);
        Assert.Contains(split, a => a.Id == pinned); // the pin follows a restored album

        // Kept across a rescan and a restart.
        await library.ScanAsync(new[] { Music });
        Assert.Equal(3, library.Albums.Count);
        await persistence.UpdateSettingsAsync(s => s.AlbumGroupingEnabled = false);
        var reloaded = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        await reloaded.LoadAsync();
        await reloaded.BackgroundInit;
        Assert.False(reloaded.AlbumGroupingEnabled);
        Assert.Equal(3, reloaded.Albums.Count);
        await reloaded.RebuildIndexAsync();
        Assert.Equal(3, reloaded.Albums.Count);

        // On again: one album, the same key as before.
        Assert.Equal(3, await library.SetAlbumGroupingAsync(true));
        Assert.Equal(grouped.Id, Assert.Single(library.Albums).Id);
        Assert.Equal(0, await library.SetAlbumGroupingAsync(true)); // no change, no rebuild noise
    }

    [Fact]
    public async Task TurningGroupingOff_ARegroupedLibraryOfAnOlderBuild_IsReadOnceAndSplit()
    {
        WritePremPoojari();
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));
        // As 1.6.8 stored it: grouped, but the songs' own album artists not kept.
        foreach (var t in library.AllTracks)
        {
            t.UngroupedAlbumArtist = null;
            t.UngroupedAlbum = null;
        }
        Assert.False((await persistence.LoadSettingsAsync()).AlbumGroupingOriginalsRead);

        Assert.Equal(3, await library.SetAlbumGroupingAsync(false));

        Assert.Equal(3, library.Albums.Count);
        Assert.Equal(new[] { "K. J. Yesudas", "K. S. Chithra", "M. G. Sreekumar" },
            library.Albums.Select(a => a.Artist).OrderBy(a => a));
    }

    // ── Writing the grouping into the files (#190) ──

    private static string[] ReadAlbumArtists(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.AlbumArtists;
    }

    [Fact]
    public async Task WriteTags_WritesTheGroupedAlbumArtist_OnlyToRegroupedFiles()
    {
        var files = WritePremPoojari();
        var solo = WriteMp3(Path.Combine("Solo", "01.mp3"), "Solo Album", new[] { "Solo Artist" });
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 4) Assert.Fail(ScanMissReport.Describe(library, Music));
        var soloStamp = File.GetLastWriteTimeUtc(solo);

        var result = await library.SetAlbumGroupingWriteTagsAsync(true);

        Assert.Equal(3, result.Grouped);
        Assert.Empty(result.UnwrittenFiles);
        foreach (var f in files)
        {
            Assert.Equal(new[] { Track.VariousArtists }, ReadAlbumArtists(f));
            using var tag = TagLib.File.Create(f);
            Assert.Equal("Prem Poojari", tag.Tag.Album); // one spelling for every file
            Assert.Equal(Path.GetFileNameWithoutExtension(f), tag.Tag.Title); // nothing else touched
            Assert.Single(tag.Tag.Performers);
        }
        Assert.Empty(ReadAlbumArtists(solo)); // not regrouped: not written
        Assert.Equal(soloStamp, File.GetLastWriteTimeUtc(solo));
        Assert.All(library.AllTracks.Where(t => t.Album == "Prem Poojari"), t => Assert.True(t.AlbumArtistFromTag));

        // The files say one album now: a re-read keeps it, and grouping off no longer splits it.
        foreach (var t in library.AllTracks) t.LastModified = default;
        await library.ScanAsync(new[] { Music });
        Assert.Equal(2, library.Albums.Count);
        await library.SetAlbumGroupingAsync(false);
        Assert.Equal(2, library.Albums.Count);
        Assert.Equal(0, (await library.SetAlbumGroupingWriteTagsAsync(true)).Grouped);
    }

    [Fact]
    public async Task WriteTags_ReadOnlyAndMissingFiles_AreReported_AndStayGroupedInNoctis()
    {
        var files = WritePremPoojari();
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));
        File.SetAttributes(files[1], FileAttributes.ReadOnly);
        File.Delete(files[2]);

        var result = await library.SetAlbumGroupingWriteTagsAsync(true);

        Assert.Equal(1, result.Grouped);
        Assert.Equal(new[] { "02.mp3", "03.mp3" }, result.UnwrittenFiles.OrderBy(n => n));
        Assert.Empty(ReadAlbumArtists(files[1]));
        Assert.Single(library.Albums);
        Assert.False(library.AllTracks.Single(t => t.FilePath == files[1]).AlbumArtistFromTag);
    }

    [Fact]
    public async Task WriteTags_Off_OrGroupingOff_WritesNothing()
    {
        var files = WritePremPoojari();
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));

        Assert.Equal(0, (await library.SetAlbumGroupingWriteTagsAsync(false)).Grouped);
        await library.SetAlbumGroupingAsync(false);
        Assert.Equal(0, (await library.SetAlbumGroupingWriteTagsAsync(true)).Grouped);

        Assert.All(files, f => Assert.Empty(ReadAlbumArtists(f)));
    }

    [Fact]
    public async Task WriteTags_On_ANewSongThatJoinsAGroupedAlbum_IsWrittenAfterTheScan()
    {
        WritePremPoojari();
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));
        await library.SetAlbumGroupingWriteTagsAsync(true);
        var written = new TaskCompletionSource<AlbumGroupResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        library.AlbumGroupingWritten += (_, r) => written.TrySetResult(r);

        var added = WriteMp3(Path.Combine("Prem Poojari", "04.mp3"), "Prem Poojari", new[] { "S. Janaki" });
        await library.ScanAsync(new[] { Music });

        var result = await written.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(1, result.Grouped);
        Assert.Equal(new[] { Track.VariousArtists }, ReadAlbumArtists(added));
        Assert.Single(library.Albums);
    }

    // ── FLAC (#190: the reporter copies FLAC and MP3) ──

    private string WriteFlac(string relativePath, string album, string performer)
    {
        var path = Path.Combine(Music, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var scratch = Path.Combine(_root, "scratch", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        File.Move(ReplayGainWriteTests.CreateFlac(scratch), path);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = Path.GetFileNameWithoutExtension(path);
        f.Tag.Performers = new[] { performer };
        f.Tag.Album = album;
        f.Save();
        return path;
    }

    private static void AssertFlacAlbumArtist(string path, string expected)
    {
        Assert.Equal("fLaC", System.Text.Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4)); // no ID3v2 in front
        using var f = TagLib.File.Create(path);
        Assert.False(f.TagTypesOnDisk.HasFlag(TagLib.TagTypes.Id3v2), f.TagTypesOnDisk.ToString());
        Assert.False(f.TagTypesOnDisk.HasFlag(TagLib.TagTypes.Ape), f.TagTypesOnDisk.ToString());
        var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false);
        Assert.Equal(new[] { expected }, xiph.GetField("ALBUMARTIST"));
    }

    [Fact]
    public async Task WriteTags_Flac_GetsAVorbisAlbumArtist()
    {
        var a = WriteFlac(Path.Combine("Malik", "01.flac"), "Malik", "Sushin Shyam, Anwar Ali");
        var b = WriteFlac(Path.Combine("Malik", "02.flac"), "Malik", "Sushin Shyam");
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));
        Assert.Equal("Sushin Shyam", Assert.Single(library.Albums).Artist);

        var result = await library.SetAlbumGroupingWriteTagsAsync(true);

        // Only the song whose own key differed is re-keyed, so only its file is written.
        Assert.Equal(1, result.Grouped);
        AssertFlacAlbumArtist(a, "Sushin Shyam");
        using (var f = TagLib.File.Create(b))
            Assert.Empty(f.Tag.AlbumArtists);
    }

    [Fact]
    public async Task GroupIntoOneAlbum_Flac_WritesAVorbisAlbumArtist()
    {
        var a = WriteFlac(Path.Combine("Rip A", "01.flac"), "Prem Poojari", "K. J. Yesudas");
        var b = WriteFlac(Path.Combine("Rip B", "02.flac"), "Prem Poojari (OST)", "K. S. Chithra");
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));

        var result = await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Prem Poojari", "Ouseppachan");

        Assert.Empty(result.UnwrittenFiles);
        AssertFlacAlbumArtist(a, "Ouseppachan");
        AssertFlacAlbumArtist(b, "Ouseppachan");
        using (var f = TagLib.File.Create(b))
            Assert.Equal("Prem Poojari", f.Tag.Album);
    }
}

/// <summary>GitHub #193 / #190: Settings › Library › Group Untagged Albums and Save Grouping to Files.</summary>
[Collection("MetadataServiceStatics")]
public class AlbumGroupingSettings193Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private (Noctis.ViewModels.SettingsViewModel Vm, FakeLibraryService Library) CreateSettings()
    {
        var library = new FakeLibraryService();
        return (new Noctis.ViewModels.SettingsViewModel(new PersistenceService(_root), library, new NoOpPlayHistoryService()), library);
    }

    private static async Task SettleAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await Task.Delay(20);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    [Fact]
    public void Defaults_GroupingOn_WritingOff()
    {
        var s = new AppSettings();
        Assert.True(s.AlbumGroupingEnabled);
        Assert.False(s.AlbumGroupingWriteTags);
        Assert.False(s.AlbumGroupingOriginalsRead);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TurningGroupingOff_AppliesToTheLibrary_IsSaved_AndLoadsWithoutReapplying()
    {
        var (vm, library) = CreateSettings();
        await vm.LoadAsync();
        Assert.True(vm.AlbumGroupingEnabled);
        Assert.False(vm.AlbumGroupingWriteTags);
        Assert.Empty(library.AlbumGroupingCalls); // the library reads the setting itself at load
        library.AlbumGroupingMoved = 3;

        vm.AlbumGroupingEnabled = false;
        await SettleAsync();

        Assert.Equal(new[] { false }, library.AlbumGroupingCalls);
        Assert.Equal(Noctis.Localization.Loc.T("Settings.AlbumGroupingMoved", 3), vm.ScanStatusText);
        await vm.SaveAsync();

        var (reloaded, reloadedLibrary) = CreateSettings();
        await reloaded.LoadAsync();
        Assert.False(reloaded.AlbumGroupingEnabled);
        Assert.False(reloaded.GetSettings().AlbumGroupingEnabled);
        Assert.Empty(reloadedLibrary.AlbumGroupingCalls);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        await SettleAsync();
        Assert.True(reloaded.AlbumGroupingEnabled);
        Assert.Equal(new[] { true }, reloadedLibrary.AlbumGroupingCalls);
    }

    [Avalonia.Headless.XUnit.AvaloniaFact]
    public async Task TurningWriteTagsOn_WritesNow_AndListsTheFilesNotSaved()
    {
        var (vm, library) = CreateSettings();
        await vm.LoadAsync();
        library.AlbumGroupingWriteResult = new AlbumGroupResult(Guid.Empty, 2, new[] { "03.mp3" });
        var notices = new List<Noctis.Views.ConfirmationRequest>();
        vm.ShowAlbumGroupingNotice = r =>
        {
            notices.Add(r);
            return Task.FromResult(new Noctis.Views.ConfirmationResult(true, false));
        };

        vm.AlbumGroupingWriteTags = true;
        await SettleAsync();

        Assert.Equal(new[] { true }, library.AlbumGroupingWriteTagsCalls);
        var notice = Assert.Single(notices);
        Assert.Equal(new[] { "03.mp3" }, notice.Details);
        Assert.Equal(Noctis.Localization.Loc.T("Settings.AlbumGroupingWrittenSome", 2, 1), vm.ScanStatusText);

        // A later background pass reports on the status line only.
        library.RaiseAlbumGroupingWritten(new AlbumGroupResult(Guid.Empty, 1, Array.Empty<string>()));
        await SettleAsync();
        Assert.Equal(Noctis.Localization.Loc.T("Settings.AlbumGroupingWritten", 1), vm.ScanStatusText);
        Assert.Single(notices);

        vm.AlbumGroupingWriteTags = false;
        await SettleAsync();
        Assert.Equal(new[] { true, false }, library.AlbumGroupingWriteTagsCalls);
    }

    [Fact]
    public void SettingsView_HasBothToggles_AndSaveToFilesFollowsGrouping()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "Noctis.sln")) && !Directory.Exists(Path.Combine(dir, "src", "Noctis")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var xaml = File.ReadAllText(Path.Combine(dir!, "src", "Noctis", "Views", "SettingsView.axaml"));

        Assert.Contains("IsChecked=\"{Binding AlbumGroupingEnabled}\"", xaml);
        Assert.Contains("IsChecked=\"{Binding AlbumGroupingWriteTags}\"", xaml);
        Assert.Contains("<Grid ColumnDefinitions=\"*,Auto\" IsEnabled=\"{Binding AlbumGroupingEnabled}\">", xaml);
    }
}
