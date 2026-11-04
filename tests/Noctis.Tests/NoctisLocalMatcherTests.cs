using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Noctis.Mobile.Services.Account;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>Which desktop songs the phone already has as its own files (one song, not two).</summary>
public class NoctisLocalMatcherTests
{
    private static RemoteSong Desk(string title, string artist, int seconds, string album = "Album") => new(
        Guid.NewGuid(), null, title, album, artist, seconds, 0, "flac", 1, 1, 2020, "", 0, 0, 0, false, 0, null);

    private static Track Local(string title, string artist, double seconds, string album = "Album") => new()
    {
        Id = Guid.NewGuid(),
        Title = title,
        Artist = artist,
        Album = album,
        FilePath = "content://x/" + Guid.NewGuid().ToString("N"),
        SourceType = SourceType.Local,
        Duration = TimeSpan.FromSeconds(seconds),
    };

    private static Dictionary<Guid, Guid> Match(RemoteSong[] songs, params Track[] library) =>
        NoctisLocalMatcher.Match(songs, library, new Dictionary<Guid, Guid>());

    [Fact]
    public void SameTitleArtistAndLength_Match()
    {
        var d = Desk("(I Just) Died in Your Arms", "Cutting Crew", 280);
        var l = Local("(I Just) Died in Your Arms", "Cutting Crew", 279.6);
        Assert.Equal(l.Id, Match(new[] { d }, l)[d.Id]);
    }

    [Fact]
    public void Decoration_FeatCreditsRemasterAndCase_DoNotPreventAMatch()
    {
        var d = Desk("Song (feat. Guest) - Remastered 2011", "The Band & Guest", 200);
        var l = Local("SONG", "the band", 201);
        Assert.Equal(l.Id, Match(new[] { d }, l)[d.Id]);
    }

    [Theory]
    [InlineData("Song (Live)", "Song")]
    [InlineData("Song", "Song (Live)")]
    [InlineData("Song (Remix)", "Song")]
    [InlineData("Song - Radio Edit", "Song")]
    [InlineData("Song (Acoustic)", "Song (Live)")]
    public void DifferentVersions_NeverMerge(string desktopTitle, string localTitle)
    {
        var d = Desk(desktopTitle, "Artist", 200);
        var l = Local(localTitle, "Artist", 200);
        Assert.Empty(Match(new[] { d }, l));
    }

    [Fact]
    public void SameVersionMarkers_StillMatch()
    {
        var d = Desk("Song (Live)", "Artist", 200);
        var l = Local("Song - Live", "Artist", 200);
        Assert.Equal(l.Id, Match(new[] { d }, l)[d.Id]);
    }

    [Fact]
    public void DurationsMoreThanAFewSecondsApart_DoNotMatch()
    {
        var d = Desk("Song", "Artist", 200);
        Assert.Empty(Match(new[] { d }, Local("Song", "Artist", 206)));
        Assert.NotEmpty(Match(new[] { d }, Local("Song", "Artist", 202.4)));
    }

    [Fact]
    public void DifferentArtists_DoNotMatch_AndUnknownOnesNeverDo()
    {
        Assert.Empty(Match(new[] { Desk("Song", "A", 200) }, Local("Song", "B", 200)));
        Assert.Empty(Match(new[] { Desk("Song", "Unknown Artist", 200) }, Local("Song", "Unknown Artist", 200)));
        Assert.Empty(Match(new[] { Desk("Unknown Title", "A", 200) }, Local("Unknown Title", "A", 200)));
        Assert.Empty(Match(new[] { Desk("Song", "A", 0) }, Local("Song", "A", 0)));
    }

    [Fact]
    public void TitlesWithoutLettersOrDigits_StillMatch_ByTheirText()
    {
        var d = Desk("?", "Tate McRae", 190);
        var l = Local("?", "Tate McRae", 190);
        var other = Local("!", "Tate McRae", 190);
        Assert.Equal(l.Id, Match(new[] { d }, other, l)[d.Id]);
    }

    [Fact]
    public void OneLocalFile_PairsWithOneDesktopSong_AlbumBreaksTheTie()
    {
        var onAlbum = Desk("Song", "Artist", 200, album: "Hits");
        var onSingle = Desk("Song", "Artist", 200, album: "Single");
        var l = Local("Song", "Artist", 200, album: "Single");
        var map = Match(new[] { onAlbum, onSingle }, l);
        Assert.Single(map);
        Assert.Equal(l.Id, map[onSingle.Id]);
    }

    [Fact]
    public void ClosestLength_WinsAmongSeveralLocalCopies()
    {
        var d = Desk("Song", "Artist", 200);
        var far = Local("Song", "Artist", 201.8);
        var near = Local("Song", "Artist", 200.1);
        Assert.Equal(near.Id, Match(new[] { d }, far, near)[d.Id]);
    }

    [Fact]
    public void DesktopRowsAndOtherSources_AreNeverALocalFile()
    {
        var d = Desk("Song", "Artist", 200);
        var remote = Local("Song", "Artist", 200);
        remote.SourceType = SourceType.NoctisServer;
        Assert.Empty(Match(new[] { d }, remote));
    }

    [Fact]
    public void AnEarlierPair_HoldsEvenAfterATagEdit()
    {
        var d = Desk("Song", "Artist", 200);
        var l = Local("Song (my edit)", "Artist", 200);
        var map = NoctisLocalMatcher.Match(new[] { d }, new[] { l }, new Dictionary<Guid, Guid> { [d.Id] = l.Id });
        Assert.Equal(l.Id, map[d.Id]);
        // ...but not once the file is gone.
        Assert.Empty(NoctisLocalMatcher.Match(new[] { d }, Array.Empty<Track>(), new Dictionary<Guid, Guid> { [d.Id] = l.Id }));
    }

    [Fact]
    public void TenThousandSongs_AreIndexed_NotComparedPairwise()
    {
        var desk = new List<RemoteSong>();
        var local = new List<Track>();
        for (var i = 0; i < 12_000; i++)
        {
            desk.Add(Desk("Song number " + i, "Artist " + i % 300, 180 + i % 90));
            if (i % 2 == 0) local.Add(Local("Song number " + i, "Artist " + i % 300, 180 + i % 90));
        }
        var sw = Stopwatch.StartNew();
        var map = NoctisLocalMatcher.Match(desk, local, new Dictionary<Guid, Guid>());
        sw.Stop();
        Assert.Equal(6_000, map.Count);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(10), $"took {sw.Elapsed}");
    }
}
