using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #239 (ODR-PHOENIX): move a song into another album, or into a new album the user
/// names, and have the files say so, so other players and DAPs show it there too.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class MoveToAlbum239Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public MoveToAlbum239Tests() => ArtistCredit.ResetToDefaults();

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

    private static TagLib.File Read(string path) => TagLib.File.Create(path);

    private static Track T(string album, int number, int disc = 1, int count = 0, int discCount = 1, int year = 0,
        bool compilation = false) => new()
    {
        Title = $"{album} {disc}-{number}",
        Album = album,
        AlbumArtist = "Artist",
        TrackNumber = number,
        TrackCount = count,
        DiscNumber = disc,
        DiscCount = discCount,
        Year = year,
        ReleaseDate = year > 0 ? $"{year}-05-01" : string.Empty,
        IsCompilation = compilation,
        FilePath = TestPaths.Primary("Music", album, $"{disc}-{number}.mp3"),
    };

    // ── Which tags a move writes ──

    [Fact]
    public void Plan_ExistingAlbum_AppendsAfterItsLastSong_OnItsLastDisc_AndTakesItsAlbumTags()
    {
        var album = new[]
        {
            T("Target", 1, disc: 1, count: 2, discCount: 2, year: 2004, compilation: true),
            T("Target", 2, disc: 1, count: 2, discCount: 2, year: 2004, compilation: true),
            T("Target", 1, disc: 2, count: 3, discCount: 2, year: 2004, compilation: true),
            T("Target", 3, disc: 2, count: 3, discCount: 2, year: 2004, compilation: true),
        };
        var a = T("Old", 7, disc: 1, count: 12, year: 1999);
        var b = T("Old", 9, disc: 1, count: 12, year: 1999);

        var plans = AlbumMove.PlanMove(new[] { a, b }, album, "Target", "Artist");

        Assert.Equal(new[] { 4, 5 }, plans.Select(p => p.TrackNumber));
        Assert.All(plans, p =>
        {
            Assert.Equal(2, p.DiscNumber);
            Assert.Equal(2, p.DiscCount);
            Assert.True(p.IsCompilation);
            Assert.Equal(2004, p.Year);
            Assert.Equal("2004-05-01", p.ReleaseDate);
            Assert.Equal(("Target", "Artist"), (p.Album, p.AlbumArtist));
            // Disc 2 says 3 songs: "4/3" would be wrong, so the total is cleared, not kept.
            Assert.Equal(0, p.TrackCount);
        });
    }

    [Fact]
    public void Plan_ExistingAlbum_KeepsItsTrackTotal_WhenTheNewNumberFits()
    {
        var album = new[] { T("Target", 1, count: 10), T("Target", 2, count: 10) };

        var plan = Assert.Single(AlbumMove.PlanMove(new[] { T("Old", 5) }, album, "Target", "Artist"));

        Assert.Equal(3, plan.TrackNumber);
        Assert.Equal(10, plan.TrackCount);
        Assert.Equal(0, plan.Year); // the album has no year: the song keeps its own
    }

    [Fact]
    public void Plan_ExistingAlbumWithoutTrackNumbers_AddsNone()
    {
        var album = new[] { T("Target", 0), T("Target", 0) };

        var plan = Assert.Single(AlbumMove.PlanMove(new[] { T("Old", 5, count: 9) }, album, "Target", "Artist"));

        Assert.Equal(0, plan.TrackNumber);
        Assert.Equal(0, plan.TrackCount);
    }

    [Fact]
    public void Plan_NewAlbum_NumbersTheSongsInOrder_OnOneDisc_NotACompilation()
    {
        var a = T("One", 7, disc: 2, discCount: 2, count: 12, year: 1999, compilation: true);
        var b = T("Two", 3, year: 2010);
        a.IsReleaseTypeOverridden = true;
        a.ReleaseType = ReleaseType.Single;

        var plans = AlbumMove.PlanMove(new[] { a, b }, Array.Empty<Track>(), "Mix", "Me");

        Assert.Equal(new[] { 1, 2 }, plans.Select(p => p.TrackNumber));
        Assert.All(plans, p =>
        {
            Assert.Equal(2, p.TrackCount);
            Assert.Equal((1, 1), (p.DiscNumber, p.DiscCount));
            Assert.False(p.IsCompilation);
            Assert.False(p.IsReleaseTypeOverridden);
            Assert.Equal(0, p.Year); // each keeps its own year
        });

        foreach (var p in plans) AlbumMove.Apply(p);
        Assert.Equal((1999, 2010), (a.Year, b.Year));
        Assert.Equal(("Mix", "Me", 1, 1), (a.Album, a.AlbumArtist, a.TrackNumber, a.DiscNumber));
    }

    // ── The library and the files ──

    [Fact]
    public async Task Move_IntoExistingAlbum_WritesItsTagsToTheFile_AndTheSongJoinsIt()
    {
        var t1 = WriteMp3(Path.Combine("Target", "01.mp3"), "Target", "Artist", "Artist", track: 1, trackCount: 3, year: 2004,
            more: f => { f.Tag.MusicBrainzReleaseId = "rel-target"; f.Tag.AlbumSort = "Target, The"; });
        WriteMp3(Path.Combine("Target", "02.mp3"), "Target", "Artist", "Artist", track: 2, trackCount: 3, year: 2004,
            more: f => f.Tag.MusicBrainzReleaseId = "rel-target");
        var cover = new byte[] { 0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4 };
        var song = WriteMp3(Path.Combine("Old", "05.mp3"), "Old", "Guest", "Guest", track: 5, trackCount: 9, year: 1999,
            more: f =>
            {
                f.Tag.MusicBrainzReleaseId = "rel-old";
                f.Tag.Pictures = new TagLib.IPicture[]
                {
                    new TagLib.Picture(new TagLib.ByteVector(cover)) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                };
            });
        var (library, _) = await NewLibraryAsync(3);
        var track = library.AllTracks.Single(t => t.FilePath == song);
        var updated = 0;
        library.LibraryUpdated += (_, _) => updated++;

        var result = await library.MoveToAlbumAsync(new[] { track }, "target", "ARTIST"); // matched as albums are

        Assert.Equal(1, result.Grouped);
        Assert.Empty(result.UnwrittenFiles);
        Assert.Equal(1, updated);
        var album = Assert.Single(library.Albums, a => a.Name == "Target");
        Assert.Equal(3, album.Tracks.Count);
        Assert.Contains(track, album.Tracks);
        Assert.DoesNotContain(library.Albums, a => a.Name == "Old");
        using (var f = Read(song))
        {
            Assert.Equal("Target", f.Tag.Album);
            Assert.Equal(new[] { "Artist" }, f.Tag.AlbumArtists);
            Assert.Equal(new[] { "Guest" }, f.Tag.Performers); // the song's own artist stays
            Assert.Equal(3u, f.Tag.Track);
            Assert.Equal(3u, f.Tag.TrackCount);
            Assert.Equal(2004u, f.Tag.Year);
            Assert.Equal("rel-target", f.Tag.MusicBrainzReleaseId);
            Assert.Equal("Target, The", f.Tag.AlbumSort);
            Assert.Equal(cover, Assert.Single(f.Tag.Pictures).Data.Data); // cover untouched
        }
        using (var f = Read(t1)) Assert.Equal(1u, f.Tag.Track); // the album's own files untouched

        // Survives a rescan that re-reads the files.
        foreach (var t in library.AllTracks) t.LastModified = default;
        await library.ScanAsync(new[] { Music });
        Assert.Equal(3, Assert.Single(library.Albums).Tracks.Count);
    }

    [Fact]
    public async Task Move_IntoNewAlbum_CreatesIt_NumbersTheSongs_AndClearsTheOldReleaseIds()
    {
        var a = WriteMp3(Path.Combine("One", "07.mp3"), "One", "X", "X", track: 7, trackCount: 12, disc: 2, discCount: 2, year: 1999,
            more: f => { f.Tag.MusicBrainzReleaseId = "rel-one"; f.Tag.AlbumSort = "One"; });
        var b = WriteMp3(Path.Combine("Two", "03.mp3"), "Two", "Y", "Y", track: 3, year: 2010);
        WriteMp3(Path.Combine("One", "08.mp3"), "One", "X", "X", track: 8);
        var (library, persistence) = await NewLibraryAsync(3);
        var ta = library.AllTracks.Single(t => t.FilePath == a);
        var tb = library.AllTracks.Single(t => t.FilePath == b);
        persistence.SaveArtwork(ta.AlbumId, new byte[] { 9 });

        var result = await library.MoveToAlbumAsync(new[] { ta, tb }, "My Mix", "Me");

        Assert.Equal(2, result.Grouped);
        var album = Assert.Single(library.Albums, x => x.Name == "My Mix");
        Assert.Equal("Me", album.Artist);
        Assert.Equal(new[] { ta, tb }, album.Tracks);
        Assert.True(File.Exists(persistence.GetArtworkPath(album.Id))); // the first song's old cover
        Assert.Single(library.Albums, x => x.Name == "One"); // the song left behind keeps its album
        using (var f = Read(a))
        {
            Assert.Equal(("My Mix", 1u, 2u, 1u, 1u), (f.Tag.Album, f.Tag.Track, f.Tag.TrackCount, f.Tag.Disc, f.Tag.DiscCount));
            Assert.Equal(new[] { "Me" }, f.Tag.AlbumArtists);
            Assert.Null(f.Tag.MusicBrainzReleaseId);
            Assert.Null(f.Tag.AlbumSort);
            Assert.Equal(1999u, f.Tag.Year); // kept
        }
        using (var f = Read(b))
        {
            Assert.Equal(2u, f.Tag.Track);
            Assert.Equal(2010u, f.Tag.Year);
        }
    }

    [Fact]
    public async Task Move_ReadOnlyFile_StillMoves_AndIsReported()
    {
        WriteMp3(Path.Combine("Target", "01.mp3"), "Target", "Artist", "Artist", track: 1);
        var ok = WriteMp3(Path.Combine("Old", "01.mp3"), "Old", "A", "A", track: 1);
        var locked = WriteMp3(Path.Combine("Old", "02.mp3"), "Old", "A", "A", track: 2);
        var (library, _) = await NewLibraryAsync(3);
        File.SetAttributes(locked, FileAttributes.ReadOnly);
        var moving = library.AllTracks.Where(t => t.Album == "Old").OrderBy(t => t.TrackNumber).ToList();

        var result = await library.MoveToAlbumAsync(moving, "Target", "Artist");

        Assert.Equal(2, result.Grouped);
        Assert.Equal(new[] { "02.mp3" }, result.UnwrittenFiles);
        Assert.Equal(3, Assert.Single(library.Albums).Tracks.Count);
        using (var f = Read(ok)) Assert.Equal("Target", f.Tag.Album);
        using (var f = Read(locked)) Assert.Equal("Old", f.Tag.Album);
        Assert.True(library.AllTracks.Single(t => t.FilePath == locked).AlbumGroupingUnwritten); // retried later
    }

    [Fact]
    public async Task Move_SongsAlreadyOnTheAlbum_AreLeftAlone()
    {
        var own = WriteMp3(Path.Combine("Target", "01.mp3"), "Target", "Artist", "Artist", track: 1);
        WriteMp3(Path.Combine("Old", "01.mp3"), "Old", "A", "A", track: 4);
        var (library, _) = await NewLibraryAsync(2);
        var before = File.GetLastWriteTimeUtc(own);

        var result = await library.MoveToAlbumAsync(library.AllTracks.ToList(), "Target", "Artist");

        Assert.Equal(1, result.Grouped);
        Assert.Equal(before, File.GetLastWriteTimeUtc(own));
        Assert.Equal(new[] { 1, 2 }, Assert.Single(library.Albums).Tracks.Select(t => t.TrackNumber));
    }

    // ── Dialog, flow and menu ──

    private static Album AlbumOf(params Track[] tracks)
    {
        foreach (var t in tracks)
        {
            if (t.Id == Guid.Empty) t.Id = Guid.NewGuid();
            t.AlbumId = Track.ComputeAlbumId(t.AlbumArtist, t.Album);
        }
        return new Album { Id = tracks[0].AlbumId, Name = tracks[0].Album, Artist = tracks[0].AlbumArtist, Tracks = tracks.ToList() };
    }

    private static (Album Current, Album Other, Album Unknown, Track Song) Albums()
    {
        var song = T("Old", 1);
        var current = AlbumOf(song, T("Old", 2));
        var other = AlbumOf(T("Target", 1), T("Target", 2));
        var unknown = AlbumOf(new Track { Title = "x", Album = "Unknown Album", AlbumArtist = "Unknown Artist" });
        return (current, other, unknown, song);
    }

    [Fact]
    public void Dialog_ListsRealAlbums_MutesTheCurrentOne_AndMovesIntoThePickedOne()
    {
        var (current, other, unknown, song) = Albums();
        var vm = new MoveToAlbumViewModel(new[] { song }, new[] { other, unknown, current });
        MoveToAlbumTarget? moved = null;
        var closed = 0;
        vm.Confirmed += (_, t) => moved = t;
        vm.CloseRequested += (_, _) => closed++;

        Assert.Equal(new[] { "Old", "Target" }, vm.Rows.Select(r => r.Name));
        Assert.True(vm.Rows[0].IsCurrent);
        vm.SelectAlbumCommand.Execute(vm.Rows[0]);
        Assert.Null(vm.SelectedRow); // the album it is on already is not a target
        Assert.False(vm.MoveCommand.CanExecute(null));

        vm.SearchText = "targ";
        var row = Assert.Single(vm.Rows);
        vm.SelectAlbumCommand.Execute(row);
        Assert.True(row.IsSelected);
        Assert.Contains("Target", vm.ConfirmText);
        Assert.True(vm.MoveCommand.CanExecute(null));
        vm.MoveCommand.Execute(null);

        Assert.Equal(new MoveToAlbumTarget("Target", "Artist", IsNew: false), moved);
        Assert.Equal(1, closed);
    }

    [Fact]
    public void Dialog_NewAlbum_NeedsAName_AndPrefillsTheAlbumArtist()
    {
        var (current, other, _, song) = Albums();
        var second = current.Tracks[1];
        var vm = new MoveToAlbumViewModel(new[] { song, second }, new[] { current, other });
        MoveToAlbumTarget? moved = null;
        vm.Confirmed += (_, t) => moved = t;

        vm.ShowCreateCommand.Execute(null);
        Assert.Equal("Artist", vm.NewAlbumArtist);
        Assert.False(vm.MoveCommand.CanExecute(null));
        vm.MoveCommand.Execute(null); // Enter in the empty field
        Assert.Null(moved);

        vm.NewAlbumName = "  My Mix ";
        Assert.Contains("My Mix", vm.ConfirmText);
        Assert.Contains("2", vm.ConfirmText);
        vm.MoveCommand.Execute(null);

        Assert.Equal(new MoveToAlbumTarget("My Mix", "Artist", IsNew: true), moved);
    }

    [AvaloniaFact]
    public async Task Flow_ConfirmCallsTheLibrary_AndReportsFilesNotWritten()
    {
        var (current, other, _, song) = Albums();
        var lib = new FakeLibraryService { MoveResult = new AlbumGroupResult(other.Id, 1, new[] { "1-1.mp3" }) };
        ((List<Album>)lib.Albums).AddRange(new[] { current, other });
        ConfirmationRequest? notice = null;

        var result = await MoveToAlbumFlow.RunAsync(new[] { song }, lib,
            vm =>
            {
                vm.SelectAlbumCommand.Execute(vm.Rows.Single(r => r.Name == "Target"));
                vm.MoveCommand.Execute(null);
                return Task.CompletedTask;
            },
            r => { notice = r; return Task.FromResult(new ConfirmationResult(true, false)); });

        var call = Assert.Single(lib.MoveCalls);
        Assert.Equal(new[] { song }, call.Tracks);
        Assert.Equal(("Target", "Artist"), (call.Album, call.AlbumArtist));
        Assert.Equal(1, result!.Grouped);
        Assert.NotNull(notice);
        Assert.True(notice!.InfoOnly);
        Assert.Equal(new[] { "1-1.mp3" }, notice.Details);
    }

    [AvaloniaFact]
    public async Task Flow_Cancelled_MovesNothing()
    {
        var (current, other, _, song) = Albums();
        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[] { current, other });

        var result = await MoveToAlbumFlow.RunAsync(new[] { song }, lib,
            vm => { vm.CancelCommand.Execute(null); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("no notice expected"));

        Assert.Null(result);
        Assert.Empty(lib.MoveCalls);
    }

    [AvaloniaFact]
    public void Dialog_View_PickARow_ThenMove_RaisesConfirmed()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var (current, other, _, song) = Albums();
        var vm = new MoveToAlbumViewModel(new[] { song }, new[] { current, other });
        MoveToAlbumTarget? moved = null;
        vm.Confirmed += (_, t) => moved = t;
        var win = new MoveToAlbumDialog(vm) { Width = 1100, Height = 820 };
        win.Show();
        try
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var move = win.FindControl<Button>("MoveButton")!;
            Assert.False(move.IsEffectivelyEnabled);
            Assert.True(win.FindControl<TextBox>("SearchBox")!.IsEffectivelyVisible);

            vm.SelectAlbumCommand.Execute(vm.Rows.Single(r => r.Name == "Target"));
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(move.IsEffectivelyEnabled);
            move.Command!.Execute(null);

            Assert.Equal("Target", moved!.Album);
        }
        finally { win.Close(); }
    }

    [AvaloniaFact]
    public void SongMenu_MoveToAlbum_IsHiddenUnlessWired_AndRunsWithTheClickedSong()
    {
        var none = new RelayCommand(() => { });
        var builder = new TrackContextMenuBuilder();
        var host = new Border();
        foreach (var key in new[] { "HeartFillIcon", "StarIcon", "TrashIcon" })
            host.Resources[key] = Avalonia.Media.Geometry.Parse("M0 0L1 1");
        builder.Build("Remove from Library", null, host);
        var song = T("Old", 1);

        builder.Bind(song, none, none, none, none, none, none, none, none, none, none);
        Assert.False(builder.MoveToAlbum.IsVisible);

        Track? ran = null;
        builder.Bind(song, none, none, none, none, none, none, none, none, none, none,
            moveToAlbumCommand: new RelayCommand<Track>(t => ran = t));
        Assert.True(builder.MoveToAlbum.IsVisible);
        builder.MoveToAlbum.Command!.Execute(builder.MoveToAlbum.CommandParameter);
        Assert.Same(song, ran);
    }

    /// <summary>The song lists, the album page and playlists wire the row; the playback bar's
    /// "…" menu offers it for the playing song.</summary>
    [Fact]
    public void Views_WireMoveToAlbum()
    {
        var root = RepoRoot();
        foreach (var view in new[] { "LibrarySongsView.axaml.cs", "AlbumDetailView.axaml.cs", "PlaylistView.axaml.cs" })
            Assert.Contains("moveToAlbumCommand: vm.MoveToAlbumCommand",
                File.ReadAllText(Path.Combine(root, "src", "Noctis", "Views", view)));
        Assert.Contains("MoveCurrentTrackToAlbumCommand",
            File.ReadAllText(Path.Combine(root, "src", "Noctis", "Views", "PlaybackBarView.axaml")));
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Noctis.sln not found");
    }
}
