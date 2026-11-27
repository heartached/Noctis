using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Now Playing's quality badge (owner 10-14): the playing file's class by Apple Music's tiers -
/// Lossless from 16-bit/44.1 kHz up to 24-bit/48 kHz, Hi-Res Lossless beyond it (Apple Newsroom,
/// 17 May 2021) - or a compressed file's codec, with the technical line and a plain line on what
/// it means. Built on Core's Track classification, so the desktop's album badge agrees.
/// </summary>
public class TrackQualityTests
{
    private static Track Make(string codec, string path, int bits = 0, int rate = 0, int bitrate = 0) => new()
    {
        Codec = codec, FilePath = path, BitsPerSample = bits, SampleRate = rate, Bitrate = bitrate,
    };

    [Theory]
    [InlineData("FLAC", "a.flac", 24, 96000, "Hi-Res Lossless", "24-bit/96 kHz FLAC")]
    [InlineData("FLAC", "a.flac", 24, 88200, "Hi-Res Lossless", "24-bit/88.2 kHz FLAC")]
    [InlineData("MPEG-4 Audio (alac)", "a.m4a", 24, 192000, "Hi-Res Lossless", "24-bit/192 kHz ALAC")]
    // Above 48 kHz is beyond Apple's Lossless ceiling whatever the depth; so is more than 24 bits.
    [InlineData("FLAC", "a.flac", 16, 96000, "Hi-Res Lossless", "16-bit/96 kHz FLAC")]
    [InlineData("FLAC", "a.flac", 32, 48000, "Hi-Res Lossless", "32-bit/48 kHz FLAC")]
    // Apple's Lossless tier: CD quality up to 24-bit/48 kHz (the reference screenshot's 24-bit/48 kHz ALAC).
    [InlineData("MPEG-4 Audio (alac)", "a.m4a", 24, 48000, "Lossless", "24-bit/48 kHz ALAC")]
    [InlineData("MPEG-4 Audio (alac)", "a.m4a", 16, 48000, "Lossless", "16-bit/48 kHz ALAC")]
    [InlineData("FLAC", "a.flac", 16, 44100, "Lossless", "16-bit/44.1 kHz FLAC")]
    [InlineData("PCM", "a.wav", 16, 44100, "Lossless", "16-bit/44.1 kHz WAV")]
    // A lossless file whose depth the scan could not read is still Lossless, by rate alone.
    [InlineData("MPEG-4 Audio (alac)", "a.m4a", 0, 44100, "Lossless", "44.1 kHz ALAC")]
    [InlineData("MPEG-4 Audio (alac)", "a.m4a", 0, 96000, "Hi-Res Lossless", "96 kHz ALAC")]
    public void Lossless_IsClassedByApplesTiers(string codec, string path, int bits, int rate, string label, string detail)
    {
        var q = TrackQuality.Of(Make(codec, path, bits, rate))!;
        Assert.Equal(label, q.Label);
        Assert.Equal(detail, q.Detail);
        Assert.True(q.IsLossless);
        Assert.Equal(label == "Hi-Res Lossless" ? TrackQuality.HiResMeaning : Track.LosslessDescription, q.Meaning);
    }

    [Theory]
    [InlineData("MPEG-4 Audio (mp4a)", "a.m4a", 256, 44100, "AAC", "256 kbps 44.1 kHz AAC")]
    [InlineData("MPEG Version 1 Audio, Layer 3", "a.mp3", 320, 48000, "MP3", "320 kbps 48 kHz MP3")]
    // The desktop's label word for word (Track.AudioQualityBadge): OPUS and OGG, as the PC shows them.
    [InlineData("Opus Version 1 Audio", "a.opus", 128, 48000, "OPUS", "128 kbps 48 kHz OPUS")]
    [InlineData("Vorbis Version 0 Audio", "a.ogg", 192, 44100, "OGG", "192 kbps 44.1 kHz OGG")]
    [InlineData("Microsoft WMA2 Audio", "a.wma", 128, 44100, "WMA", "128 kbps 44.1 kHz WMA")]
    // No bitrate known: the line is what is left.
    [InlineData("MPEG-4 Audio (mp4a)", "a.m4a", 0, 44100, "AAC", "44.1 kHz AAC")]
    [InlineData("", "a.mp3", 0, 0, "MP3", "MP3")]
    public void Lossy_IsItsCodec_WithTheBitrate(string codec, string path, int bitrate, int rate, string label, string detail)
    {
        var q = TrackQuality.Of(Make(codec, path, 0, rate, bitrate))!;
        Assert.Equal(label, q.Label);
        Assert.Equal(detail, q.Detail);
        Assert.False(q.IsLossless);
        Assert.Equal(Track.LossyDescription, q.Meaning);
    }

    [Fact]
    public void NoCodecToName_HasNoBadge()
    {
        Assert.Null(TrackQuality.Of(null));
        Assert.Null(TrackQuality.Of(Make("", "a.bin", 16, 44100)));
    }

    /// <summary>The badge follows the track: a lossless file, then an MP3, then nothing loaded.</summary>
    [Fact]
    public void NowPlaying_BadgeFollowsTheTrack()
    {
        var flac = Make("FLAC", "a.flac", 24, 96000);
        flac.Id = System.Guid.NewGuid(); flac.Title = "A";
        var mp3 = Make("MPEG Version 1 Audio, Layer 3", "b.mp3", 0, 44100, 320);
        mp3.Id = System.Guid.NewGuid(); mp3.Title = "B";
        using var rig = MobileFixtures.MakeRig(new[] { flac, mp3 });
        var changes = 0;
        rig.Shell.Player.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(NowPlayingViewModel.Quality)) changes++; };

        Assert.False(rig.Shell.Player.HasQuality);
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        Assert.Equal("Hi-Res Lossless", rig.Shell.Player.Quality!.Label);
        Assert.True(changes >= 1);
        rig.Shell.Player.NextCommand.Execute(null);
        Assert.Equal("MP3", rig.Shell.Player.Quality!.Label);
        Assert.Equal("320 kbps 44.1 kHz MP3", rig.Shell.Player.Quality.Detail);

        // The card: the shell opens it for the playing file and OK closes it (Back closes it too).
        rig.Shell.OpenNowPlayingCommand.Execute(null);
        rig.Shell.ShowQualityCardCommand.Execute(null);
        var card = Assert.IsType<QualityCardViewModel>(rig.Shell.PopMenu);
        Assert.True(rig.Shell.IsMenuOpen);
        Assert.Equal("MP3", card.Label);
        Assert.Equal(Track.LossyDescription, card.Meaning);
        card.DoneCommand.Execute(null);
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        rig.Shell.ShowQualityCardCommand.Execute(null);
        Assert.True(rig.Shell.TryHandleBack());
        Assert.False(rig.Shell.IsMenuOpen);
        Assert.True(rig.Shell.IsNowPlayingOpen);
        // Another menu takes its place; nothing loaded, nothing to show.
        rig.Shell.ShowQualityCardCommand.Execute(null);
        rig.Shell.OpenNowPlayingMenuCommand.Execute(null);
        Assert.IsType<ContextSheetViewModel>(rig.Shell.PopMenu);
        Assert.Null(rig.Shell.QualityCard);
    }
}
