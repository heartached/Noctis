using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Importing a TIDAL playlist/album by pasted link: URL recognition, JSON:API page parsing
/// (tracks resolved through <c>included</c>), the page walk with its include fallback, and
/// the PKCE helpers the browser sign-in is built from.
/// </summary>
public class TidalPlaylistLinkTests
{
    private const string PlaylistId = "1b418bb8-90a7-4f87-901d-707993838346";

    [Theory]
    [InlineData("https://tidal.com/browse/playlist/1b418bb8-90a7-4f87-901d-707993838346?u", TidalLinkKind.Playlist, PlaylistId)]
    [InlineData("https://listen.tidal.com/playlist/1B418BB8-90A7-4F87-901D-707993838346", TidalLinkKind.Playlist, PlaylistId)]
    [InlineData("  https://tidal.com/playlist/1b418bb8-90a7-4f87-901d-707993838346  ", TidalLinkKind.Playlist, PlaylistId)]
    [InlineData("https://tidal.com/browse/album/302127", TidalLinkKind.Album, "302127")]
    [InlineData("http://www.tidal.com/album/7?utm=x", TidalLinkKind.Album, "7")]
    public void TryParse_RecognisesShareLinks(string url, TidalLinkKind kind, string id)
    {
        Assert.True(TidalPlaylistLink.TryParse(url, out var k, out var i));
        Assert.Equal(kind, k);
        Assert.Equal(id, i);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://tidal.com/browse/artist/3566")]
    [InlineData("https://tidal.com/browse/track/1234")]
    [InlineData("https://tidal.com/browse/playlist/1234")] // playlists are UUIDs
    [InlineData("https://tidal.com/browse/album/1b418bb8-90a7-4f87-901d-707993838346")] // albums are numeric
    [InlineData("https://www.deezer.com/en/playlist/3155776842")]
    [InlineData("https://open.spotify.com/playlist/37i9dQZF1DXcBWIGoYBM5M")]
    public void TryParse_RejectsEverythingElse(string? text)
        => Assert.False(TidalPlaylistLink.TryParse(text, out _, out _));

    [Fact]
    public void BuildUrls_TargetTheV2Api()
    {
        Assert.Equal("https://openapi.tidal.com/v2/playlists/" + PlaylistId + "?countryCode=DE",
            TidalPlaylistLink.BuildInfoUrl(TidalLinkKind.Playlist, PlaylistId, "DE"));
        var items = TidalPlaylistLink.BuildItemsUrl(TidalLinkKind.Album, "7", "US");
        Assert.StartsWith("https://openapi.tidal.com/v2/albums/7/relationships/items?countryCode=US&include=", items);
        Assert.Contains("items.artists", Uri.UnescapeDataString(items));
        Assert.DoesNotContain("items.artists", TidalPlaylistLink.BuildItemsUrl(TidalLinkKind.Album, "7", "US", nested: false));
    }

    [Fact]
    public void ParseName_ReadsPlaylistName_OrAlbumTitle()
    {
        Assert.Equal("Late Night", TidalPlaylistLink.ParseName("""{ "data": { "id": "x", "type": "playlists", "attributes": { "name": "Late Night" } } }"""));
        Assert.Equal("25", TidalPlaylistLink.ParseName("""{ "data": { "id": "7", "type": "albums", "attributes": { "title": "25" } } }"""));
        Assert.Null(TidalPlaylistLink.ParseName("""{ "errors": [ { "status": "404" } ] }"""));
        Assert.Null(TidalPlaylistLink.ParseName("nope"));
    }

    private const string Page1 = """
    { "data": [ { "id": "t1", "type": "tracks" }, { "id": "v1", "type": "videos" }, { "id": "t2", "type": "tracks" }, { "id": "t-missing", "type": "tracks" } ],
      "included": [
        { "id": "t1", "type": "tracks", "attributes": { "title": "Boston" },
          "relationships": { "artists": { "data": [ { "id": "a1", "type": "artists" } ] }, "albums": { "data": [ { "id": "al1", "type": "albums" } ] } } },
        { "id": "t2", "type": "tracks", "attributes": { "title": "Hello" },
          "relationships": { "artists": { "data": [ { "id": "a2", "type": "artists" }, { "id": "a3", "type": "artists" } ] }, "albums": { "data": [ { "id": "al2", "type": "albums" } ] } } },
        { "id": "a1", "type": "artists", "attributes": { "name": "STELLA LEFTY" } },
        { "id": "a2", "type": "artists", "attributes": { "name": "Adele" } },
        { "id": "a3", "type": "artists", "attributes": { "name": "Someone" } },
        { "id": "al1", "type": "albums", "attributes": { "title": "Boston" } },
        { "id": "al2", "type": "albums", "attributes": { "title": "25" } } ],
      "links": { "self": "/playlists/x/relationships/items", "next": "/playlists/x/relationships/items?page%5Bcursor%5D=abc&countryCode=US" } }
    """;

    [Fact]
    public void ParseItemsPage_ResolvesTracksThroughIncluded_InOrder_AndMakesNextAbsolute()
    {
        var (entries, next) = TidalPlaylistLink.ParseItemsPage(Page1);
        Assert.Equal(2, entries.Count); // video and the un-included track are skipped
        Assert.Equal(("Boston", "STELLA LEFTY", "Boston"), (entries[0].Title, entries[0].Artist, entries[0].Album));
        Assert.Equal(("Hello", "Adele, Someone", "25"), (entries[1].Title, entries[1].Artist, entries[1].Album));
        Assert.Equal("https://openapi.tidal.com/v2/playlists/x/relationships/items?page%5Bcursor%5D=abc&countryCode=US", next);
    }

    [Fact]
    public void ParseItemsPage_WithoutNestedIncludes_StillYieldsTitles()
    {
        const string json = """
        { "data": [ { "id": "t1", "type": "tracks" } ],
          "included": [ { "id": "t1", "type": "tracks", "attributes": { "title": "Lucid Dreams" },
                          "relationships": { "artists": { "data": [ { "id": "a9", "type": "artists" } ] } } } ] }
        """;
        var (entries, next) = TidalPlaylistLink.ParseItemsPage(json, albumTitle: "Goodbye & Good Riddance");
        Assert.Single(entries);
        Assert.Equal(("Lucid Dreams", "", "Goodbye & Good Riddance"), (entries[0].Title, entries[0].Artist, entries[0].Album));
        Assert.Null(next);
    }

    [Fact]
    public void ParseItemsPage_StopsOnForeignNext_AndSurvivesGarbage()
    {
        const string json = """{ "data": [], "links": { "next": "https://evil.example/steal?token" } }""";
        Assert.Null(TidalPlaylistLink.ParseItemsPage(json).Next);
        var (entries, next) = TidalPlaylistLink.ParseItemsPage("{ not json");
        Assert.Empty(entries);
        Assert.Null(next);
    }

    [Fact]
    public async Task FetchAllAsync_WalksEveryPage_UsingOnlyTidalUrls()
    {
        var urls = new List<string>();
        Task<string?> Fetch(string url, CancellationToken _)
        {
            urls.Add(url);
            if (url == TidalPlaylistLink.BuildInfoUrl(TidalLinkKind.Playlist, "x", "US"))
                return Task.FromResult<string?>("""{ "data": { "id": "x", "type": "playlists", "attributes": { "name": "Top" } } }""");
            if (url.Contains("cursor", StringComparison.Ordinal))
                return Task.FromResult<string?>("""
                { "data": [ { "id": "t3", "type": "tracks" } ],
                  "included": [ { "id": "t3", "type": "tracks", "attributes": { "title": "Nowhere Song" } } ] }
                """);
            return Task.FromResult<string?>(Page1);
        }

        var result = await TidalPlaylistLink.FetchAllAsync(TidalLinkKind.Playlist, "x", "US", Fetch, CancellationToken.None);

        Assert.Equal("Top", result.SuggestedName);
        Assert.Equal(3, result.Entries.Count);
        Assert.Equal(3, urls.Count); // info + two item pages
        Assert.All(urls, u => Assert.StartsWith("https://openapi.tidal.com/v2/playlists/x", u));
    }

    [Fact]
    public async Task FetchAllAsync_NestedIncludeRefused_RetriesWithItemsOnly_AndFallsBackToAGenericName()
    {
        var urls = new List<string>();
        Task<string?> Fetch(string url, CancellationToken _)
        {
            urls.Add(url);
            if (!url.Contains("relationships", StringComparison.Ordinal)) return Task.FromResult<string?>(null); // info 404
            if (url.Contains("items.artists", StringComparison.Ordinal)) return Task.FromResult<string?>(null); // 400 on dotted include
            return Task.FromResult<string?>("""
            { "data": [ { "id": "t1", "type": "tracks" } ],
              "included": [ { "id": "t1", "type": "tracks", "attributes": { "title": "Only Track" } } ] }
            """);
        }

        var result = await TidalPlaylistLink.FetchAllAsync(TidalLinkKind.Album, "7", "US", Fetch, CancellationToken.None);
        Assert.Equal("TIDAL album", result.SuggestedName);
        Assert.Single(result.Entries);
        Assert.Equal(string.Empty, result.Entries[0].Album);
        Assert.Equal(3, urls.Count); // info, nested attempt, plain retry
    }

    // ── PKCE helpers ─────────────────────────────────────────

    [Fact]
    public void ComputeChallenge_MatchesRfc7636AppendixB()
    {
        // RFC 7636 §B: verifier → S256 challenge.
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM",
            TidalOAuth.ComputeChallenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));
        var verifier = TidalOAuth.CreateVerifier();
        Assert.InRange(verifier.Length, 43, 128);
        Assert.Matches("^[A-Za-z0-9_-]+$", verifier);
    }

    [Fact]
    public void BuildAuthorizeUrl_CarriesEveryPkceParameter()
    {
        var url = TidalOAuth.BuildAuthorizeUrl("cid", "chal", "st");
        Assert.StartsWith("https://login.tidal.com/authorize?", url);
        Assert.Contains("response_type=code", url);
        Assert.Contains("client_id=cid", url);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString("http://127.0.0.1:47474/callback"), url);
        Assert.Contains("scope=playlists.read", url);
        Assert.Contains("code_challenge_method=S256", url);
        Assert.Contains("code_challenge=chal", url);
        Assert.Contains("state=st", url);
    }

    [Fact]
    public void TryParseCallback_ReadsCodeAndState_AndIgnoresOtherPaths()
    {
        Assert.True(TidalOAuth.TryParseCallback("GET /callback?code=abc%20d&state=s1 HTTP/1.1", out var code, out var state, out var error));
        Assert.Equal(("abc d", "s1", ""), (code, state, error));

        Assert.True(TidalOAuth.TryParseCallback("GET /callback?error=access_denied&state=s1 HTTP/1.1", out _, out _, out error));
        Assert.Equal("access_denied", error);

        Assert.False(TidalOAuth.TryParseCallback("GET /favicon.ico HTTP/1.1", out _, out _, out _));
        Assert.False(TidalOAuth.TryParseCallback("POST /callback HTTP/1.1", out _, out _, out _));
        Assert.False(TidalOAuth.TryParseCallback(null, out _, out _, out _));
    }

    [Fact]
    public void TokenForms_AndResponses()
    {
        var form = TidalOAuth.BuildCodeExchangeForm("cid", "code", "ver");
        Assert.Equal("authorization_code", form["grant_type"]);
        Assert.Equal("ver", form["code_verifier"]);
        Assert.Equal(TidalOAuth.RedirectUri, form["redirect_uri"]);
        Assert.DoesNotContain("client_secret", form.Keys);
        Assert.Equal("refresh_token", TidalOAuth.BuildRefreshForm("cid", "r")["grant_type"]);

        var tokens = TidalOAuth.ParseTokenResponse("""{ "access_token": "a", "refresh_token": "r", "expires_in": 86400, "token_type": "Bearer" }""");
        Assert.NotNull(tokens);
        Assert.Equal(("a", "r", TimeSpan.FromDays(1)), (tokens!.AccessToken, tokens.RefreshToken, tokens.ExpiresIn));
        Assert.Null(TidalOAuth.ParseTokenResponse("""{ "error": "invalid_grant" }"""));
        Assert.True(TidalOAuth.IsInvalidGrant("""{ "error": "invalid_grant" }"""));
        Assert.Null(TidalOAuth.ParseTokenResponse("garbage"));
    }
}
