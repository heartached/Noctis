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
