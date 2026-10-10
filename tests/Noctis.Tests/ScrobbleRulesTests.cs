using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class ScrobbleRulesTests
{
    private static readonly DateTime T0 = new(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void PausedTime_DoesNotCountAsPlayed()
    {
        var clock = new ScrobblePlayClock();
        clock.Start(T0);
        clock.Pause(T0.AddSeconds(10));
        clock.Resume(T0.AddMinutes(5));
        Assert.Equal(TimeSpan.FromSeconds(10), clock.Played(T0.AddMinutes(5)));
        // 10 s of a 3-minute song, a long pause, then a skip: not a scrobble.
        Assert.False(ScrobblePlayClock.ShouldScrobble(TimeSpan.FromMinutes(3), clock.Played(T0.AddMinutes(5))));
    }

    [Fact]
    public void StoppedWhilePaused_CountsOnlyUpToThePause()
    {
        var clock = new ScrobblePlayClock();
        clock.Start(T0);
        clock.Pause(T0.AddSeconds(100));
        Assert.Equal(TimeSpan.FromSeconds(100), clock.Played(T0.AddMinutes(30)));
    }

    [Fact]
    public void RepeatedPauseOrResume_IsHarmless()
    {
        var clock = new ScrobblePlayClock();
        clock.Start(T0);
        clock.Resume(T0.AddSeconds(5));
        clock.Pause(T0.AddSeconds(20));
        clock.Pause(T0.AddSeconds(40));
        clock.Resume(T0.AddSeconds(60));
        clock.Resume(T0.AddSeconds(70));
        Assert.Equal(TimeSpan.FromSeconds(40), clock.Played(T0.AddSeconds(80)));
    }

    [Theory]
    [InlineData(180, 90, true)]    // exactly half
    [InlineData(180, 89, false)]
    [InlineData(600, 240, true)]   // 4 minutes of a long song
    [InlineData(600, 239, false)]
    [InlineData(30, 30, false)]    // 30 s or shorter never scrobbles (last.fm/api/scrobbling)
    [InlineData(31, 20, true)]
    [InlineData(0, 500, false)]    // unknown length
    public void ShouldScrobble_FollowsLastFmRules(int durationSeconds, int playedSeconds, bool expected)
        => Assert.Equal(expected, ScrobblePlayClock.ShouldScrobble(
            TimeSpan.FromSeconds(durationSeconds), TimeSpan.FromSeconds(playedSeconds)));

    private static Track MakeTrack(string artist = "Friend", string albumArtist = "Friend", string album = "Unreleased")
        => new()
        {
            Title = "Demo",
            Artist = artist,
            AlbumArtist = albumArtist,
            Album = album,
            AlbumId = Track.ComputeAlbumId(albumArtist, album),
        };

    [Fact]
    public void NoKeys_NothingExcluded()
        => Assert.False(ScrobbleExclusionKeys.IsExcluded(new HashSet<string>(), MakeTrack()));

    [Fact]
    public void AlbumKey_ExcludesEverySongOnThatAlbumOnly()
    {
        var track = MakeTrack();
        var keys = new HashSet<string> { ScrobbleExclusionKeys.ForAlbum(track.AlbumId) };
        Assert.True(ScrobbleExclusionKeys.IsExcluded(keys, track));
        Assert.False(ScrobbleExclusionKeys.IsExcluded(keys, MakeTrack(album: "Released")));
    }

    [Fact]
    public void AlbumKey_SurvivesARescan_SameTagsSameId()
    {
        var before = MakeTrack();
        var keys = new HashSet<string> { ScrobbleExclusionKeys.ForAlbum(before.AlbumId) };
        // A rescan builds a new Track (new Id) but AlbumId hashes the same tags.
        var after = MakeTrack();
        Assert.NotEqual(before.Id, after.Id);
        Assert.True(ScrobbleExclusionKeys.IsExcluded(keys, after));
    }

    [Fact]
    public void TrackKey_ExcludesOnlyThatSong()
    {
        var track = MakeTrack();
        var keys = new HashSet<string> { ScrobbleExclusionKeys.ForTrack(track.Id) };
        Assert.True(ScrobbleExclusionKeys.IsExcluded(keys, track));
        Assert.False(ScrobbleExclusionKeys.IsExcluded(keys, MakeTrack()));
    }

    [Fact]
    public void ArtistKey_MatchesAnyCreditedName_IgnoringCase()
    {
        var keys = new HashSet<string> { ScrobbleExclusionKeys.ForArtist("Friend") };
        // Featured on someone else's song, and as album artist on a compilation.
        Assert.True(ScrobbleExclusionKeys.IsExcluded(keys, MakeTrack(artist: "Other feat. FRIEND", albumArtist: "Other")));
        Assert.True(ScrobbleExclusionKeys.IsExcluded(keys, MakeTrack(artist: "Other", albumArtist: "friend")));
        Assert.False(ScrobbleExclusionKeys.IsExcluded(keys, MakeTrack(artist: "Friendly", albumArtist: "Other")));
    }
}
