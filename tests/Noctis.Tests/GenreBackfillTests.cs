using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The v12 metadata-schema migration (GitHub #123 follow-up, 2026-10-10): tracks indexed
/// before multi-genre support hold only their file's first genre, and a rescan never re-reads
/// them (LibraryService skips a file whose mtime and size are unchanged). One light, resumable
/// pass re-reads the genres of single-genre rows; only files with several genres change.
/// </summary>
public class GenreBackfillTests : IDisposable
{
    private readonly FolderMetadataBackfillTests.BackfillTestPersistence _persistence = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public GenreBackfillTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        _persistence.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class CountingMetadata : MetadataService, IMetadataService
    {
        public readonly List<string> GenreReads = new();

        string IMetadataService.ReadGenres(string filePath)
        {
            lock (GenreReads) GenreReads.Add(Path.GetFileName(filePath));
            return ReadGenres(filePath);
        }
    }

    private string File(string format, string name, params string[] genres)
    {
        var dir = Path.Combine(_dir, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var created = format == "flac" ? ReplayGainWriteTests.CreateFlac(dir) : ReplayGainWriteTests.CreateMp3WithId3v2Only(dir);
        var path = Path.Combine(_dir, name);
        System.IO.File.Move(created, path);
        using var f = TagLib.File.Create(path);
        if (format == "flac")
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("GENRE", genres);
        else
        {
            var id3 = (TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true);
            id3.Version = 4;
            TagLib.Id3v2.TextInformationFrame.Get(id3, "TCON", true).Text = genres;
        }
        f.Save();
        return path;
    }

    private static Track Row(string path, string genre) => new()
    {
        Id = Guid.NewGuid(),
        FilePath = path,
        Title = Path.GetFileNameWithoutExtension(path),
        Artist = "Silk Sonic",
        AlbumArtist = "Silk Sonic",
        Album = "An Evening with Silk Sonic",
        AlbumId = Track.ComputeAlbumId("Silk Sonic", "An Evening with Silk Sonic"),
        Genre = genre,
        Duration = TimeSpan.FromMinutes(4),
        FileSize = 1000,
        SourceType = SourceType.Local,
        LastModified = DateTime.UtcNow,
        DateAdded = DateTime.UtcNow,
    };

    private async Task<CountingMetadata> LaunchAsync()
    {
        var metadata = new CountingMetadata();
        var library = new LibraryService(metadata, _persistence, new SqliteLibraryIndexService(_persistence),
            new FolderMetadataBackfillTests.FakeAuditTrail());
        await library.LoadAsync();
        await library.BackgroundInit;
        await library.LabelBackfill;
        return metadata;
    }

    [Fact]
    public async Task Load_V11Library_ReadsEveryGenreOfMultiGenreFiles_Once()
    {
        var flac = Row(File("flac", "a.flac", "Rock", "Pop"), "Rock");
        var mp3 = Row(File("mp3", "b.mp3", "R&B/Soul", "Funk"), " R&B/Soul");
        var single = Row(File("flac", "c.flac", "Jazz"), "Jazz");
        var current = Row(File("flac", "d.flac", "Rock", "Pop"), "Rock; Pop");
        var untagged = Row(File("flac", "e.flac"), "");
        var edited = Row(File("flac", "f.flac", "Rock", "Pop"), "Jazz"); // re-tagged in Noctis since
        _persistence.LibraryTracks.AddRange(new[] { flac, mp3, single, current, untagged, edited });
        _persistence.Settings.MetadataSchemaVersion = 11;

        var first = await LaunchAsync();

        Assert.Equal("Rock; Pop", flac.Genre);
        Assert.Equal("R&B/Soul; Funk", mp3.Genre);
        Assert.Equal("Jazz", single.Genre);
        Assert.Equal("Rock; Pop", current.Genre);
        Assert.Equal("", untagged.Genre);
        Assert.Equal("Jazz", edited.Genre);
        // Only rows holding exactly one genre are read.
        Assert.Equal(new[] { "a.flac", "b.mp3", "c.flac", "f.flac" }, first.GenreReads.OrderBy(n => n, StringComparer.Ordinal));
        Assert.Equal(12, _persistence.Settings.MetadataSchemaVersion);
        Assert.False(System.IO.File.Exists(Path.Combine(_persistence.DataDirectory, "genre-backfill.progress")));

        var second = await LaunchAsync();
        Assert.Empty(second.GenreReads);
    }

    [Fact]
    public async Task Load_V10Library_RunsTheLabelPassThenTheGenrePass()
    {
        var flac = Row(File("flac", "a.flac", "Rock", "Pop"), "Rock");
        _persistence.LibraryTracks.Add(flac);
        _persistence.Settings.MetadataSchemaVersion = 10;

        await LaunchAsync();

        Assert.Equal("Rock; Pop", flac.Genre);
        Assert.Equal(12, _persistence.Settings.MetadataSchemaVersion);
    }

    [Fact]
    public void ReadGenres_MatchesTheFullRead()
    {
        var paths = new[]
        {
            File("flac", "a.flac", "Rock", " Pop", "rock"),
            File("flac", "b.flac", "Jazz"),
            File("flac", "c.flac"),
            File("mp3", "d.mp3", "Rock", "Pop"),
        };
        var metadata = new MetadataService();
        foreach (var path in paths)
            Assert.Equal(metadata.ReadTrackMetadata(path)!.Genre, metadata.ReadGenres(path));
        Assert.Equal("Rock; Pop", metadata.ReadGenres(paths[0]));
        Assert.Equal("", metadata.ReadGenres(Path.Combine(_dir, "missing.flac")));
    }
}
