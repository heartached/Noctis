using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>Music video discovery: same-name clip beside the song, or in a videos folder.</summary>
public class MusicVideoLocatorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public MusicVideoLocatorTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private string Audio(string name = "09 - Runaway.flac")
    {
        var p = Path.Combine(_dir, name); File.WriteAllBytes(p, new byte[] { 1 }); return p;
    }

    [Fact]
    public void Sibling_SameStem_IsFound()
    {
        var audio = Audio();
        var video = Path.Combine(_dir, "09 - Runaway.mp4"); File.WriteAllBytes(video, new byte[] { 1 });
        Assert.Equal(video, MusicVideoLocator.Find(audio));
    }

    [Fact]
    public void VideosSubfolder_IsFound()
    {
        var audio = Audio();
        Directory.CreateDirectory(Path.Combine(_dir, "videos"));
        var video = Path.Combine(_dir, "videos", "09 - Runaway.mkv"); File.WriteAllBytes(video, new byte[] { 1 });
        Assert.Equal(video, MusicVideoLocator.Find(audio));
    }

    [Fact]
    public void UpperCaseExtension_IsFound()
    {
        // Exact ".mp4" probes missed "song.MP4" on case-sensitive file systems (Linux).
        var audio = Audio();
        var video = Path.Combine(_dir, "09 - Runaway.MP4"); File.WriteAllBytes(video, new byte[] { 1 });
        Assert.Equal(video, MusicVideoLocator.Find(audio));
    }

    [Fact]
    public void DifferentStem_OrNothing_ReturnsNull()
    {
        var audio = Audio();
        File.WriteAllBytes(Path.Combine(_dir, "10 - Hell of a Life.mp4"), new byte[] { 1 });
        Assert.Null(MusicVideoLocator.Find(audio));
        Assert.Null(MusicVideoLocator.Find(null));
        Assert.Null(MusicVideoLocator.Find(""));
    }
}

/// <summary>The player menu's "Music video" toggle only shows for a song that has a clip
/// (Discord, aaron 2026-09-21: it showed for every song), and it stays visible with the
/// feature off so it can be switched back on.</summary>
public class PlayerMusicVideoMenuTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public PlayerMusicVideoMenuTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private Noctis.Models.Track Song(string stem, bool withVideo)
    {
        var audio = Path.Combine(_dir, stem + ".flac"); File.WriteAllBytes(audio, new byte[] { 1 });
        if (withVideo) File.WriteAllBytes(Path.Combine(_dir, stem + ".mp4"), new byte[] { 1 });
        return new Noctis.Models.Track { Title = stem, FilePath = audio };
    }

    private static ViewModels.PlayerViewModel Player()
        => new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    [Fact]
    public void MenuItem_HiddenForSongWithoutClip_ShownForSongWithClip()
    {
        var player = Player();
        Assert.False(player.CurrentTrackHasMusicVideoFile);

        player.CurrentTrack = Song("Good Morning", withVideo: false);
        Assert.False(player.CurrentTrackHasMusicVideoFile);

        player.CurrentTrack = Song("Stronger", withVideo: true);
        Assert.True(player.CurrentTrackHasMusicVideoFile);
    }

    [Fact]
    public void MenuItem_StaysVisibleWhenFeatureOff_SoItCanBeReEnabled()
    {
        var player = Player();
        player.MusicVideosEnabled = false;
        player.CurrentTrack = Song("Flashing Lights", withVideo: true);
        Assert.True(player.CurrentTrackHasMusicVideoFile);
        Assert.False(player.HasMusicVideo);

        player.MusicVideosEnabled = true;
        Assert.True(player.HasMusicVideo);
    }
}
