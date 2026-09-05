using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Importing a Deezer playlist/album by pasted link: URL recognition, page parsing, and the
/// page walk that gathers every track (stopping on the last page or a foreign "next").
/// </summary>
public class DeezerPlaylistLinkTests
{
    [Theory]
    [InlineData("https://www.deezer.com/en/playlist/3155776842", DeezerLinkKind.Playlist, 3155776842L)]
    [InlineData("https://deezer.com/playlist/12?utm_source=x", DeezerLinkKind.Playlist, 12L)]
    [InlineData("  https://www.deezer.com/fr/album/302127  ", DeezerLinkKind.Album, 302127L)]
    [InlineData("http://www.deezer.com/pt-br/album/7", DeezerLinkKind.Album, 7L)]
    public void TryParse_RecognisesShareLinks(string url, DeezerLinkKind kind, long id)
    {
        Assert.True(DeezerPlaylistLink.TryParse(url, out var k, out var i));
        Assert.Equal(kind, k);
        Assert.Equal(id, i);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")]
    [InlineData("https://www.deezer.com/en/artist/27")]
    [InlineData("https://deezer.page.link/abc")]
    [InlineData("C:\\music\\list.m3u8")]
    public void TryParse_RejectsEverythingElse(string? text)
        => Assert.False(DeezerPlaylistLink.TryParse(text, out _, out _));

    [Fact]
    public void ParseTracksPage_ReadsEntries_AndNext()
    {
        const string json = """
        { "data": [
            { "title": "Boston", "artist": { "name": "STELLA LEFTY" }, "album": { "title": "Boston" } },
            { "title": "Hello", "artist": { "name": "Adele" }, "album": { "title": "25" } },
            { "title": "" }
          ],
          "total": 100,
          "next": "https://api.deezer.com/playlist/1/tracks?limit=100&index=100" }
        """;
        var (entries, next) = DeezerPlaylistLink.ParseTracksPage(json);
        Assert.Equal(2, entries.Count);
        Assert.Equal(("Boston", "STELLA LEFTY", "Boston"), (entries[0].Title, entries[0].Artist, entries[0].Album));
        Assert.Equal("https://api.deezer.com/playlist/1/tracks?limit=100&index=100", next);
    }

    [Fact]
    public void ParseTracksPage_AlbumPages_UseTheAlbumTitle_AndStopOnForeignNext()
    {
        const string json = """
        { "data": [ { "title": "Lucid Dreams", "artist": { "name": "Juice WRLD" } } ],
          "next": "https://evil.example/steal" }
        """;
        var (entries, next) = DeezerPlaylistLink.ParseTracksPage(json, albumTitle: "Goodbye & Good Riddance");
        Assert.Single(entries);
        Assert.Equal("Goodbye & Good Riddance", entries[0].Album);
        Assert.Null(next);
    }

    [Fact]
    public void ParseTitle_ErrorRecord_IsNull()
    {
        Assert.Null(DeezerPlaylistLink.ParseTitle("""{ "error": { "type": "DataException", "message": "no data" } }"""));
        Assert.Equal("Top Worldwide", DeezerPlaylistLink.ParseTitle("""{ "id": 1, "title": "Top Worldwide" }"""));
        Assert.Null(DeezerPlaylistLink.ParseTitle("nope"));
    }

    [Fact]
    public async Task FetchAllAsync_WalksEveryPage_UsingOnlyDeezerUrls()
    {
        var urls = new List<string>();
        Task<string?> Fetch(string url, CancellationToken _)
        {
            urls.Add(url);
            if (url == "https://api.deezer.com/playlist/42")
                return Task.FromResult<string?>("""{ "id": 42, "title": "Top Worldwide", "nb_tracks": 3 }""");
            if (url.Contains("index=0"))
                return Task.FromResult<string?>("""
                { "data": [
                    { "title": "Boston", "artist": { "name": "STELLA LEFTY" }, "album": { "title": "Boston" } },
                    { "title": "Hello", "artist": { "name": "Adele" }, "album": { "title": "25" } } ],
                  "next": "https://api.deezer.com/playlist/42/tracks?limit=100&index=100" }
                """);
            return Task.FromResult<string?>("""{ "data": [ { "title": "Nowhere Song", "artist": { "name": "Nobody" }, "album": { "title": "None" } } ] }""");
        }

        var result = await DeezerPlaylistLink.FetchAllAsync(DeezerLinkKind.Playlist, 42, Fetch, CancellationToken.None);

        Assert.Equal("Top Worldwide", result.SuggestedName);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(3, urls.Count); // info + two track pages
        Assert.All(urls, u => Assert.StartsWith("https://api.deezer.com/playlist/42", u));
    }

    [Fact]
    public async Task FetchAllAsync_InfoUnavailable_FallsBackToAGenericName()
    {
        static Task<string?> Fetch(string url, CancellationToken _)
            => Task.FromResult<string?>(url.EndsWith("/album/7")
                ? null
                : """{ "data": [ { "title": "Only Track", "artist": { "name": "A" } } ] }""");

        var result = await DeezerPlaylistLink.FetchAllAsync(DeezerLinkKind.Album, 7, Fetch, CancellationToken.None);
        Assert.Equal("Deezer album", result.SuggestedName);
        Assert.Single(result.Entries);
        Assert.Equal(string.Empty, result.Entries[0].Album); // no title known → no album column
    }

    [Theory]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M?si=abc", "Spotify", "https://exportify.net/")]
    [InlineData("https://music.apple.com/us/playlist/todays-hits/pl.f4d106fed2bd41149aaacabb233eb5eb", "Apple Music", "https://www.tunemymusic.com/transfer")]
    [InlineData("https://listen.tidal.com/playlist/1b418bb8-90a7-4f87-901d-707993838346", "TIDAL", "https://www.tunemymusic.com/transfer")]
    [InlineData("https://music.youtube.com/playlist?list=PL123", "YouTube Music", "https://www.tunemymusic.com/transfer")]
    [InlineData("https://music.amazon.com/playlists/B07XYZ", "Amazon Music", "https://www.tunemymusic.com/transfer")]
    public void StreamingLinkHints_PointToAWorkingExportPath(string url, string service, string helpUrl)
    {
        var hint = StreamingLinkHints.For(url);
        Assert.NotNull(hint);
        Assert.Equal(service, hint!.Service);
        Assert.Equal(helpUrl, hint.HelpUrl);
        Assert.Contains("file", hint.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("hello")]
    [InlineData("https://www.deezer.com/en/playlist/3155776842")] // Deezer imports directly: no hint
    [InlineData("https://example.com/playlist/1")]
    public void StreamingLinkHints_QuietForEverythingElse(string? text)
        => Assert.Null(StreamingLinkHints.For(text));
}
