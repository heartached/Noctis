using Xunit;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Tests;

/// <summary>The phone's ReplayGain arithmetic (<see cref="ReplayGainLevel"/>): which tag a mode
/// reads, the pre-amp, the desktop's ±dB window, and Prevent Clipping's cap from the peak.</summary>
public class ReplayGainLevelTests
{
    private static double Db(double linear) => 20 * Math.Log10(linear);

    [Theory]
    [InlineData("Off", ReplayGainMode.Off)]
    [InlineData("Track", ReplayGainMode.Track)]
    [InlineData("album", ReplayGainMode.Album)]
    // The desktop's "Auto" (album ?? track) reads as Album here: the same value in both apps.
    [InlineData("Auto", ReplayGainMode.Album)]
    [InlineData("", ReplayGainMode.Off)]
    [InlineData(null, ReplayGainMode.Off)]
    [InlineData("loud", ReplayGainMode.Off)]
    public void ParseMode_ReadsTheSettingsValue(string? value, ReplayGainMode expected) =>
        Assert.Equal(expected, ReplayGainLevel.ParseMode(value));

    [Fact]
    public void ModeName_IsWhatSettingsJsonHolds()
    {
        Assert.Equal("Off", ReplayGainLevel.ModeName(ReplayGainMode.Off));
        Assert.Equal("Track", ReplayGainLevel.ModeName(ReplayGainMode.Track));
        Assert.Equal("Album", ReplayGainLevel.ModeName(ReplayGainMode.Album));
    }

    [Fact]
    public void Off_IsUnity_WhateverTheTags()
    {
        Assert.Equal(1.0, ReplayGainLevel.Linear(ReplayGainMode.Off, -8, -6, 0.5, 6, preventClipping: false));
    }

    [Fact]
    public void Track_ReadsTrackGain_Album_ReadsAlbumGain()
    {
        Assert.Equal(-8, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, -8, -6, null, 0, false)), 6);
        Assert.Equal(-6, Db(ReplayGainLevel.Linear(ReplayGainMode.Album, -8, -6, null, 0, false)), 6);
    }

    [Fact]
    public void EachMode_FallsBackToTheOtherTag()
    {
        // Album with no album gain: the track's (as the desktop). Track with no track gain: the album's.
        Assert.Equal(-8, Db(ReplayGainLevel.Linear(ReplayGainMode.Album, -8, null, null, 0, false)), 6);
        Assert.Equal(-6, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, null, -6, null, 0, false)), 6);
    }

    [Fact]
    public void Untagged_PlaysAsItIs_LikeTheDesktop()
    {
        // No guessed gain and no pre-amp on a song with no ReplayGain at all (VlcAudioPlayer bypasses too).
        Assert.Equal(1.0, ReplayGainLevel.Linear(ReplayGainMode.Track, null, null, null, 6, false));
        Assert.Equal(1.0, ReplayGainLevel.Linear(ReplayGainMode.Album, double.NaN, null, null, -6, false));
    }

    [Fact]
    public void Preamp_AddsToTheTag_AndIsClampedToTheSlidersRange()
    {
        Assert.Equal(-5, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, -8, null, null, 3, false)), 6);
        Assert.Equal(4, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, -8, null, null, 40, false)), 6);   // +12 at most
    }

    [Fact]
    public void Total_StaysInTheDesktopsWindow()
    {
        // A corrupt tag can't blow the speakers or mute the song: -30..+12 dB, as VlcAudioPlayer.
        Assert.Equal(12, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, 40, null, null, 0, false)), 6);
        Assert.Equal(-30, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, -80, null, null, 0, false)), 6);
    }

    [Fact]
    public void PreventClipping_CapsABoostAtThePeaksHeadroom()
    {
        // +6 dB on a song peaking at 0.8 would clip; the cap is 1/0.8 (+1.94 dB).
        var capped = ReplayGainLevel.Linear(ReplayGainMode.Track, 6, null, 0.8, 0, preventClipping: true);
        Assert.Equal(1 / 0.8, capped, 6);
        // Off: the full boost (the gain stage's limiter then catches the peaks).
        Assert.Equal(6, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, 6, null, 0.8, 0, preventClipping: false)), 6);
        // A cut is never raised by it.
        Assert.Equal(-6, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, -6, null, 0.8, 0, preventClipping: true)), 6);
    }

    [Fact]
    public void PreventClipping_WithNoUsablePeak_LeavesTheGain()
    {
        Assert.Equal(6, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, 6, null, null, 0, true)), 6);
        Assert.Equal(6, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, 6, null, 0, 0, true)), 6);
        Assert.Equal(6, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, 6, null, double.NaN, 0, true)), 6);
    }

    [Fact]
    public void PreventClipping_InAlbumMode_UsesThisSongsPeak()
    {
        // The song's own samples are what could clip: its track peak bounds the album gain too.
        var g = ReplayGainLevel.Linear(ReplayGainMode.Album, -2, 4, 0.9, 0, true);
        Assert.Equal(1 / 0.9, g, 6);
    }

    [Fact]
    public void Track_Overload_ReadsTheSongsFields()
    {
        var t = new Track { ReplayGainTrackDb = -7, ReplayGainAlbumDb = -5, ReplayGainTrackPeak = 0.99 };
        Assert.Equal(-7, Db(ReplayGainLevel.Linear(ReplayGainMode.Track, t, 0, true)), 6);
        Assert.Equal(-5, Db(ReplayGainLevel.Linear(ReplayGainMode.Album, t, 0, true)), 6);
        Assert.Equal(1.0, ReplayGainLevel.Linear(ReplayGainMode.Track, (Track?)null, 0, true));
    }
}
