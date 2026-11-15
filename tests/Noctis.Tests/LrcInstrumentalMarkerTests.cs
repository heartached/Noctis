using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #236: LRCGET saves an LRCLIB instrumental track as a lyrics file holding only
/// "[au: instrumental]". The parser did not know the [au:] header tag, so that one line was
/// shown as the song's lyric for the whole song (desktop and phone). It means "no lyrics".
/// </summary>
public class LrcInstrumentalMarkerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    public static TheoryData<string> Markers => new()
    {
        "[au: instrumental]",
        "[au:instrumental]",
        "[AU: Instrumental]",
        "  [au: instrumental]  ",
        "﻿[au: instrumental]",
        "\n\n[au: instrumental]\n\n",
        "[au: instrumental]\r\n",
        "[ar: Some Artist]\n[ti: Some Title]\n[au: instrumental]",
    };

    [Theory]
    [MemberData(nameof(Markers))]
    public void Parser_ShowsNoLineForTheMarker(string content)
    {
        Assert.Empty(LrcParser.Parse(content));
    }

    [Theory]
    [MemberData(nameof(Markers))]
    public void Marker_IsAnInstrumentalPlaceholder(string content)
    {
        Assert.True(LyricsSearchSelector.IsInstrumentalPlaceholder(content));
    }

    [Theory]
    [InlineData("[au: Some Writer]\n[00:01.00]first line\n[00:02.00]second line")]
    [InlineData("[au: Some Writer]\nplain first line")]
    [InlineData("[au: Some Writer]")]
    [InlineData("[au: instrumental]\n[00:01.00]but someone sings")]
    [InlineData("[ar: x]\n[ti: y]")]
    public void RealLyricsOrAnotherAuthor_AreNotInstrumental(string content)
    {
        Assert.False(LyricsSearchSelector.IsInstrumentalPlaceholder(content));
    }

    [Fact]
    public void Parser_HidesAnAuthorTag_ButKeepsTheLyrics()
    {
        var lines = LrcParser.Parse("[au: Some Writer]\n[AR: Some Artist]\n[00:01.00]first line\n[00:02.00]second line");

        Assert.Equal(new[] { "first line", "second line" }, lines.Select(l => l.Text));
    }

    // ── Phone: the shared loader and the lyrics page ──

    private static Track PhoneTrack(string lyrics = "", string synced = "") =>
        new() { Id = Guid.NewGuid(), Title = "song", FilePath = "content://x/song.mp3", Duration = TimeSpan.FromSeconds(60), Lyrics = lyrics, SyncedLyrics = synced };

    [Theory]
    [InlineData(".lrc")]
    [InlineData(".elrc")]
    public void Loader_MarkerSidecar_IsInstrumental(string ext)
    {
        var files = new FakeTrackFiles();
        files.Sidecars[ext] = "[au: instrumental]";

        var result = LyricsLoader.Load(PhoneTrack(lyrics: "tag words"), files, joinSplitWords: false);

        Assert.Equal(LyricsSource.Instrumental, result.Source);
        Assert.Empty(result.Lines);
    }

    [Theory]
    [InlineData("[au: instrumental]", "")]
    [InlineData("", "[au: instrumental]")]
    public void Loader_MarkerInTheSongsLyricsFields_IsInstrumental(string lyrics, string synced)
    {
        var result = LyricsLoader.Load(PhoneTrack(lyrics, synced), new FakeTrackFiles(), joinSplitWords: false);

        Assert.Equal(LyricsSource.Instrumental, result.Source);
        Assert.Empty(result.Lines);
    }

    private sealed class CountingOnline : IOnlineLyricsSource
    {
        public int Calls { get; private set; }
        public Task<OnlineLyrics?> FindAsync(Track track, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<OnlineLyrics?>(new OnlineLyrics("[00:01.00]online line", false));
        }
    }

    [Fact]
    public async Task PhonePage_MarkerSidecar_ShowsInstrumental_WithoutLookingOnline()
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var np = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = "[au: instrumental]";
        var online = new CountingOnline();
        var vm = new LyricsPageViewModel(player, np, files, persistence, work => Task.FromResult(work())) { Online = online };

        np.PlayTracks(new[] { PhoneTrack() }, 0);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!vm.IsInstrumental && DateTime.UtcNow < deadline) await Task.Delay(15);

        Assert.True(vm.IsInstrumental);
        Assert.False(vm.HasLyrics);
        Assert.Empty(vm.Lines);
        Assert.Equal(0, online.Calls);
    }

    // ── Desktop: the lyrics page's local probe ──

    private string DesktopSong(string name)
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, name + ".mp3");
        File.WriteAllBytes(path, new byte[16]);
        return path;
    }

    [Theory]
    [InlineData(".lrc")]
    [InlineData(".elrc")]
    public void DesktopProbe_MarkerSidecar_IsInstrumental(string ext)
    {
        var path = DesktopSong("sidecar" + ext.TrimStart('.'));
        File.WriteAllText(Path.ChangeExtension(path, ext), "[au: instrumental]\n");

        var probe = LyricsViewModel.ProbeLocalLyricSources(new Track { FilePath = path, Title = "t" }, false, "en");

        Assert.Equal(LyricsViewModel.InstrumentalSource, probe.Source);
        Assert.Null(probe.Lines);
    }

    [Theory]
    [InlineData("[au: instrumental]", "")]
    [InlineData("", "[au: instrumental]")]
    public void DesktopProbe_MarkerInTheSongsLyricsFields_IsInstrumental(string lyrics, string synced)
    {
        var path = DesktopSong("fields" + lyrics.Length);
        var row = new Track { FilePath = path, Title = "t", Lyrics = lyrics, SyncedLyrics = synced };

        var probe = LyricsViewModel.ProbeLocalLyricSources(row, false, "en");

        Assert.Equal(LyricsViewModel.InstrumentalSource, probe.Source);
    }

    [Fact]
    public void DesktopProbe_AuthorTagWithLyrics_StillShowsTheLyrics()
    {
        var path = DesktopSong("author");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), "[au: Some Writer]\n[00:01.00]first line");

        var probe = LyricsViewModel.ProbeLocalLyricSources(new Track { FilePath = path, Title = "t" }, false, "en");

        Assert.Equal("Sidecar:Lrc", probe.Source);
        Assert.Equal(new[] { "first line" }, probe.Lines!.Select(l => l.Text));
    }
}
