using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #187: "Show artists as written in the tags" (Settings › Library), and the lyrics
/// page / album header joining every credit with a hard-coded ", " while the Songs list kept
/// "feat." ("Metro Boomin feat. Drake" read "Metro Boomin, Drake" on the lyrics page).
/// </summary>
[Collection("ArtistCredit global configuration")]
public class ArtistTags187Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ArtistTags187Tests() => ArtistCredit.ResetToDefaults();

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>What the lyrics page / album header render: each name, then its separator.</summary>
    private static string Rendered(ArtistTokenItem[] tokens)
        => string.Concat(tokens.Select(t => t.Name + (t.IsLast ? "" : t.Separator)));

    // ── Formatter: default vs as tagged ──

    [Theory]
    [InlineData("Kraak & Smaak", "Kraak & Smaak", "Kraak & Smaak")]          // "&" is no default separator
    [InlineData("Metro Boomin feat. Drake", "Metro Boomin feat. Drake", "Metro Boomin feat. Drake")]
    [InlineData("Skrillex x Fred again..", "Skrillex x Fred again..", "Skrillex x Fred again..")]
    [InlineData("Rihanna; Drake", "Rihanna, Drake", "Rihanna; Drake")]
    [InlineData("Bad Bunny / Bomba Estéreo / J Balvin", "Bad Bunny, Bomba Estéreo, J Balvin", "Bad Bunny / Bomba Estéreo / J Balvin")]
    [InlineData("A,B", "A, B", "A,B")]
    [InlineData("AC/DC", "AC/DC", "AC/DC")]
    [InlineData("", "", "")]
    public void Display_DefaultReadsSymbolsAsCommas_AsTaggedKeepsTheText(string credit, string byDefault, string asTagged)
    {
        Assert.Equal(byDefault, ArtistCredit.Display(credit));
        Assert.Equal(byDefault, new Track { Artist = credit }.ArtistCreditDisplay);

        ArtistCredit.ShowAsTagged = true;
        Assert.Equal(asTagged, ArtistCredit.Display(credit));
        Assert.Equal(asTagged, new Track { Artist = credit }.ArtistCreditDisplay);
        Assert.Equal(asTagged, new Album { Artist = credit }.ArtistDisplay);
    }

    [Fact]
    public void AsTagged_IsDisplayOnly_SplittingIsUnchanged()
    {
        const string credit = "Rihanna; Drake feat. SZA";
        var names = ArtistCredit.Split(credit);
        ArtistCredit.ShowAsTagged = true;
        Assert.Equal(names, ArtistCredit.Split(credit));
        Assert.Equal(new[] { "Rihanna", "Drake", "SZA" }, names);
        Assert.Equal("Rihanna", Track.GetPrimaryArtist(credit));
    }

    [Fact]
    public void AsTagged_ShowsTheTagsCredit_NotNamesMergedFromTheTitle()
    {
        var track = new Track { Title = "Work (feat. Drake)", Artist = "Rihanna, Drake", ArtistAsTagged = "Rihanna" };
        Assert.Equal("Rihanna, Drake", track.ArtistCreditDisplay);
        ArtistCredit.ShowAsTagged = true;
        Assert.Equal("Rihanna", track.ArtistCreditDisplay);
        Assert.Equal("Rihanna", track.ArtistDisplay);
    }

    [Fact]
    public void AsTagged_StaleStoredCredit_FallsBackToTheIndexedOne()
    {
        // The credit was edited to something that no longer starts with the stored tag text.
        var track = new Track { Title = "Work (feat. Drake)", Artist = "Someone Else", ArtistAsTagged = "Rihanna" };
        ArtistCredit.ShowAsTagged = true;
        Assert.Equal("Someone Else", track.ArtistCreditDisplay);
    }

    [Fact]
    public void AsTagged_KeepsTheComposerSuffix()
    {
        var track = new Track { Artist = "A; B", Composer = "C", ShowComposerInAllViews = true };
        Assert.Equal("A, B — C", track.ArtistDisplay);
        ArtistCredit.ShowAsTagged = true;
        Assert.Equal("A; B — C", track.ArtistDisplay);
        Assert.Equal("A; B", track.ArtistCreditDisplay); // the island / Discord line never has it
    }

    [Fact]
    public void ResetToDefaults_SwitchesAsTaggedOff()
    {
        ArtistCredit.ShowAsTagged = true;
        ArtistCredit.ResetToDefaults();
        Assert.False(ArtistCredit.ShowAsTagged);
    }

    [Fact]
    public void QueueRow_ArtistAndAlbumArtistFields_FollowTheMode()
    {
        var track = new Track { Artist = "A; B", AlbumArtist = "A / B" };
        Assert.Equal("A, B", QueueRowLayout.Format(track, QueueRowField.Artist));
        Assert.Equal("A, B", QueueRowLayout.Format(track, QueueRowField.AlbumArtist));
        ArtistCredit.ShowAsTagged = true;
        Assert.Equal("A; B", QueueRowLayout.Format(track, QueueRowField.Artist));
        Assert.Equal("A / B", QueueRowLayout.Format(track, QueueRowField.AlbumArtist));
    }

    // ── The inconsistency: lyrics page / album header vs Songs list ──

    [Theory]
    [InlineData("Metro Boomin feat. Drake")]
    [InlineData("Kanye West, GLC, Consequence")]
    [InlineData("Rihanna; Drake / SZA")]
    [InlineData("Daft Punk ft. Pharrell Williams & Nile Rodgers")]
    [InlineData("Tyler, The Creator; Kali Uchis")]
    [InlineData("Kraak & Smaak")]
    public void LyricsHeader_RendersTheSameTextAsTheSongsList_InBothModes(string credit)
    {
        foreach (var asTagged in new[] { false, true })
        {
            ArtistCredit.ShowAsTagged = asTagged;
            var track = new Track { Artist = credit };
            var tokens = LyricsViewModel.BuildArtistTokens(track.ArtistCreditDisplay);
            Assert.Equal(track.ArtistDisplay, Rendered(tokens));
            Assert.Equal(ArtistCredit.Split(credit).Length == 0 ? 1 : ArtistCredit.Split(credit).Length, tokens.Length);
        }
    }

    [Fact]
    public void LyricsHeader_AsTagged_KeepsTheTagsSeparators()
    {
        ArtistCredit.ShowAsTagged = true;
        var tokens = LyricsViewModel.BuildArtistTokens(new Track { Artist = "Rihanna; Drake" }.ArtistCreditDisplay);
        Assert.Equal(new[] { "Rihanna", "Drake" }, tokens.Select(t => t.Name)); // links still per artist
        Assert.Equal("; ", tokens[0].Separator);
    }

    [Fact]
    public void AlbumHeader_AsTagged_KeepsTheTagsSeparators()
    {
        var album = new Album { Artist = "Simon & Garfunkel; Paul Simon" };
        ArtistCredit.ShowAsTagged = true;
        Assert.Equal("Simon & Garfunkel; Paul Simon", Rendered(ArtistTokenItem.FromCredit(album.ArtistDisplay)));
    }

    [Fact]
    public void ClickOnAsTaggedText_StillResolvesEachArtist()
    {
        const string shown = "Metro Boomin x Future feat. Drake";
        // "x" is no default separator: "Metro Boomin x Future" is one credited name.
        Assert.Equal("Metro Boomin x Future", ArtistCreditSpans.NameAt(shown, 2));
        Assert.Equal("Drake", ArtistCreditSpans.NameAt(shown, shown.IndexOf("Drake", StringComparison.Ordinal) + 1));
        Assert.Null(ArtistCreditSpans.NameAt(shown, shown.IndexOf("feat.", StringComparison.Ordinal) + 1));
    }

    // ── Scan, cache, old libraries ──

    [Fact]
    public void ArtistAsTaggedFor_OnlyWhenTheTitleNamesFeaturedArtists()
    {
        Assert.Equal("Rihanna", MetadataService.ArtistAsTaggedFor("Rihanna", "Work (feat. Drake)"));
        Assert.Null(MetadataService.ArtistAsTaggedFor("Kraak & Smaak", "Squeeze Me"));
    }

    [Fact]
    public void LibraryFile_WithoutTheField_Loads_AndNullIsNotWritten()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        };
        // As a build before #187 wrote it.
        const string old = "[{\"title\":\"Work (feat. Drake)\",\"artist\":\"Rihanna, Drake\",\"albumArtist\":\"Rihanna\"}]";
        var loaded = JsonSerializer.Deserialize<Track[]>(old, options)!;
        Assert.Null(loaded[0].ArtistAsTagged);
        Assert.Equal("Rihanna, Drake", loaded[0].ArtistTagText);

        Assert.DoesNotContain("artistAsTagged", JsonSerializer.Serialize(new Track { Artist = "A" }, options));
        var roundTrip = JsonSerializer.Deserialize<Track>(
            JsonSerializer.Serialize(new Track { Artist = "Rihanna, Drake", ArtistAsTagged = "Rihanna" }, options), options)!;
        Assert.Equal("Rihanna", roundTrip.ArtistAsTagged);
    }

    private string Music => Path.Combine(_root, "music");

    private string WriteMp3(string name, string title, string[] performers)
    {
        var path = Path.Combine(Music, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using var f = TagLib.File.Create(path);
        f.Tag.Title = title;
        f.Tag.Performers = performers;
        f.Tag.Album = "Album";
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

    [Fact]
    public async Task Scan_KeepsTheTagsCredit_BesideTheMergedOne_AndOldLibrariesReadItOnce()
    {
        var work = WriteMp3("work.mp3", "Work (feat. Drake)", new[] { "Rihanna" });
        WriteMp3("squeeze.mp3", "Squeeze Me", new[] { "Kraak & Smaak" });
        var (library, persistence) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 2) Assert.Fail(ScanMissReport.Describe(library, Music));

        var merged = library.AllTracks.Single(t => t.FilePath == work);
        Assert.Equal("Rihanna, Drake", merged.Artist);                 // merged from the title, as before
        Assert.Equal("Rihanna", merged.ArtistAsTagged);
        var duo = library.AllTracks.Single(t => t.FilePath != work);
        Assert.Equal("Kraak & Smaak", duo.Artist);
        Assert.Null(duo.ArtistAsTagged);                              // nothing stored when nothing can differ

        ArtistCredit.ShowAsTagged = true;
        Assert.Equal("Rihanna", merged.ArtistDisplay);
        Assert.Equal("Kraak & Smaak", duo.ArtistDisplay);

        // As a build before #187 stored it: no tag credit. The pass re-reads only that song.
        merged.ArtistAsTagged = null;
        Assert.True(LibraryService.NeedsArtistTagText(merged));
        Assert.False(LibraryService.NeedsArtistTagText(duo));
        Assert.Equal(1, await library.ResolveArtistTagTextAsync());
        Assert.Equal("Rihanna", merged.ArtistAsTagged);
        Assert.Equal("Rihanna, Drake", merged.Artist);                 // the index keeps its credit
        Assert.Equal(0, await library.ResolveArtistTagTextAsync());    // resolved for good

        // And it was persisted.
        var reloaded = await persistence.LoadLibraryAsync();
        Assert.Equal("Rihanna", reloaded!.Single(t => t.FilePath == work).ArtistAsTagged);
    }

    [Fact]
    public async Task MergingAfterTheFact_KeepsTheTagsCredit()
    {
        WriteMp3("work.mp3", "Work (feat. Drake)", new[] { "Rihanna" });
        var (library, _) = await NewLibraryAsync();
        await library.ScanAsync(new[] { Music });
        if (library.AllTracks.Count != 1) Assert.Fail(ScanMissReport.Describe(library, Music));
        var track = library.AllTracks.Single();

        // As indexed while merging was off in a build before #187: tag credit, nothing stored.
        track.Artist = "Rihanna";
        track.ArtistAsTagged = null;
        Assert.Equal(1, await library.ApplyMergeFeaturedFromTitlesAsync(true));
        Assert.Equal("Rihanna, Drake", track.Artist);
        Assert.Equal("Rihanna", track.ArtistAsTagged);
    }

    [Fact]
    public void WritingTags_MakesTheIndexedCreditTheTagsCredit()
    {
        var path = WriteMp3("work.mp3", "Work (feat. Drake)", new[] { "Rihanna" });
        var track = new MetadataService().ReadTrackMetadata(path)!;
        Assert.Equal("Rihanna", track.ArtistAsTagged);

        // Any save writes the indexed credit (merged names included) into the file.
        Assert.True(new MetadataService().WriteTrackMetadata(track));
        Assert.Equal("Rihanna, Drake", track.ArtistAsTagged);
        Assert.Equal("Rihanna, Drake", new MetadataService().ReadTrackMetadata(path)!.ArtistAsTagged);
    }
}
