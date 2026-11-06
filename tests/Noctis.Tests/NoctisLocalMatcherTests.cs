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
    private static RemoteSong Desk(string title, string artist, int seconds, string album = "Album", long size = 0) => new(
        Guid.NewGuid(), null, title, album, artist, seconds, size, "mp3", 1, 1, 2020, "", 0, 0, 0, false, 0, null);

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

    // Beta 2 feedback: "2 Sugar (feat. Ayra Starr)" and "20 Min" still showed twice. The phone reads
    // a song through its content stream, where MetadataService skips the desktop's MP3 frame count
    // (ApplyCountedMp3Length needs a local path) and ffprobe: a headerless VBR MP3 gets TagLib's
    // first-frame guess on the phone (often a minute off) and its counted length on the computer,
    // and a file TagLib cannot time reads as 0 there. The same file has the same size on both.
    [Fact]
    public void TheSameFile_MatchesEvenWhenThePhoneMisreadsItsLength()
    {
        var d = Desk("2 Sugar (feat. Ayra Starr)", "Wizkid, Ayra Starr", 201, size: 6_543_210);
        var guessed = Local("2 Sugar (feat. Ayra Starr)", "Wizkid, Ayra Starr", 143.2);
        guessed.FileSize = 6_543_210;
        Assert.Equal(guessed.Id, Match(new[] { d }, guessed)[d.Id]);

        var untimed = Local("20 Min", "Lil Uzi Vert", 0);
        untimed.FileSize = 4_000_001;
        var d2 = Desk("20 Min", "Lil Uzi Vert", 220, size: 4_000_001);
        Assert.Equal(untimed.Id, Match(new[] { d2 }, untimed)[d2.Id]);
    }

    [Fact]
    public void TheSameSize_NeverJoinsADifferentVersionOrArtist_AndADifferentSizeStillNeedsTheLength()
    {
        var live = Local("Song (Live)", "Artist", 150);
        live.FileSize = 5_000_000;
        Assert.Empty(Match(new[] { Desk("Song", "Artist", 200, size: 5_000_000) }, live));
        var other = Local("Song", "Someone Else", 150);
        other.FileSize = 5_000_000;
        Assert.Empty(Match(new[] { Desk("Song", "Artist", 200, size: 5_000_000) }, other));
        var copy = Local("Song", "Artist", 150);
        copy.FileSize = 5_000_001;
        Assert.Empty(Match(new[] { Desk("Song", "Artist", 200, size: 5_000_000) }, copy));
    }

    // The desktop sends (int)Duration.TotalSeconds (NoctisServer.SongJson): 203.9 s arrives as 203,
    // so a copy 2.4 s longer than the computer's file read as 3.3 s apart.
    [Fact]
    public void TheDesktopsCutSeconds_CountAsTheWholeSecond()
    {
        var d = Desk("Song", "Artist", 203);
        Assert.NotEmpty(Match(new[] { d }, Local("Song", "Artist", 206.3)));
        Assert.NotEmpty(Match(new[] { d }, Local("Song", "Artist", 200.6)));
        Assert.Empty(Match(new[] { d }, Local("Song", "Artist", 207)));
    }

    [Fact]
    public void FeaturedArtistsWrittenDifferently_StillMatch()
    {
        var d = Desk("2 Sugar (feat. Ayra Starr)", "Wizkid, Ayra Starr", 201);
        Assert.NotEmpty(Match(new[] { d }, Local("2 Sugar", "WizKid", 201.4)));
        Assert.NotEmpty(Match(new[] { d }, Local("2 Sugar feat. Ayra Starr", "Wizkid feat. Ayra Starr", 201.4)));
        Assert.NotEmpty(Match(new[] { d }, Local("2 Sugar (Ft Ayra Starr)", "Wizkid; Ayra Starr", 201.4)));
        Assert.NotEmpty(Match(new[] { d }, Local("2 SUGAR", "Ayra Starr & Wizkid", 201.4)));
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
