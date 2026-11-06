using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #178 (ODR-PHOENIX): a Malayalam film soundtrack showed as one album per singer
/// ("Malik — Sushin Shyam, Anwar Ali, Sameer Binsi · 1 track", the same cover four times in
/// the carousel). Files without an Album Artist tag take their first performer as album
/// artist, and the album key is album artist + album name. Untagged songs of one album in one
/// folder are now one album (<see cref="AlbumGrouping"/>), and "Group into One Album" fixes the
/// cases the rule cannot see.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class GroupAlbums178Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public GroupAlbums178Tests() => ArtistCredit.ResetToDefaults();

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

    private static Track Untagged(string folder, string file, string album, string artist, int number = 0, bool? fromTag = false)
    {
        var albumArtist = Track.ResolveAlbumArtist("", ArtistCredit.Split(artist).FirstOrDefault(), false);
        return new Track
        {
            Title = file,
            Artist = artist,
            AlbumArtist = albumArtist,
            AlbumArtistFromTag = fromTag,
            Album = album,
            TrackNumber = number,
            FilePath = TestPaths.Primary("Music", folder, file + ".mp3"),
            AlbumId = Track.ComputeAlbumId(albumArtist, album),
        };
    }

    private static Track Tagged(string folder, string file, string album, string artist, string albumArtist, int number = 0)
    {
        var t = Untagged(folder, file, album, artist, number, fromTag: true);
        t.AlbumArtist = albumArtist;
        t.AlbumId = Track.ComputeAlbumId(albumArtist, album);
        return t;
    }

    private static List<Track> PremPoojari(string folder = "Prem Poojari") => new()
    {
        Untagged(folder, "01", "Prem Poojari", "K. J. Yesudas", 1),
        Untagged(folder, "02", "Prem Poojari", "K. S. Chithra", 2),
        Untagged(folder, "03", "Prem Poojari", "M. G. Sreekumar, K. S. Chithra", 3),
    };

    private string WriteMp3(string relativePath, string album, string[] performers, string[]? albumArtists = null,
        bool compilation = false)
    {
        var path = Path.Combine(_root, "music", relativePath);
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
        f.Tag.Album = album;
        f.Save();
        if (compilation)
            using (var c = TagLib.File.Create(path)) { ExtendedTagIO.WriteIsCompilation(c, true); c.Save(); }
        return path;
    }

    private string Music => Path.Combine(_root, "music");

    private async Task<(LibraryService Library, PersistenceService Persistence)> NewLibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { Music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        return (library, persistence);
    }

    private static string[] ReadAlbumArtists(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.AlbumArtists;
    }

    // ── Root cause ──

    [Fact]
    public void RootCause_UntaggedFilesOfOneAlbum_ReadWithOneAlbumKeyPerSinger()
    {
        // The per-file read keys each song by its own singer: this is what split the album.
        var a = WriteMp3(Path.Combine("Prem Poojari", "01.mp3"), "Prem Poojari", new[] { "K. J. Yesudas" });
        var b = WriteMp3(Path.Combine("Prem Poojari", "02.mp3"), "Prem Poojari", new[] { "K. S. Chithra" });

        var ta = new MetadataService().ReadTrackMetadata(a)!;
        var tb = new MetadataService().ReadTrackMetadata(b)!;

        Assert.NotEqual(ta.AlbumId, tb.AlbumId);
        Assert.Equal("K. J. Yesudas", ta.AlbumArtist);
        Assert.False(ta.AlbumArtistFromTag);
        Assert.False(tb.AlbumArtistFromTag);
    }

    [Fact]
    public void Read_RecordsWhetherTheAlbumArtistIsARealTag()
    {
        var tagged = WriteMp3("t.mp3", "X", new[] { "Singer" }, new[] { "Composer" });
        var untagged = WriteMp3("u.mp3", "X", new[] { "Singer" });

        Assert.True(new MetadataService().ReadTrackMetadata(tagged)!.AlbumArtistFromTag);
        Assert.False(new MetadataService().ReadTrackMetadata(untagged)!.AlbumArtistFromTag);
    }

    // ── The rule ──

    [Fact]
    public void SameFolder_NoAlbumArtistTag_DifferentSingers_BecomeOneVariousArtistsAlbum()
    {
        var tracks = PremPoojari();
        Assert.Equal(3, tracks.Select(t => t.AlbumId).Distinct().Count());

        var moves = AlbumGrouping.Apply(tracks);

        Assert.Single(tracks.Select(t => t.AlbumId).Distinct());
        Assert.All(tracks, t => Assert.Equal(Track.VariousArtists, t.AlbumArtist));
        Assert.Equal(Track.ComputeAlbumId(Track.VariousArtists, "Prem Poojari"), tracks[0].AlbumId);
        Assert.Equal(3, moves.Count);
        Assert.All(tracks, t => Assert.False(t.AlbumArtistFromTag)); // still not a tag
    }

    [Fact]
    public void ArtistCreditedOnEverySong_IsTheAlbumArtist()
    {
        // The issue's "Malik — Sushin Shyam, Anwar Ali, Sameer Binsi": the composer is on every song.
        var tracks = new List<Track>
        {
            Untagged("Malik", "01", "Malik", "Sushin Shyam, Anwar Ali, Sameer Binsi", 1),
            Untagged("Malik", "02", "Malik", "Nithin Raj, Sushin Shyam", 2),
            Untagged("Malik", "03", "Malik", "Sushin Shyam", 3),
        };

        AlbumGrouping.Apply(tracks);

        Assert.Single(tracks.Select(t => t.AlbumId).Distinct());
        Assert.All(tracks, t => Assert.Equal("Sushin Shyam", t.AlbumArtist));
    }

    [Fact]
    public void DiscSubfolders_CountAsTheAlbumFolder()
    {
        var tracks = new List<Track>
        {
            Untagged(Path.Combine("Big Album", "CD1"), "01", "Big Album", "A", 1),
            Untagged(Path.Combine("Big Album", "Disc 2"), "01", "Big Album", "B", 1),
        };

        AlbumGrouping.Apply(tracks);

        Assert.Equal(tracks[0].AlbumId, tracks[1].AlbumId);
    }

    [Fact]
    public void SameNameInDifferentFolders_StaysApart()
    {
        // Two different "Greatest Hits" by two artists.
        var queen = Untagged("Queen GH", "01", "Greatest Hits", "Queen", 1);
        var abba = Untagged("ABBA GH", "01", "Greatest Hits", "ABBA", 1);
        var before = (queen.AlbumId, abba.AlbumId);

        var moves = AlbumGrouping.Apply(new[] { queen, abba });

        Assert.Empty(moves);
        Assert.Equal(before, (queen.AlbumId, abba.AlbumId));
        Assert.Equal("Queen", queen.AlbumArtist);
    }

    [Fact]
    public void ExplicitAlbumArtistTags_AreNeverOverridden()
    {
        var a = Tagged("Split", "01", "Split EP", "Band A", "Band A", 1);
        var b = Tagged("Split", "02", "Split EP", "Band B", "Band B", 2);
        var before = (a.AlbumId, b.AlbumId);

        var moves = AlbumGrouping.Apply(new[] { a, b });

        Assert.Empty(moves);
        Assert.Equal(before, (a.AlbumId, b.AlbumId));
        Assert.Equal("Band A", a.AlbumArtist);
    }

    [Fact]
    public void PartlyTaggedAlbum_UntaggedSongsJoinTheTaggedAlbumArtist()
    {
        var tagged1 = Tagged("OST", "01", "OST", "Singer 1", "Composer", 1);
        var tagged2 = Tagged("OST", "02", "OST", "Singer 2", "Composer", 2);
        var untagged = Untagged("OST", "03", "OST", "Singer 3", 3);
        var taggedId = tagged1.AlbumId;

        AlbumGrouping.Apply(new[] { tagged1, tagged2, untagged });

        Assert.Equal(taggedId, untagged.AlbumId);
        Assert.Equal("Composer", untagged.AlbumArtist);
        Assert.Equal(taggedId, tagged1.AlbumId); // the tagged songs did not move
    }

    [Fact]
    public void Compilations_AreLeftAsVariousArtists()
    {
        var c1 = Untagged("Comp", "01", "Now 1", "A", 1);
        var c2 = Untagged("Comp", "02", "Now 1", "B", 2);
        foreach (var t in new[] { c1, c2 })
        {
            t.IsCompilation = true;
            t.AlbumArtist = Track.ResolveAlbumArtist("", t.Artist, true);
            t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
        }

        var moves = AlbumGrouping.Apply(new[] { c1, c2 });

        Assert.Empty(moves);
        Assert.Equal(Track.ComputeAlbumId(Track.VariousArtists, "Now 1"), c1.AlbumId);
    }

    [Fact]
    public void UnknownSource_IsNotGrouped_UntilResolved()
    {
        var tracks = PremPoojari();
        foreach (var t in tracks) t.AlbumArtistFromTag = null;

        Assert.Empty(AlbumGrouping.Apply(tracks));
        Assert.Equal(3, AlbumGrouping.UnresolvedInSplitAlbums(tracks).Count);
    }

    [Fact]
    public void PlaceholderAlbumNames_AreNotGrouped()
    {
        var a = Untagged("Downloads", "a", "Unknown Album", "A");
        var b = Untagged("Downloads", "b", "Unknown Album", "B");

        Assert.Empty(AlbumGrouping.Apply(new[] { a, b }));
    }

    [Fact]
    public void AlbumNamesDifferingInCaseAndSpacing_AreOneAlbum()
    {
        var a = Untagged("PP", "01", "Prem Poojari", "A", 1);
        var b = Untagged("PP", "02", "prem  poojari ", "B", 2);
        var c = Untagged("PP", "03", "Prem Poojari", "C", 3);

        AlbumGrouping.Apply(new[] { a, b, c });

        Assert.Single(new[] { a, b, c }.Select(t => t.AlbumId).Distinct());
        Assert.Equal("Prem Poojari", b.Album); // the most common spelling
    }

    // ── Stability ──

    [Fact]
    public void SecondPass_ChangesNothing_AndInputOrderDoesNotMatter()
    {
        var forward = PremPoojari();
        var reversed = PremPoojari();
        reversed.Reverse();

        AlbumGrouping.Apply(forward);
        AlbumGrouping.Apply(reversed);

        Assert.Empty(AlbumGrouping.Apply(forward));
        Assert.Equal(forward[0].AlbumId, reversed[0].AlbumId);
        Assert.Equal(forward[0].AlbumArtist, reversed[0].AlbumArtist);
    }

    [Fact]
    public void NewSongInTheFolder_JoinsTheGroupedAlbum()
    {
        var tracks = PremPoojari();
        AlbumGrouping.Apply(tracks);
        var albumId = tracks[0].AlbumId;

        // A new file (or a re-read one) arrives keyed by its own singer.
        tracks.Add(Untagged("Prem Poojari", "04", "Prem Poojari", "S. Janaki", 4));
        var moves = AlbumGrouping.Apply(tracks);

        Assert.Single(moves);
        Assert.All(tracks, t => Assert.Equal(albumId, t.AlbumId));
    }

    [Fact]
    public void ReReadSongOfAGroupedAlbum_ReturnsToIt()
    {
        var tracks = PremPoojari();
        AlbumGrouping.Apply(tracks);
        var albumId = tracks[0].AlbumId;

        // A rescan of a changed file replaces it with a fresh read (performer as album artist).
        tracks[1] = Untagged("Prem Poojari", "02", "Prem Poojari", "K. S. Chithra", 2);
        AlbumGrouping.Apply(tracks);

        Assert.All(tracks, t => Assert.Equal(albumId, t.AlbumId));
    }

    // ── Folder keys ──

    [Fact]
    public void FolderKey_AndroidDocumentUri_UsesTheDecodedFolder()
    {
        const string a = "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2FPrem%20Poojari%2F01.mp3";
        const string b = "content://com.android.externalstorage.documents/tree/primary%3AMusic/document/primary%3AMusic%2FPrem%20Poojari%2FCD2%2F01.mp3";

        Assert.Equal("content:primary:music/prem poojari", AlbumGrouping.FolderKey(a));
        Assert.Equal(AlbumGrouping.FolderKey(a), AlbumGrouping.FolderKey(b));
        Assert.Null(AlbumGrouping.FolderKey("content://media/external/audio/media/42"));
        Assert.Null(AlbumGrouping.FolderKey("http://server/stream/1"));
    }

    // ── Album-keyed data ──

    [Fact]
    public void CarryAlbumKeyedFiles_CopiesCoverAndLoops_ToTheNewKey()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        var tracks = PremPoojari();
        var oldId = tracks[0].AlbumId;
        var trackId = tracks[0].Id;
        persistence.SaveArtwork(oldId, new byte[] { 1, 2, 3 });
        var loop = persistence.GetAnimatedCoverPath(oldId, null, ".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(loop)!);
        File.WriteAllBytes(loop, new byte[] { 9 });

        var moves = AlbumGrouping.Apply(tracks);
        LibraryService.CarryAlbumKeyedFiles(persistence, moves);

        var newId = tracks[0].AlbumId;
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(persistence.GetArtworkPath(newId)));
        Assert.True(File.Exists(persistence.GetAnimatedCoverPath(newId, null, ".mp4")));
        Assert.True(File.Exists(persistence.GetArtworkPath(oldId))); // copied, not moved
        Assert.Equal(trackId, moves[0].TrackId);
    }

    [Fact]
    public void PinnedAlbums_FollowAnEmptiedKey()
    {
        var tracks = PremPoojari();
        var oldIds = tracks.Select(t => t.AlbumId).ToList();
        var other = Guid.NewGuid();
        var moves = AlbumGrouping.Apply(tracks);

        var remapped = LibraryService.RemapPinnedAlbumIds(new[] { other, oldIds[0], oldIds[1] }, moves, tracks);

        Assert.Equal(new[] { other, tracks[0].AlbumId }, remapped);
        Assert.Null(LibraryService.RemapPinnedAlbumIds(new[] { other }, moves, tracks));
    }

    // ── Library: scan, rescan, existing libraries ──

    [Fact]
    public async Task Scan_GroupsTheSoundtrack_AndRescansKeepTheSameAlbum()
    {
        WriteMp3(Path.Combine("Prem Poojari", "01.mp3"), "Prem Poojari", new[] { "K. J. Yesudas" });
        WriteMp3(Path.Combine("Prem Poojari", "02.mp3"), "Prem Poojari", new[] { "K. S. Chithra" });
        WriteMp3(Path.Combine("Prem Poojari", "03.mp3"), "Prem Poojari", new[] { "M. G. Sreekumar" });
        // Same album name elsewhere, by one artist: stays its own album.
        WriteMp3(Path.Combine("Other", "01.mp3"), "Prem Poojari", new[] { "Someone Else" });
        var (library, _) = await NewLibraryAsync();

        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 4) Assert.Fail(ScanMissReport.Describe(library, Music));

        var soundtrack = library.Albums.Where(a => a.Name == "Prem Poojari").ToList();
        Assert.Equal(2, soundtrack.Count);
        var grouped = soundtrack.Single(a => a.TrackCount == 3);
        Assert.Equal(Track.VariousArtists, grouped.Artist);
        Assert.Equal("Someone Else", soundtrack.Single(a => a.TrackCount == 1).Artist);

        // Unchanged rescan: same key.
        await library.ScanAsync(new[] { Music });
        Assert.Contains(library.Albums, a => a.Id == grouped.Id && a.TrackCount == 3);

        // A new song in the folder joins.
        WriteMp3(Path.Combine("Prem Poojari", "04.mp3"), "Prem Poojari", new[] { "S. Janaki" });
        await library.ScanAsync(new[] { Music });
        Assert.Contains(library.Albums, a => a.Id == grouped.Id && a.TrackCount == 4);
    }

    [Fact]
    public async Task Scan_RespectsAlbumArtistTags()
    {
        WriteMp3(Path.Combine("Split", "01.mp3"), "Split EP", new[] { "Band A" }, new[] { "Band A" });
        WriteMp3(Path.Combine("Split", "02.mp3"), "Split EP", new[] { "Band B" }, new[] { "Band B" });
        var (library, _) = await NewLibraryAsync();

        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));

        Assert.Equal(2, library.Albums.Count(a => a.Name == "Split EP"));
    }

    [Fact]
    public async Task ExistingLibrary_UnknownSources_AreReadOnce_AndTheAlbumGroups()
    {
        WriteMp3(Path.Combine("Prem Poojari", "01.mp3"), "Prem Poojari", new[] { "K. J. Yesudas" });
        WriteMp3(Path.Combine("Prem Poojari", "02.mp3"), "Prem Poojari", new[] { "K. S. Chithra" });
        // A tagged split album (two real album artists) must stay split after the re-read.
        WriteMp3(Path.Combine("Split", "01.mp3"), "Split EP", new[] { "Band A" }, new[] { "Band A" });
        WriteMp3(Path.Combine("Split", "02.mp3"), "Split EP", new[] { "Band B" }, new[] { "Band B" });
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 4) Assert.Fail(ScanMissReport.Describe(library, Music));

        // As an older build stored it: no source recorded, one album per singer.
        foreach (var t in library.AllTracks)
        {
            t.AlbumArtistFromTag = null;
            t.AlbumArtist = ArtistCredit.Split(t.Artist)[0];
            t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
        }
        var oldId = library.AllTracks.First(t => t.Album == "Prem Poojari").AlbumId;
        persistence.SaveArtwork(oldId, new byte[] { 4, 5, 6 });

        var resolved = await library.ResolveAlbumArtistSourcesAsync();

        Assert.Equal(4, resolved);
        var soundtrack = library.Albums.Where(a => a.Name == "Prem Poojari").ToList();
        Assert.Single(soundtrack);
        Assert.Equal(2, soundtrack[0].TrackCount);
        Assert.True(File.Exists(persistence.GetArtworkPath(soundtrack[0].Id))); // cover carried
        Assert.Equal(2, library.Albums.Count(a => a.Name == "Split EP"));
        // Nothing left to resolve: the next launch reads no file.
        Assert.Equal(0, await library.ResolveAlbumArtistSourcesAsync());
    }

    // ── Manual: Group into One Album ──

    [Fact]
    public async Task GroupIntoAlbum_WritesTheTags_AndMergesAlbumsFromDifferentFolders()
    {
        var a = WriteMp3(Path.Combine("Rip A", "01.mp3"), "Prem Poojari (OST)", new[] { "K. J. Yesudas" }, new[] { "K. J. Yesudas" });
        var b = WriteMp3(Path.Combine("Rip B", "02.mp3"), "Prem Poojari", new[] { "K. S. Chithra" });
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));
        Assert.Equal(2, library.Albums.Count);
        var oldId = library.AllTracks.Single(t => t.FilePath == a).AlbumId;
        persistence.SaveArtwork(oldId, new byte[] { 7 });

        var result = await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Prem Poojari", "Ouseppachan");

        Assert.Equal(2, result.Grouped);
        Assert.Empty(result.UnwrittenFiles);
        var album = Assert.Single(library.Albums);
        Assert.Equal("Prem Poojari", album.Name);
        Assert.Equal("Ouseppachan", album.Artist);
        Assert.Equal(result.AlbumId, album.Id);
        Assert.True(File.Exists(persistence.GetArtworkPath(album.Id)));
        Assert.Equal(new[] { "Ouseppachan" }, ReadAlbumArtists(a));
        Assert.Equal(new[] { "Ouseppachan" }, ReadAlbumArtists(b));

        // Survives a rescan that re-reads the files.
        foreach (var t in library.AllTracks) t.LastModified = default;
        await library.ScanAsync(new[] { Music });
        Assert.Equal(album.Id, Assert.Single(library.Albums).Id);
    }

    [Fact]
    public async Task GroupIntoAlbum_WritesTheTags_OfSongsTheRuleAlreadyGrouped()
    {
        // The rule files these under "Various Artists" in the library only; their files have
        // no album artist. Grouping them with a namesake must still write every file.
        var a = WriteMp3(Path.Combine("Prem Poojari", "01.mp3"), "Prem Poojari", new[] { "K. J. Yesudas" });
        var b = WriteMp3(Path.Combine("Prem Poojari", "02.mp3"), "Prem Poojari", new[] { "K. S. Chithra" });
        var c = WriteMp3(Path.Combine("Other", "01.mp3"), "Prem Poojari", new[] { "Someone Else" });
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 3) Assert.Fail(ScanMissReport.Describe(library, Music));
        Assert.Equal(Track.VariousArtists, library.AllTracks.Single(t => t.FilePath == a).AlbumArtist);

        var result = await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "Prem Poojari", Track.VariousArtists);

        Assert.Equal(3, result.Grouped);
        Assert.Single(library.Albums);
        Assert.Equal(new[] { Track.VariousArtists }, ReadAlbumArtists(a));
        Assert.Equal(new[] { Track.VariousArtists }, ReadAlbumArtists(b));
        Assert.Equal(new[] { Track.VariousArtists }, ReadAlbumArtists(c));
    }

    [Fact]
    public async Task GroupIntoAlbum_ReadOnlyFile_StillGroups_AndIsReported()
    {
        var a = WriteMp3(Path.Combine("A", "01.mp3"), "One", new[] { "X" }, new[] { "X" });
        var b = WriteMp3(Path.Combine("B", "01.mp3"), "Two", new[] { "Y" }, new[] { "Y" });
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));
        File.SetAttributes(b, FileAttributes.ReadOnly);

        var result = await library.GroupIntoAlbumAsync(library.AllTracks.ToList(), "One", "X");

        Assert.Equal(new[] { "01.mp3" }, result.UnwrittenFiles);
        Assert.Single(library.Albums);
        Assert.Equal(new[] { "Y" }, ReadAlbumArtists(b));
        Assert.True(library.AllTracks.Single(t => t.FilePath == b).AlbumArtistFromTag); // not re-keyed by the rule
    }

    [Fact]
    public void SuggestGroupTarget_UsesTheMostCommonNameAndTheSharedArtist()
    {
        var tracks = new List<Track>
        {
            Untagged("1", "01", "Malik", "Sushin Shyam, Anwar Ali", 1),
            Untagged("2", "02", "Malik", "Sushin Shyam", 2),
            Untagged("3", "03", "Malik (Original Motion Picture Soundtrack)", "Nithin Raj, Sushin Shyam", 3),
        };

        var (album, artist) = AlbumGrouping.SuggestGroupTarget(tracks);

        Assert.Equal("Malik", album);
        Assert.Equal("Sushin Shyam", artist);
        Assert.Equal(Track.VariousArtists,
            AlbumGrouping.SuggestGroupTarget(new[] { Untagged("1", "a", "M", "A"), Untagged("1", "b", "M", "B") }).AlbumArtist);
    }

    [Fact]
    public void SameNameAlbums_FindsSpellingVariants()
    {
        var a = new Album { Id = Guid.NewGuid(), Name = "Prem Poojari" };
        var b = new Album { Id = Guid.NewGuid(), Name = "prem  poojari" };
        var c = new Album { Id = Guid.NewGuid(), Name = "Malik" };

        Assert.Equal(new[] { a, b }, AlbumGrouping.SameNameAlbums(new[] { a, b, c }, a));
    }

    [AvaloniaFact]
    public async Task GroupCommand_SelectedAlbums_PromptsWithDefaults_AndGroupsTheirSongs()
    {
        var (vm, lib) = AlbumsVm();
        var t1 = Untagged("1", "01", "Malik", "Sushin Shyam, Anwar Ali", 1);
        var t2 = Untagged("2", "02", "Malik", "Sushin Shyam", 2);
        var a1 = new Album { Id = t1.AlbumId, Name = "Malik", Artist = t1.AlbumArtist, Tracks = new() { t1 } };
        var a2 = new Album { Id = t2.AlbumId, Name = "Malik", Artist = t2.AlbumArtist, Tracks = new() { t2 } };
        (string?, string?) asked = default;
        vm.PromptGroupTarget = (_, _, name, _, artist, _, _) =>
        {
            asked = (name, artist);
            return Task.FromResult<(string, string)?>((name!, "Sushin Shyam"));
        };
        vm.CtrlSelectedAlbums = new List<Album> { a1, a2 };
        var consumed = 0;
        vm.CtrlSelectionConsumed += (_, _) => consumed++;

        await vm.GroupIntoOneAlbumCommand.ExecuteAsync(a1);

        Assert.Equal(("Malik", "Sushin Shyam"), asked);
        var call = Assert.Single(lib.GroupCalls);
        Assert.Equal(new[] { t1, t2 }, call.Tracks);
        Assert.Equal(("Malik", "Sushin Shyam"), (call.Album, call.AlbumArtist));
        Assert.Empty(vm.CtrlSelectedAlbums);
        // The view clears its own selection on this, or the old albums ride into the next command.
        Assert.Equal(1, consumed);
    }

    /// <summary>The Albums view drops a selection a command used, and selected albums a
    /// rebuild no longer shows (they rode into the next Group as invisible extras, GitHub #178).</summary>
    [Fact]
    public void AlbumsView_ClearsItsSelection_OnConsume_AndPrunesItOnRebuild()
    {
        var src = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis", "Views", "LibraryAlbumsView.axaml.cs"));
        Assert.Contains("_vm.CtrlSelectionConsumed += OnCtrlSelectionConsumed;", src);
        Assert.Contains("_vm.CtrlSelectionConsumed -= OnCtrlSelectionConsumed;", src);
        Assert.Contains("_selectedAlbums.RemoveWhere(a => !shown.Contains(a));", src);
        var vmSrc = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis", "ViewModels", "LibraryAlbumsViewModel.cs"));
        // Every command that used the selection goes through ConsumeCtrlSelection.
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(vmSrc, @"CtrlSelectedAlbums\.Clear\(\);"));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Noctis.sln not found");
    }

    [AvaloniaFact]
    public async Task GroupCommand_OneAlbum_OffersItsNamesakes_OrExplainsHowToSelect()
    {
        var (vm, lib) = AlbumsVm();
        var t1 = Untagged("1", "01", "Prem Poojari", "A", 1);
        var t2 = Untagged("1", "02", "Prem Poojari", "B", 2);
        var t3 = Untagged("2", "01", "Malik", "C", 1);
        var a1 = new Album { Id = t1.AlbumId, Name = "Prem Poojari", Tracks = new() { t1 } };
        var a2 = new Album { Id = t2.AlbumId, Name = "Prem Poojari", Tracks = new() { t2 } };
        var a3 = new Album { Id = t3.AlbumId, Name = "Malik", Tracks = new() { t3 } };
        ((List<Album>)lib.Albums).AddRange(new[] { a1, a2, a3 });
        vm.PromptGroupTarget = (_, _, name, _, artist, _, _) => Task.FromResult<(string, string)?>((name!, artist!));
        var notices = 0;
        Views.ConfirmationRequest? notice = null;
        vm.ShowGroupNotice = r => { notices++; notice = r; return Task.FromResult(new Views.ConfirmationResult(true, false)); };

        await vm.GroupIntoOneAlbumCommand.ExecuteAsync(a1);
        Assert.Equal(new[] { t1, t2 }, Assert.Single(lib.GroupCalls).Tracks);

        await vm.GroupIntoOneAlbumCommand.ExecuteAsync(a3);
        Assert.Single(lib.GroupCalls); // nothing to group with
        Assert.Equal(1, notices);
        Assert.True(notice!.InfoOnly); // a message, not a question: OK only (owner 11-05)
    }

    [AvaloniaFact]
    public void ConfirmationDialog_InfoOnly_ShowsOnlyOk()
    {
        var info = new Views.ConfirmationDialog(new Views.ConfirmationRequest("Done.", InfoOnly: true));
        Assert.False(info.FindControl<Button>("CancelButton")!.IsVisible);
        Assert.Equal(Loc.T("Mobile.OK"), info.FindControl<Button>("ConfirmButton")!.Content);

        var ask = new Views.ConfirmationDialog(new Views.ConfirmationRequest("Sure?"));
        Assert.True(ask.FindControl<Button>("CancelButton")!.IsVisible);
    }

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
}
