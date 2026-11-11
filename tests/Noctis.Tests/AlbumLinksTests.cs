using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Discord (Halil, 11-11): MUSIC (30 songs) and MUSIC - SORRY 4 DA WAIT (the same 30 plus 4)
/// should both show their full track list without keeping the 30 files twice, and likes and
/// plays should be shared. "Link Tracks From" makes the deluxe album list the original's songs
/// as the same Track instances (AlbumLinks).
/// </summary>
[Collection("ArtistCredit global configuration")]
public class AlbumLinksTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AlbumLinksTests() => ArtistCredit.ResetToDefaults();

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── Pure album building ──

    private static Track Song(string album, int number, string title, int seconds = 100, int disc = 1)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = title,
            Album = album,
            AlbumArtist = "Playboi Carti",
            Artist = "Playboi Carti",
            TrackNumber = number,
            DiscNumber = disc,
            Duration = TimeSpan.FromSeconds(seconds),
            AlbumId = Track.ComputeAlbumId("Playboi Carti", album),
        };

    private static Album AlbumOf(params Track[] tracks) => new()
    {
        Id = tracks[0].AlbumId,
        Name = tracks[0].Album,
        Artist = tracks[0].AlbumArtist,
        Tracks = tracks.ToList(),
        TrackCount = tracks.Length,
        TotalDuration = TimeSpan.FromTicks(tracks.Sum(t => t.Duration.Ticks)),
    };

    private static Dictionary<Guid, Album> Index(params Album[] albums) => albums.ToDictionary(a => a.Id);

    private static AlbumLink Link(Album album, Album source) => new() { AlbumId = album.Id, SourceAlbumId = source.Id };

    [Fact]
    public void Attach_ListsTheSourceSongsAsTheSameInstances_InAlbumOrder_WithCountAndLength()
    {
        var music = AlbumOf(Song("MUSIC", 1, "POP MUSIC"), Song("MUSIC", 2, "EVIL J0RDAN"), Song("MUSIC", 3, "MOJO JOJO"));
        var deluxe = AlbumOf(Song("MUSIC - SORRY 4 DA WAIT", 4, "BACKD00R"), Song("MUSIC - SORRY 4 DA WAIT", 5, "DIS 1 GOT IT"));

        AlbumLinks.Attach(Index(music, deluxe), new[] { Link(deluxe, music) });

        Assert.Equal(new[] { "POP MUSIC", "EVIL J0RDAN", "MOJO JOJO", "BACKD00R", "DIS 1 GOT IT" },
            deluxe.EditionTracks.Select(t => t.Title));
        Assert.Same(music.Tracks[0], deluxe.EditionTracks[0]);
        Assert.Equal(5, deluxe.TrackCount);
        Assert.Equal(TimeSpan.FromSeconds(500), deluxe.TotalDuration);
        // The album's own files are unchanged: what edits, converts or deletes "the album" acts on.
        Assert.Equal(2, deluxe.Tracks.Count);
        Assert.Equal(3, deluxe.LinkedTracks.Count);
        Assert.Equal(new[] { music.Id }, deluxe.LinkedAlbumIds);
        // The source album is untouched.
        Assert.Equal(3, music.TrackCount);
        Assert.Same(music.Tracks, music.EditionTracks);
        Assert.False(music.HasLinkedTracks);
    }

    [Fact]
    public void Attach_TheEditionsOwnCopyOfASong_WinsOverTheLinkedOne()
    {
        var music = AlbumOf(Song("MUSIC", 1, "POP MUSIC"), Song("MUSIC", 2, "EVIL J0RDAN"));
        var deluxe = AlbumOf(Song("MUSIC - SORRY 4 DA WAIT", 1, "Pop Music"), Song("MUSIC - SORRY 4 DA WAIT", 3, "BACKD00R"));

        AlbumLinks.Attach(Index(music, deluxe), new[] { Link(deluxe, music) });

        Assert.Equal(3, deluxe.EditionTracks.Count);
        Assert.Same(deluxe.Tracks[0], deluxe.EditionTracks[0]);
        Assert.Same(music.Tracks[1], deluxe.EditionTracks[1]);
    }

    [Fact]
    public void Attach_OnATie_TheOwnFileComesFirst()
    {
        var music = AlbumOf(Song("MUSIC", 1, "A song"));
        var deluxe = AlbumOf(Song("MUSIC - SORRY 4 DA WAIT", 1, "Z bonus"));

        AlbumLinks.Attach(Index(music, deluxe), new[] { Link(deluxe, music) });

        Assert.Equal(new[] { "Z bonus", "A song" }, deluxe.EditionTracks.Select(t => t.Title));
    }

    [Fact]
    public void Attach_TakesOnlyTheSourcesOwnSongs_NoChainsNoCycles_AndIgnoresSelfLinks()
    {
        var a = AlbumOf(Song("A", 1, "a1"));
        var b = AlbumOf(Song("B", 2, "b2"));
        var c = AlbumOf(Song("C", 3, "c3"));

        AlbumLinks.Attach(Index(a, b, c), new[] { Link(b, a), Link(c, b), Link(a, c), Link(a, a) });

        Assert.Equal(new[] { "b2", "c3" }, c.EditionTracks.Select(t => t.Title));
        Assert.Equal(new[] { "a1", "c3" }, a.EditionTracks.Select(t => t.Title));
        Assert.Equal(new[] { "a1", "b2" }, b.EditionTracks.Select(t => t.Title));
    }

    [Fact]
    public void Attach_ALinkToAMissingAlbum_Waits()
    {
        var deluxe = AlbumOf(Song("MUSIC - SORRY 4 DA WAIT", 31, "BACKD00R"));

        AlbumLinks.Attach(Index(deluxe), new[] { new AlbumLink { AlbumId = deluxe.Id, SourceAlbumId = Guid.NewGuid() } });

        Assert.False(deluxe.HasLinkedTracks);
        Assert.Same(deluxe.Tracks, deluxe.EditionTracks);
        Assert.Equal(1, deluxe.TrackCount);
    }

    [Fact]
    public void Remap_FollowsARenamedAlbum_AndKeepsALinkWhoseSongsAreMissing()
    {
        var music = AlbumOf(Song("MUSIC", 1, "POP MUSIC"), Song("MUSIC", 2, "EVIL J0RDAN"));
        var deluxe = AlbumOf(Song("MUSIC - SORRY 4 DA WAIT", 31, "BACKD00R"));
        var oldIndex = Index(music, deluxe);
        var links = new List<AlbumLink> { Link(deluxe, music) };

        // Get Info renamed the deluxe album: its song now carries a new key.
        var song = deluxe.Tracks[0];
        song.Album = "MUSIC (Deluxe)";
        song.AlbumId = Track.ComputeAlbumId(song.AlbumArtist, song.Album);
        var renamed = AlbumOf(song);
        var tracks = music.Tracks.Append(song).ToDictionary(t => t.Id);

        var remapped = AlbumLinks.Remap(links, oldIndex, tracks, Index(music, renamed));

        Assert.NotNull(remapped);
        Assert.Equal(renamed.Id, Assert.Single(remapped!).AlbumId);
        Assert.Equal(music.Id, remapped![0].SourceAlbumId);

        // The original's files are not there (hidden folder, partial scan): nothing changes.
        var withoutMusic = new Dictionary<Guid, Track> { [song.Id] = song };
        var renamedLinks = remapped;
        Assert.Null(AlbumLinks.Remap(renamedLinks, Index(music, renamed), withoutMusic, Index(renamed)));
    }

    // ── Through the library ──

    private string Music => Path.Combine(_root, "music");

    private string WriteMp3(string folder, string album, uint number, string title)
    {
        var path = Path.Combine(Music, folder, $"{number:00} {title}.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title;
        f.Tag.Performers = new[] { "Playboi Carti" };
        f.Tag.AlbumArtists = new[] { "Playboi Carti" };
        f.Tag.Album = album;
        f.Tag.Track = number;
        f.Save();
        return path;
    }

    private async Task<(LibraryService Library, PersistenceService Persistence)> NewLibraryAsync()
    {
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        if (!File.Exists(Path.Combine(_root, "data", "settings.json")))
            await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { Music } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());
        return (library, persistence);
    }

    private static readonly Guid MusicId = Track.ComputeAlbumId("Playboi Carti", "MUSIC");
    private static readonly Guid DeluxeId = Track.ComputeAlbumId("Playboi Carti", "MUSIC - SORRY 4 DA WAIT");

    /// <summary>MUSIC with 3 songs, the deluxe folder with only its bonus song (track 4), linked.</summary>
    private async Task<(LibraryService Library, PersistenceService Persistence, string BonusPath, string FirstPath)> LinkedLibraryAsync()
    {
        var first = WriteMp3("MUSIC", "MUSIC", 1, "POP MUSIC");
        WriteMp3("MUSIC", "MUSIC", 2, "EVIL J0RDAN");
        WriteMp3("MUSIC", "MUSIC", 3, "MOJO JOJO");
        var bonus = WriteMp3("MUSIC - SORRY 4 DA WAIT", "MUSIC - SORRY 4 DA WAIT", 4, "BACKD00R");
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 4) Assert.Fail(ScanMissReport.Describe(library, Music));

        await library.LinkAlbumTracksAsync(DeluxeId, MusicId);
        return (library, persistence, bonus, first);
    }

    [Fact]
    public async Task Link_TheDeluxeAlbumListsAllFourSongs_TheOriginalStaysThree_SongsAreNotDoubled()
    {
        var (library, persistence, _, _) = await LinkedLibraryAsync();

        var music = library.GetAlbumById(MusicId)!;
        var deluxe = library.GetAlbumById(DeluxeId)!;
        Assert.Equal(3, music.TrackCount);
        Assert.Equal(4, deluxe.TrackCount);
        Assert.Equal(new[] { "POP MUSIC", "EVIL J0RDAN", "MOJO JOJO", "BACKD00R" }, deluxe.EditionTracks.Select(t => t.Title));
        for (int i = 0; i < 3; i++) Assert.Same(music.Tracks[i], deluxe.EditionTracks[i]);

        // One row per file: Songs, the albums' own lists, an artist's play-all and a genre/folder
        // style join of several albums never meet a linked song twice.
        Assert.Equal(4, library.Tracks.Count);
        Assert.Equal(4, library.Albums.Sum(a => a.Tracks.Count));
        var artistAll = ArtistDetailViewModel.AllTracksOf(library.Albums, "Playboi Carti");
        Assert.Equal(4, artistAll.Count);
        Assert.Equal(4, artistAll.Select(t => t.Id).Distinct().Count());
        Assert.Equal(4, library.Artists.Single().TrackCount);

        // Saved with the library's settings.
        var saved = await persistence.LoadSettingsAsync();
        var link = Assert.Single(saved.AlbumLinks);
        Assert.Equal(DeluxeId, link.AlbumId);
        Assert.Equal(MusicId, link.SourceAlbumId);
    }

    [Fact]
    public async Task Link_AFavouriteAndAPlayCount_AreTheSameOnBothAlbums()
    {
        var (library, _, _, _) = await LinkedLibraryAsync();
        var deluxe = library.GetAlbumById(DeluxeId)!;

        var onDeluxe = deluxe.EditionTracks[0];
        onDeluxe.IsFavorite = true;
        onDeluxe.PlayCount += 2;

        var onMusic = library.GetAlbumById(MusicId)!.Tracks[0];
        Assert.True(onMusic.IsFavorite);
        Assert.Equal(2, onMusic.PlayCount);
        Assert.Single(library.Tracks, t => t.IsFavorite);
    }

    [Fact]
    public async Task Link_SurvivesARescanAndARestart()
    {
        var (library, _, _, _) = await LinkedLibraryAsync();

        await library.ScanAsync(new[] { Music });
        Assert.Equal(4, library.GetAlbumById(DeluxeId)!.EditionTracks.Count);

        // Restart twice: the first load rebuilds or restores, the second restores the index cache.
        for (int run = 0; run < 2; run++)
        {
            var (reloaded, persistence) = await NewLibraryAsync();
            await reloaded.LoadAsync();
            await reloaded.BackgroundInit;
            var deluxe = reloaded.GetAlbumById(DeluxeId)!;
            Assert.Equal(4, deluxe.TrackCount);
            Assert.Equal(1, deluxe.Tracks.Count);
            Assert.Same(reloaded.GetAlbumById(MusicId)!.Tracks[0], deluxe.EditionTracks[0]);
            await Task.Delay(300); // the index cache is written in the background

            // The cache keeps the own files (what its restore checks against), not the edition.
            var cache = await persistence.LoadIndexCacheAsync();
            Assert.NotNull(cache);
            var entry = cache!.Albums.Single(a => a.Id == DeluxeId);
            Assert.Equal(1, entry.TrackCount);
            Assert.Single(entry.TrackIds);
        }
    }

    [Fact]
    public async Task Link_RemovingALinkedSong_TakesItOffBothAlbums()
    {
        var (library, _, _, first) = await LinkedLibraryAsync();
        var song = library.GetAlbumById(MusicId)!.Tracks.Single(t => t.FilePath == first);

        await library.RemoveTrackAsync(song.Id);

        Assert.Equal(2, library.GetAlbumById(MusicId)!.TrackCount);
        var deluxe = library.GetAlbumById(DeluxeId)!;
        Assert.Equal(3, deluxe.TrackCount);
        Assert.DoesNotContain(deluxe.EditionTracks, t => t.Id == song.Id);
    }

    [Fact]
    public async Task Link_TheDeluxeAlbumsOwnFileGoing_LeavesTheOriginalAlone()
    {
        var (library, _, bonus, _) = await LinkedLibraryAsync();
        File.Delete(bonus);

        await library.ScanAsync(new[] { Music });

        Assert.Null(library.GetAlbumById(DeluxeId));
        Assert.Equal(3, library.GetAlbumById(MusicId)!.TrackCount);
        Assert.Equal(3, library.Tracks.Count);
    }

    [Fact]
    public async Task Unlink_RestoresTheDeluxeAlbumToItsOwnFiles()
    {
        var (library, persistence, _, _) = await LinkedLibraryAsync();

        await library.UnlinkAlbumTracksAsync(DeluxeId, MusicId);

        var deluxe = library.GetAlbumById(DeluxeId)!;
        Assert.Equal(1, deluxe.TrackCount);
        Assert.False(deluxe.HasLinkedTracks);
        Assert.Same(deluxe.Tracks, deluxe.EditionTracks);
        Assert.Empty((await persistence.LoadSettingsAsync()).AlbumLinks);
    }

    [Fact]
    public void Candidates_AreTheArtistsOtherAlbums_NotYetLinked()
    {
        var music = AlbumOf(Song("MUSIC", 1, "POP MUSIC"));
        var wlr = AlbumOf(Song("Whole Lotta Red", 1, "Rockstar Made"));
        var deluxe = AlbumOf(Song("MUSIC - SORRY 4 DA WAIT", 31, "BACKD00R"));
        AlbumLinks.Attach(Index(music, wlr, deluxe), new[] { Link(deluxe, music) });

        Assert.Equal(new[] { wlr }, AlbumLinks.Candidates(deluxe, new[] { music, wlr, deluxe }));
    }
}
