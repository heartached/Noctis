using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #117 (ngoomie): with "," removed from the artist separators, multi-value artist
/// tags still came back joined with a hard-coded ", " — "Dìyù, Lena" never split, so every
/// credit combination became its own artist — and saving split on a hard-coded "," and ";",
/// cutting "Earth, Wind &amp; Fire" in two. Reads now join with the active separator, writes
/// split on the active symbol separators, and an existing library is re-read once when the
/// join it was stored with no longer matches.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ArtistCreditJoinTests : IDisposable
{
    private static readonly string[] SemicolonOnly = { ";", "feat.", "ft.", "featuring" };
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ArtistCreditJoinTests() => ArtistCredit.ResetToDefaults();

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string WriteMp3(string name, string[] performers, string[]? albumArtists = null)
    {
        var path = Path.Combine(_root, "music", name + ".mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // One MPEG-1 Layer III frame repeated (see FileSystemSourceScanTests).
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = name;
        f.Tag.Performers = performers;
        if (albumArtists != null) f.Tag.AlbumArtists = albumArtists;
        f.Tag.Album = name + " Album";
        f.Save();
        return path;
    }

    private static string[] ReadPerformers(string path)
    {
        using var f = TagLib.File.Create(path);
        return f.Tag.Performers;
    }

    // ── Read: multi-value tags join with the active separator ──

    [Fact]
    public void MultiValueArtist_DefaultSeparators_JoinsWithComma()
    {
        var path = WriteMp3("multi", new[] { "Dìyù", "Lena" });

        var track = new MetadataService().ReadTrackMetadata(path)!;

        Assert.Equal("Dìyù, Lena", track.Artist);
        Assert.Equal(new[] { "Dìyù", "Lena" }, ArtistCredit.Split(track.Artist));
    }

    [Fact]
    public void MultiValueArtist_SemicolonOnly_JoinsWithSemicolon_AndSplitsBack()
    {
        ArtistCredit.Configure(ArtistGroupMode.Artist, SemicolonOnly);
        var path = WriteMp3("multi", new[] { "Dìyù", "Lena", "Demonbitch" }, new[] { "Dìyù", "Lena" });

        var track = new MetadataService().ReadTrackMetadata(path)!;

        Assert.Equal("Dìyù; Lena; Demonbitch", track.Artist);
        Assert.Equal(new[] { "Dìyù", "Lena", "Demonbitch" }, ArtistCredit.Split(track.Artist));
        Assert.Equal("Dìyù", track.PrimaryArtist);
        Assert.Equal("Dìyù; Lena", track.AlbumArtist);
    }

    // ── Write: split on the active symbol separators ──

    [Fact]
    public void EarthWindAndFire_SemicolonOnly_SurvivesReadAndWrite()
    {
        ArtistCredit.Configure(ArtistGroupMode.Artist, SemicolonOnly);
        var path = WriteMp3("ewf", new[] { "Earth, Wind & Fire" });
        var metadata = new MetadataService();

        var track = metadata.ReadTrackMetadata(path)!;
        Assert.Equal("Earth, Wind & Fire", track.Artist);
        Assert.True(metadata.WriteTrackMetadata(track));

        Assert.Equal(new[] { "Earth, Wind & Fire" }, ReadPerformers(path));
        Assert.Equal("Earth, Wind & Fire", metadata.ReadTrackMetadata(path)!.Artist);
    }

    [Fact]
    public void Write_SemicolonOnly_SplitsOnSemicolonNotComma()
    {
        ArtistCredit.Configure(ArtistGroupMode.Artist, SemicolonOnly);
        var path = WriteMp3("collab", new[] { "Placeholder" });
        var metadata = new MetadataService();
        var track = metadata.ReadTrackMetadata(path)!;

        track.Artist = "Earth, Wind & Fire; Dìyù";
        Assert.True(metadata.WriteTrackMetadata(track));

        Assert.Equal(new[] { "Earth, Wind & Fire", "Dìyù" }, ReadPerformers(path));
        Assert.Equal("Earth, Wind & Fire; Dìyù", metadata.ReadTrackMetadata(path)!.Artist);
    }

    [Fact]
    public void Write_DefaultSeparators_RoundTripsMultiValueArtist()
    {
        var path = WriteMp3("multi", new[] { "Dìyù", "Lena" });
        var metadata = new MetadataService();
        var track = metadata.ReadTrackMetadata(path)!;

        Assert.True(metadata.WriteTrackMetadata(track));

        Assert.Equal(new[] { "Dìyù", "Lena" }, ReadPerformers(path));
    }

    [Theory]
    [InlineData("A, B; C", new[] { "A", "B", "C" })]
    [InlineData("A / B", new[] { "A", "B" })]
    [InlineData("AC/DC", new[] { "AC/DC" })]                 // a tight slash is part of the name
    [InlineData("Rihanna feat. Drake", new[] { "Rihanna feat. Drake" })] // words stay one credit
    [InlineData("A,,B", new[] { "A", "B" })]
    [InlineData("", new string[0])]
    public void SplitForTag_DefaultSeparators_SplitsOnSymbolsOnly(string value, string[] expected)
        => Assert.Equal(expected, ArtistCredit.SplitForTag(value));

    [Fact]
    public void SplitForTag_FollowsTheActiveSeparators()
    {
        ArtistCredit.Configure(ArtistGroupMode.Artist, SemicolonOnly);
        Assert.Equal(new[] { "Earth, Wind & Fire", "A / B" }, ArtistCredit.SplitForTag("Earth, Wind & Fire; A / B"));
    }

    // ── Existing libraries: re-read once when the stored join is stale ──

    [Fact]
    public async Task ExistingLibrary_SeparatorChange_RereadsStaleCredits_KeepingUserState()
    {
        var multi = WriteMp3("multi", new[] { "Dìyù", "Lena" }, new[] { "Dìyù", "Lena" });
        WriteMp3("ewf", new[] { "Earth, Wind & Fire" });
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { Path.Combine(_root, "music") } });
        var library = new LibraryService(new MetadataService(), persistence,
            new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());

        // Scanned by an older build / under the defaults: stored with ", ".
        await library.ScanAsync(new[] { Path.Combine(_root, "music") });
        var track = library.AllTracks.Single(t => t.FilePath == multi);
        Assert.Equal("Dìyù, Lena", track.Artist);
        track.IsFavorite = true;
        track.PlayCount = 9;
        track.Rating = 4;
        await library.SaveTrackUserStateAsync(new[] { track });
        var oldAlbumId = track.AlbumId;
        persistence.SaveArtwork(oldAlbumId, new byte[] { 1, 2, 3 });

        // The defaults still match the recorded ", " join: nothing to do.
        Assert.Equal(0, await library.ApplyArtistCreditJoinAsync());

        ArtistCredit.Configure(ArtistGroupMode.Artist, SemicolonOnly);
        var changed = await library.ApplyArtistCreditJoinAsync();

        Assert.Equal(1, changed); // "Earth, Wind & Fire" is re-read but reads back the same
        Assert.Equal("Dìyù; Lena", track.Artist);
        Assert.Equal("Dìyù; Lena", track.AlbumArtist);
        Assert.Equal(Track.ComputeAlbumId("Dìyù; Lena", track.Album), track.AlbumId);
        Assert.True(track.IsFavorite);
        Assert.Equal(9, track.PlayCount);
        Assert.Equal(4, track.Rating);
        Assert.True(File.Exists(persistence.GetArtworkPath(track.AlbumId)));
        Assert.Equal("Earth, Wind & Fire", library.AllTracks.Single(t => t.FilePath != multi).Artist);
        Assert.Contains(library.Artists, a => a.Name == "Dìyù");
        Assert.DoesNotContain(library.Artists, a => a.Name == "Dìyù, Lena");
        Assert.Equal("; ", (await persistence.LoadSettingsAsync()).ArtistCreditJoin);

        // Recorded: a second pass is a no-op, and a rescan of the unchanged file keeps it.
        Assert.Equal(0, await library.ApplyArtistCreditJoinAsync());
        await library.ScanAsync(new[] { Path.Combine(_root, "music") });
        var rescanned = library.AllTracks.Single(t => t.FilePath == multi);
        Assert.Equal("Dìyù; Lena", rescanned.Artist);
        Assert.True(rescanned.IsFavorite);
    }
}
