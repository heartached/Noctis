using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class PlaylistImportTests
{
    // ── CSV parsing (Exportify) ──

    [Fact]
    public void Csv_ParsesExportifyColumns_AndQuotedCommas()
    {
        const string csv =
            "\"Track Name\",\"Artist Name(s)\",\"Album Name\"\n" +
            "\"Hello\",\"Adele\",\"25\"\n" +
            "\"Seven Nation Army\",\"The White Stripes, Someone\",\"Elephant\"\n";

        var result = PlaylistImportParser.ParseCsv(csv, "fallback");
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Hello", result.Entries[0].Title);
        Assert.Equal("Adele", result.Entries[0].Artist);
        Assert.Equal("25", result.Entries[0].Album);
        // Multiple artists collapse to the primary artist.
        Assert.Equal("The White Stripes", result.Entries[1].Artist);
    }

    [Fact]
    public void Csv_EmbeddedCommaInQuotedTitle_IsPreserved()
    {
        const string csv =
            "Track Name,Artist Name(s),Album Name\n" +
            "\"Hello, Goodbye\",Beatles,Album\n";
        var entry = PlaylistImportParser.ParseCsv(csv, "f").Entries.Single();
        Assert.Equal("Hello, Goodbye", entry.Title);
    }

    // ── M3U parsing ──

    [Fact]
    public void M3u_ParsesExtinfAndResolvesRelativePaths()
    {
        var baseDir = OperatingSystem.IsWindows() ? @"C:\Music\Playlists" : "/music/playlists";
        const string m3u =
            "#EXTM3U\n" +
            "#EXTINF:215,Adele - Hello\n" +
            "sub/hello.mp3\n" +
            "# a comment\n" +
            "no-extinf.flac\n";

        var result = PlaylistImportParser.ParseM3u(m3u, "My List", baseDir);

        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Hello", result.Entries[0].Title);
        Assert.Equal("Adele", result.Entries[0].Artist);
        Assert.Equal(Path.GetFullPath(Path.Combine(baseDir, "sub", "hello.mp3")), result.Entries[0].FilePath);
        // No EXTINF: title falls back to the filename stem.
        Assert.Equal("no-extinf", result.Entries[1].Title);
    }

    [Fact]
    public void M3u_ForeignAbsolutePath_IsKeptForFilenameMatching()
    {
        var result = PlaylistImportParser.ParseM3u(
            "C:\\Users\\other\\Music\\song.mp3\n", "f", "/music");
        var entry = result.Entries.Single();
        Assert.EndsWith("song.mp3", entry.FilePath.Replace('\\', '/'));
        Assert.Equal("song", entry.Title);
    }

    // ── Path/filename matching ladder (m3u entries) ──

    [Fact]
    public void Match_ExactLibraryPath_Wins()
    {
        var track = new Track { Title = "Hello", Artist = "Adele", FilePath = @"C:\Music\hello.mp3" };
        var entry = new PlaylistImportEntry("Different Title", "Nobody", "", @"C:\Music\hello.mp3");

        var result = FuzzyTrackMatcher.Match(new[] { entry }, new[] { track }).Single();

        Assert.Same(track, result.Match);
        Assert.Equal(1.0, result.Score);
    }

    [Fact]
    public void Match_ForeignAbsolutePath_ResolvesByUniqueFilename()
    {
        var track = new Track { Title = "Hello", Artist = "Adele", FilePath = @"C:\Mine\Library\hello.mp3" };
        // Path from another machine — different root, same file name.
        var entry = new PlaylistImportEntry("", "", "", "/home/other/music/hello.mp3");

        var result = FuzzyTrackMatcher.Match(new[] { entry }, new[] { track }).Single();

        Assert.Same(track, result.Match);
    }

    [Fact]
    public void Match_AmbiguousFilename_FallsBackToExtinfText()
    {
        var a = new Track { Title = "Hello", Artist = "Adele", FilePath = @"C:\A\track01.mp3" };
        var b = new Track { Title = "Yellow", Artist = "Coldplay", FilePath = @"C:\B\track01.mp3" };
        var entry = new PlaylistImportEntry("Yellow", "Coldplay", "", "/foreign/track01.mp3");

        var result = FuzzyTrackMatcher.Match(new[] { entry }, new[] { a, b }).Single();

        Assert.Same(b, result.Match);
    }

    // ── JSON parsing (TuneMyMusic / generic) ──

    [Fact]
    public void Json_ParsesTracksArray_WithPlaylistName()
    {
        const string json = """
        {
          "playlistName": "Road Trip",
          "tracks": [
            { "title": "Hello", "artist": "Adele", "album": "25" },
            { "name": "Yellow", "artistName": "Coldplay", "albumName": "Parachutes" }
          ]
        }
        """;

        var result = PlaylistImportParser.ParseJson(json, "fallback");
        Assert.Equal("Road Trip", result.SuggestedName);
        Assert.Equal(2, result.Entries.Count);
        Assert.Equal("Hello", result.Entries[0].Title);
        Assert.Equal("Coldplay", result.Entries[1].Artist);
    }

    [Fact]
    public void Json_TopLevelArray_IsSupported()
    {
        const string json = """[ { "title": "Song", "artist": "X" } ]""";
        var result = PlaylistImportParser.ParseJson(json, "myfile");
        Assert.Equal("myfile", result.SuggestedName);
        Assert.Single(result.Entries);
    }

    // ── Fuzzy matching ──

    private static Track Lib(string title, string artist, string path)
        => new() { Title = title, Artist = artist, FilePath = path };

    [Fact]
    public void Match_ExactNormalized_IsConfident()
    {
        var lib = new[] { Lib("Hello", "Adele", @"C:\a.mp3") };
        var entries = new[] { new PlaylistImportEntry("hello", "ADELE", "25") };
        var m = FuzzyTrackMatcher.Match(entries, lib).Single();
        Assert.NotNull(m.Match);
        Assert.Equal(1.0, m.Score, 3);
    }

    [Fact]
    public void Match_PunctuationAndCaseDiffer_StillMatches()
    {
        var lib = new[] { Lib("Hey Jude", "The Beatles", @"C:\a.mp3") };
        var entries = new[] { new PlaylistImportEntry("Hey Jude!", "the beatles", "") };
        Assert.NotNull(FuzzyTrackMatcher.Match(entries, lib).Single().Match);
    }

    [Fact]
    public void Match_UnrelatedTrack_IsMissing()
    {
        var lib = new[] { Lib("Hello", "Adele", @"C:\a.mp3") };
        var entries = new[] { new PlaylistImportEntry("Stairway to Heaven", "Led Zeppelin", "") };
        Assert.Null(FuzzyTrackMatcher.Match(entries, lib).Single().Match);
    }

    [Fact]
    public void Match_PicksBestCandidateAmongSimilar()
    {
        var lib = new[]
        {
            Lib("Yellow Submarine", "The Beatles", @"C:\a.mp3"),
            Lib("Yellow", "Coldplay", @"C:\b.mp3")
        };
        var entries = new[] { new PlaylistImportEntry("Yellow", "Coldplay", "Parachutes") };
        var m = FuzzyTrackMatcher.Match(entries, lib).Single();
        Assert.Equal(@"C:\b.mp3", m.Match!.FilePath);
    }
}
