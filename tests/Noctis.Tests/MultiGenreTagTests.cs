using System.Text;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #123 follow-up (2026-10-10): a file with several genres (repeated Vorbis GENRE
/// fields, multi-value ID3v2.4 TCON, several M4A ©gen values) was read as its FIRST genre
/// only, and editing the genre box wrote one value back, so the other genres were lost;
/// "Rock; Pop" typed into the box was saved as one literal genre. Real temp files only.
/// </summary>
public class MultiGenreTagTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public MultiGenreTagTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    // ── Files ──

    /// <summary>A FLAC, MP3 (ID3v2.4 only) or M4A in its own folder, its genre field holding
    /// <paramref name="genres"/> as separate values in the format's native way.</summary>
    private string Create(string format, params string[] genres)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var path = format switch
        {
            "flac" => ReplayGainWriteTests.CreateFlac(dir),
            "mp3" => ReplayGainWriteTests.CreateMp3WithId3v2Only(dir),
            "m4a" => CreateM4a(dir),
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
        using var f = TagLib.File.Create(path);
        f.Tag.Title = "Song";
        f.Tag.Performers = new[] { "Artist" };
        f.Tag.Album = "Album";
        switch (format)
        {
            case "flac":
                ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("GENRE", genres);
                break;
            case "mp3":
                var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
                id3.Version = 4;
                TagLib.Id3v2.TextInformationFrame.Get(id3, "TCON", true).Text = genres;
                break;
            case "m4a":
                ((TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, true)).SetText(GenBox, genres);
                break;
        }
        f.Save();
        return path;
    }

    // The ©gen atom (TagLib's BoxType is internal).
    private static readonly TagLib.ByteVector GenBox = new(new byte[] { 0xA9, (byte)'g', (byte)'e', (byte)'n' });

    /// <summary>The genre values exactly as stored in the format's own genre field.</summary>
    private static string[] RawGenres(string path)
    {
        using var f = TagLib.File.Create(path);
        return Path.GetExtension(path) switch
        {
            ".flac" => ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, false)).GetField("GENRE"),
            ".mp3" => ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false))
                .GetFrames<TagLib.Id3v2.TextInformationFrame>("TCON").SelectMany(fr => fr.Text).ToArray(),
            ".m4a" => ((TagLib.Mpeg4.AppleTag)f.GetTag(TagLib.TagTypes.Apple, false)).GetText(GenBox),
            _ => throw new ArgumentOutOfRangeException(nameof(path)),
        };
    }

    /// <summary>The genres TagLib reports (ID3v1 numeric genres mapped back to names).</summary>
    private static string[] Genres(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.Genres;
    }

    private static int Id3Version(string path)
    {
        using var f = TagLib.File.Create(path);
        return ((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, false)).Version;
    }

    // Smallest MP4 TagLib# opens: ftyp + moov/mvhd + mdat. TagLib adds udta/meta/ilst on save.
    internal static string CreateM4a(string dir)
    {
        static byte[] Box(string type, byte[] payload)
        {
            var size = 8 + payload.Length;
            var b = new byte[size];
            b[0] = (byte)(size >> 24); b[1] = (byte)(size >> 16); b[2] = (byte)(size >> 8); b[3] = (byte)size;
            Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
            payload.CopyTo(b, 8);
            return b;
        }

        var mvhd = new byte[100];          // version 0
        mvhd[15] = 0xE8; mvhd[14] = 0x03;  // timescale 1000
        mvhd[19] = 0xE8; mvhd[18] = 0x03;  // duration 1000 (1 s)
        mvhd[21] = 0x01;                   // rate 1.0
        mvhd[24] = 0x01;                   // volume 1.0
        mvhd[99] = 0x02;                   // next track id
        var path = Path.Combine(dir, "song.m4a");
        var bytes = new List<byte>();
        bytes.AddRange(Box("ftyp", Encoding.ASCII.GetBytes("M4A \0\0\0\0M4A mp42isom")));
        bytes.AddRange(Box("moov", Box("mvhd", mvhd)));
        bytes.AddRange(Box("mdat", new byte[64]));
        File.WriteAllBytes(path, bytes.ToArray());
        return path;
    }

    private static async Task<(MetadataViewModel Vm, Track Track)> OpenEditor(string path, string? libraryGenre = null)
    {
        var track = new MetadataService().ReadTrackMetadata(path)!;
        if (libraryGenre != null) track.Genre = libraryGenre; // a row from before the fix
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new MetadataService(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService());
        await vm.InitializeAsync();
        return (vm, track);
    }

    // ── Read (the scanner's reader) ──

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void Read_KeepsEveryGenre_JoinedWithSemicolons(string format)
    {
        var path = Create(format, "Rock", "Pop");
        Assert.Equal(new[] { "Rock", "Pop" }, Genres(path));

        var track = new MetadataService().ReadTrackMetadata(path);
        _o.WriteLine($"{format}: raw=[{string.Join("|", RawGenres(path))}] Track.Genre='{track?.Genre}'");
        Assert.NotNull(track);
        Assert.Equal("Rock; Pop", track!.Genre);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void Read_TrimsAndDedupesGenres(string format)
    {
        var path = Create(format, " Rock", "rock ", "Pop ", "");
        var track = new MetadataService().ReadTrackMetadata(path)!;
        _o.WriteLine($"{format}: raw=[{string.Join("|", RawGenres(path))}] Track.Genre='{track.Genre}'");
        Assert.Equal("Rock; Pop", track.Genre);
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void Read_SingleGenre_IsUnchanged(string format)
    {
        var path = Create(format, "Rock");
        Assert.Equal("Rock", new MetadataService().ReadTrackMetadata(path)!.Genre);
    }

    // ── Save through the metadata editor ──

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task EditorSave_GenreUntouched_KeepsEveryGenreValue(string format)
    {
        var path = Create(format, "Rock", "Pop");
        var before = RawGenres(path);
        var (vm, track) = await OpenEditor(path);

        vm.Title = "Renamed";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        _o.WriteLine($"{format}: before=[{string.Join("|", before)}] after=[{string.Join("|", RawGenres(path))}]");
        Assert.Equal("Renamed", new MetadataService().ReadTrackMetadata(path)!.Title);
        Assert.Equal(before, RawGenres(path));
        Assert.Equal(new[] { "Rock", "Pop" }, Genres(path));
        Assert.Equal("Rock; Pop", track.Genre);
        if (format == "mp3") Assert.Equal(4, Id3Version(path));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task EditorSave_EditedGenres_WritesEachAsItsOwnValue(string format)
    {
        var path = Create(format, "Rock", "Pop");
        var (vm, track) = await OpenEditor(path);

        vm.Genre = " Jazz ;; Rock;jazz ; Blues; ";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        _o.WriteLine($"{format}: after=[{string.Join("|", RawGenres(path))}] Track.Genre='{track.Genre}'");
        Assert.Equal(new[] { "Jazz", "Rock", "Blues" }, Genres(path));
        Assert.Equal(3, RawGenres(path).Length);
        Assert.Equal("Jazz; Rock; Blues", track.Genre);
        Assert.Equal("Jazz; Rock; Blues", new MetadataService().ReadTrackMetadata(path)!.Genre);
        if (format == "mp3") Assert.Equal(4, Id3Version(path));
    }

    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task EditorSave_SingleGenre_UntouchedAndEdited(string format)
    {
        var path = Create(format, "Rock");
        var before = RawGenres(path);
        var (vm, track) = await OpenEditor(path);
        vm.Title = "Renamed";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(before, RawGenres(path));

        (vm, track) = await OpenEditor(path);
        vm.Genre = "Pop";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Pop" }, Genres(path));
        Assert.Equal("Pop", track.Genre);

        (vm, track) = await OpenEditor(path);
        vm.Genre = "  ";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Empty(Genres(path));
        Assert.Equal(string.Empty, track.Genre);
    }

    /// <summary>The user cuts "Rock; Pop" down to "Rock": a real edit, written even though the
    /// result is the file's first genre.</summary>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public async Task EditorSave_ReducedToTheFirstGenre_IsWritten(string format)
    {
        var path = Create(format, "Rock", "Pop");
        var (vm, track) = await OpenEditor(path);
        Assert.Equal("Rock; Pop", vm.Genre);

        vm.Genre = "Rock";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.Equal(new[] { "Rock" }, Genres(path));
        Assert.Equal("Rock", track.Genre);
    }

    /// <summary>A library row from before the fix holds "Rock" only; the editor shows the
    /// file's full list, an untouched save keeps it, and an edit starts from it.</summary>
    [Fact]
    public async Task Editor_StaleLibraryRow_ShowsTheFilesGenres()
    {
        var path = Create("flac", "Rock", "Pop");
        var (vm, track) = await OpenEditor(path, libraryGenre: "Rock");
        Assert.Equal("Rock; Pop", vm.Genre);

        vm.Title = "Renamed";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Rock", "Pop" }, Genres(path));
        Assert.Equal("Rock; Pop", track.Genre);

        (vm, track) = await OpenEditor(path, libraryGenre: "Rock");
        vm.Genre = "Rock";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(new[] { "Rock" }, Genres(path));
    }

    [Fact]
    public async Task AlbumEditor_ReducedToTheFirstGenre_IsWrittenToEveryTrack()
    {
        var paths = new[] { Create("flac", "Rock", "Pop"), Create("mp3", "Rock", "Pop") };
        var svc = new MetadataService();
        var tracks = paths.Select(p => svc.ReadTrackMetadata(p)!).ToList();
        foreach (var t in tracks) { t.Album = "Album"; t.AlbumArtist = "Artist"; }
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var vm = new MetadataViewModel(tracks[0], svc, lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), albumScoped: true, albumTracks: tracks);
        await vm.InitializeAsync();
        Assert.Equal("Rock; Pop", vm.Genre);

        vm.Genre = "Rock";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        foreach (var p in paths) Assert.Equal(new[] { "Rock" }, Genres(p));
    }

    // ── Save through other writers (lyrics, ratings, converter) ──

    /// <summary>Outside the editor a row holding only some of the file's genres (or none)
    /// never shrinks the file's list.</summary>
    [Theory]
    [InlineData("Pop")]
    [InlineData("pop; ROCK")]
    [InlineData("")]
    public void WriteTrackMetadata_RowWithFewerGenres_KeepsEveryGenre(string rowGenre)
    {
        var path = Create("flac", "Rock", "Pop", "Jazz");
        var svc = new MetadataService();
        var track = svc.ReadTrackMetadata(path)!;
        track.Genre = rowGenre;
        track.Lyrics = "la la";

        Assert.True(svc.WriteTrackMetadata(track));
        Assert.Equal(new[] { "Rock", "Pop", "Jazz" }, RawGenres(path));
    }

    /// <summary>A row whose genres the file lacks (a new download, a converted copy) still
    /// writes them.</summary>
    [Fact]
    public void WriteTrackMetadata_NewGenreOutsideTheFile_IsStillWritten()
    {
        var path = Create("flac");
        var svc = new MetadataService();
        var track = svc.ReadTrackMetadata(path)!;
        track.Genre = "Rock; Pop";
        Assert.True(svc.WriteTrackMetadata(track));
        Assert.Equal(new[] { "Rock", "Pop" }, RawGenres(path));
    }

    /// <summary>A library row read before multi-genre support holds only the file's first
    /// genre. Lyrics/rating saves pass that row to WriteTrackMetadata; it must not shrink the
    /// file's genre list to that one value.</summary>
    [Theory]
    [InlineData("flac")]
    [InlineData("mp3")]
    [InlineData("m4a")]
    public void WriteTrackMetadata_StaleFirstGenreRow_KeepsEveryGenre(string format)
    {
        var path = Create(format, "Rock", "Pop");
        var before = RawGenres(path);
        var svc = new MetadataService();
        var track = svc.ReadTrackMetadata(path)!;
        track.Genre = "Rock"; // what the pre-fix reader stored
        track.Lyrics = "la la";

        Assert.True(svc.WriteTrackMetadata(track));
        Assert.Equal(before, RawGenres(path));
    }

    [Fact]
    public void GenreList_SplitsJoinsAndNormalizes()
    {
        Assert.Equal(new[] { "Rock", "pop" }, Track.SplitGenres(" Rock ;pop;; ROCK ; Pop "));
        Assert.Empty(Track.SplitGenres(null));
        Assert.Empty(Track.SplitGenres(" ; "));
        Assert.Equal("Hip-Hop/Rap", Track.NormalizeGenre("Hip-Hop/Rap")); // '/' is part of a genre name
        Assert.Equal("Rock; Pop", Track.JoinGenres(new[] { "Rock", null, " rock", "Pop;rock" }));
        Assert.Equal(string.Empty, Track.JoinGenres(null));
    }

    // ── Consumers of the joined value ──

    [Fact]
    public void SmartPlaylist_GenreIs_MatchesAnyOfTheTracksGenres()
    {
        var rock = new Track { Title = "a", Genre = "Rock; Pop" };
        var jazz = new Track { Title = "b", Genre = "Jazz" };
        var playlist = new Playlist { IsSmartPlaylist = true, MatchAll = true };

        playlist.Rules = new() { new SmartPlaylistRule { Field = RuleField.Genre, Operator = RuleOperator.Equals, Value = "pop" } };
        Assert.Equal(new[] { rock }, SmartPlaylistEvaluator.Evaluate(playlist, new[] { rock, jazz }));

        playlist.Rules = new() { new SmartPlaylistRule { Field = RuleField.Genre, Operator = RuleOperator.Equals, Value = "Rock; Pop" } };
        Assert.Equal(new[] { rock }, SmartPlaylistEvaluator.Evaluate(playlist, new[] { rock, jazz }));
    }

    [Fact]
    public void WrapTopGenres_CountAPlayForEachOfItsGenres()
    {
        var both = new Track { Id = Guid.NewGuid(), Title = "a", Artist = "X", Genre = "Rock; Pop", Duration = TimeSpan.FromMinutes(3) };
        var pop = new Track { Id = Guid.NewGuid(), Title = "b", Artist = "Y", Genre = "pop", Duration = TimeSpan.FromMinutes(3) };
        var lib = new[] { both, pop }.ToDictionary(t => t.Id);
        var start = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local);
        var events = new[] { both, both, pop }.Select((t, i) => new PlayHistoryEvent
        {
            TrackId = t.Id, Title = t.Title, Artist = t.Artist, PlayedAtUtc = start.AddHours(i).ToUniversalTime(),
        }).ToList();

        var stats = WrapStatsBuilder.Build(events, lib, 2026);

        Assert.Equal(
            new[] { ("Pop", 3, 1.0), ("Rock", 2, 2 / 3.0) },
            stats.TopGenres.Select(g => (g.Name, g.Plays, g.Share)).ToArray());
    }

    [Fact]
    public void Autoplay_GenreTier_MatchesAnySharedGenre()
    {
        var seed = new Track { Title = "seed", Artist = "S", Genre = "Rock; Pop" };
        var rock = new Track { Title = "r", Artist = "X", Genre = "rock" };
        var pop = new Track { Title = "p", Artist = "Y", Genre = "Jazz; Pop" };
        var jazz = new Track { Title = "j", Artist = "Z", Genre = "Jazz" };

        var picks = new AutoplayService().PickSimilar(seed, new[] { seed, rock, pop, jazz }, 10, new HashSet<Guid>());

        Assert.Equal(new[] { "p", "r" }, picks.Select(t => t.Title).OrderBy(t => t));
    }

    [Fact]
    public void Radio_SameGenreScore_MatchesAnySharedGenre()
    {
        var seed = new Track { Title = "seed", Artist = "S", AlbumArtist = "S", Genre = "Rock; Pop" };
        var pop = new Track { Title = "p", Artist = "Y", AlbumArtist = "Y", Genre = "Pop" };
        var jazz = new Track { Title = "j", Artist = "Z", AlbumArtist = "Z", Genre = "Jazz" };

        var picks = new RadioService().BuildSimilar(seed, new[] { seed, pop, jazz }, 10, new HashSet<Guid>());

        Assert.Equal(new[] { pop }, picks);
    }

    [Fact]
    public void OrganizeGenreToken_UsesTheFirstGenre()
    {
        var track = new Track { Title = "Song", Genre = "Rock; Pop", FilePath = Path.Combine(_dir, "in", "a.flac") };
        var root = Path.Combine(_dir, "out");

        var move = Assert.Single(FileOrganizePlanner.Plan(new[] { track }, "{Genre}/{Title}", root, _ => false));

        Assert.Equal(Path.Combine(root, "Rock", "Song.flac"), move.TargetPath);
    }

    [Fact]
    public void AlbumChips_ShowEachGenre()
    {
        var album = new Album { Name = "A", Genre = "Rock; Pop", TrackCount = 2 };
        Assert.Equal(new[] { "Rock", "Pop", "2 songs" }, Noctis.Views.DescriptionDialogs.AlbumChips(album));
    }

    [Fact]
    public void ArtistDominantGenre_CountsEachGenre()
    {
        var songs = new[]
        {
            new Track { Genre = "Rock; Pop" },
            new Track { Genre = "Pop" },
            new Track { Genre = "Jazz; pop" },
        };
        Assert.Equal("POP", ArtistDetailViewModel.DominantGenre(songs));
    }
}
