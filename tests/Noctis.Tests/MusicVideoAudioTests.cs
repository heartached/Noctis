using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Waveform;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Music video audio (Discord, aaron): with the setting on, a song whose music video is found
/// plays the clip's own audio, the song file being the engine's fallback. Decided at song start
/// only; everything timed against the song file (lyric authoring, waveform, quality badge,
/// synced lyrics whose clip is a different length) steps aside while the clip's audio plays.
/// </summary>
public class MusicVideoAudioTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "mv-audio-" + Guid.NewGuid().ToString("N"));
    public MusicVideoAudioTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ── Harness ──

    /// <summary>A 3-minute FLAC song, with (optionally) a same-name clip beside it.</summary>
    private Track Song(string stem, bool withClip = true)
    {
        var audio = Path.Combine(_dir, stem + ".flac");
        File.WriteAllBytes(audio, new byte[] { 1 });
        if (withClip) File.WriteAllBytes(Clip(stem), new byte[] { 1 });
        return new Track
        {
            Id = Guid.NewGuid(),
            Title = stem,
            Artist = "Kanye West",
            FilePath = audio,
            Codec = "FLAC",
            SampleRate = 44100,
            BitsPerSample = 16,
            Duration = TimeSpan.FromMinutes(3),
        };
    }

    private string Clip(string stem) => Path.Combine(_dir, stem + ".mp4");

    private static (PlayerViewModel Vm, FakeAudioPlayer Engine) Player(bool videoAudio = true, bool videos = true)
    {
        var engine = new FakeAudioPlayer();
        var vm = new PlayerViewModel(engine, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService())
        {
            MusicVideosEnabled = videos,
            MusicVideoAudioEnabled = videoAudio,
        };
        return (vm, engine);
    }

    /// <summary>The engine fell back to the song file (broken clip): it now reports that
    /// path, and the view model picks it up on the engine's next report.</summary>
    private static void EngineFallsBackTo(FakeAudioPlayer engine, string songFile)
    {
        engine.CurrentMediaPath = songFile;
        engine.RaiseDurationResolved(TimeSpan.FromMinutes(3));
        Dispatcher.UIThread.RunJobs();
    }

    private static void Pump(Func<bool> done, int timeoutMs = 5000)
    {
        var end = Environment.TickCount64 + timeoutMs;
        while (!done() && Environment.TickCount64 < end)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    // PlayTrack arms a 2s commit guard and a seek-settle window against stale positions from
    // the outgoing song; clear both so a test can feed end-of-track ticks without sleeping.
    private static void ClearStartGuards(PlayerViewModel vm)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        typeof(PlayerViewModel).GetField("_autoMixCommitGuardUntilUtc", flags)!.SetValue(vm, DateTime.MinValue);
        typeof(PlayerViewModel).GetField("_lastSeekTime", flags)!.SetValue(vm, DateTime.MinValue);
    }

    private static void Tick(FakeAudioPlayer engine, TimeSpan position)
    {
        engine.RaisePositionChanged(position);
        Dispatcher.UIThread.RunJobs();
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    // ── 1. Setting off ──

    [AvaloniaFact]
    public void SettingOff_PlaysTheSongFile_WithNoFallback()
    {
        Assert.False(new AppSettings().MusicVideoUseVideoAudio); // off by default
        var (vm, engine) = Player(videoAudio: false);
        var song = Song("Runaway");

        vm.ReplaceQueueAndPlay(new[] { song }, 0);

        Assert.Equal(new[] { song.FilePath }, engine.PlayedPaths);
        Assert.Equal(new string?[] { null }, engine.PlayedFallbacks);
        Assert.False(vm.IsPlayingMusicVideoAudio);
        Assert.True(vm.HasMusicVideo); // the video itself still shows, muted
    }

    // ── 2. Setting on, clip beside the song ──

    [AvaloniaFact]
    public void SettingOn_PlaysTheClip_WithTheSongFileAsFallback()
    {
        var (vm, engine) = Player();
        var song = Song("Runaway");

        vm.ReplaceQueueAndPlay(new[] { song }, 0);

        Assert.Equal(new[] { Clip("Runaway") }, engine.PlayedPaths);
        Assert.Equal(new string?[] { song.FilePath }, engine.PlayedFallbacks);
        Assert.True(vm.IsPlayingMusicVideoAudio);
        Assert.StartsWith("Music video audio (MP4)", vm.SignalPathStages[0].Detail);
        Assert.NotEqual("Lossless", vm.SignalPathQuality);
    }

    // ── 3. Setting on, but the song is not eligible ──

    [AvaloniaFact]
    public void SettingOn_ButMusicVideosOff_PlaysTheSongFile()
    {
        var (vm, engine) = Player(videos: false);
        var song = Song("Stronger");

        vm.ReplaceQueueAndPlay(new[] { song }, 0);

        Assert.Equal(song.FilePath, engine.PlayedPaths.Single());
        Assert.Null(engine.PlayedFallbacks.Single());
        Assert.False(vm.IsPlayingMusicVideoAudio);
    }

    [AvaloniaTheory]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("remember")]
    [InlineData("authoring")]
    public void SettingOn_ButSongTimedAgainstItsFile_PlaysTheSongFile(string reason)
    {
        var (vm, engine) = Player();
        var song = Song("Flashing Lights");
        switch (reason)
        {
            case "start": song.StartTimeMs = 12_000; break;
            case "stop": song.StopTimeMs = 150_000; break;
            case "remember": song.RememberPlaybackPosition = true; break;
            case "authoring": vm.RequestOriginalAudio(song); break;
        }

        vm.ReplaceQueueAndPlay(new[] { song }, 0);

        Assert.Equal(song.FilePath, engine.PlayedPaths.Single());
        Assert.Null(engine.PlayedFallbacks.Single());
        Assert.False(vm.IsPlayingMusicVideoAudio);
    }

    [Fact]
    public void Resolver_RefusesRemoteStreamsAndCdTracks_EvenWithAClipPath()
    {
        var clip = Clip("Heartless");
        var local = new Track { FilePath = Path.Combine(_dir, "Heartless.flac") };
        var remote = new Track { FilePath = "https://server/rest/stream?id=1", SourceType = SourceType.Navidrome };
        var cd = new Track { FilePath = "cdda:///D:/#3", SourceType = SourceType.AudioCd };

        Assert.True(PlayerViewModel.UsesMusicVideoAudio(local, clip, videosOn: true, audioOn: true, originalAudioRequested: false));
        Assert.False(PlayerViewModel.UsesMusicVideoAudio(remote, clip, true, true, false));
        Assert.False(PlayerViewModel.UsesMusicVideoAudio(cd, clip, true, true, false));
        Assert.False(PlayerViewModel.UsesMusicVideoAudio(local, null, true, true, false));
        Assert.False(PlayerViewModel.UsesMusicVideoAudio(local, clip, true, audioOn: false, false));
    }

    // ── 4. Never switches mid-song ──

    [AvaloniaFact]
    public void TogglingMidSong_NeverRestartsOrSeeksThePlayingSong()
    {
        var (vm, engine) = Player();
        var song = Song("Runaway");
        vm.ReplaceQueueAndPlay(new[] { song }, 0);
        Assert.Single(engine.PlayedPaths);

        vm.MusicVideoAudioEnabled = false;
        vm.MusicVideosEnabled = false;
        vm.MusicVideosEnabled = true;
        vm.MusicVideoAudioEnabled = true;
        vm.MusicVideoAudioEnabled = false;
        vm.SetLyricsPageActions(() => { }, () => { }, () => { }, () => { }, () => { }, true, false, true, true);
        vm.ClearLyricsPageActions();
        Dispatcher.UIThread.RunJobs();

        Assert.Single(engine.PlayedPaths);
        Assert.Empty(engine.Seeks);
        Assert.True(vm.IsPlayingMusicVideoAudio); // the song keeps the source it started on

        // The next song start is where the new choice lands.
        vm.ReplaceQueueAndPlay(new[] { song }, 0);
        Assert.Equal(song.FilePath, engine.PlayedPaths[^1]);
        Assert.False(vm.IsPlayingMusicVideoAudio);
    }

    // ── 5. Gapless: the staged path is exactly what plays ──

    [AvaloniaFact]
    public void Gapless_StagesTheClip_AndTheAdvancePlaysThatExactPath()
    {
        var (vm, engine) = Player();
        var first = Song("Good Morning", withClip: false);
        var next = Song("Champion");
        vm.ReplaceQueueAndPlay(new[] { first, next }, 0);
        ClearStartGuards(vm);

        // Inside the prepare lead; the next song's clip is looked for off the UI thread, so it
        // is staged on the first tick after the probe finished.
        Pump(() =>
        {
            Tick(engine, TimeSpan.FromMinutes(3) - TimeSpan.FromSeconds(5));
            return engine.PreparedPaths.Count > 0;
        });
        var staged = Assert.Single(engine.PreparedPaths);
        Assert.Equal(Clip("Champion"), staged);

        // Resolved once, when staged: the advance must not look again (the clip moving away
        // now would otherwise flip it to the song file and miss the staged standby).
        File.Delete(Clip("Champion"));
        Tick(engine, TimeSpan.FromMinutes(3) - TimeSpan.FromMilliseconds(300)); // handoff lead

        Assert.Equal(next.Id, vm.CurrentTrack?.Id);
        Assert.Equal(staged, engine.PlayedPaths[^1]);
        Assert.Equal(next.FilePath, engine.PlayedFallbacks[^1]);
        // The music video reuses that probe too rather than looking a second time.
        Assert.Equal(staged, vm.CurrentMusicVideoPath);
    }

    // ── 6. The engine fell back to the song file ──

    [AvaloniaFact]
    public void EngineFallback_IsNotMusicVideoAudio_AndShowsTheSongsSignalPath()
    {
        var (vm, engine) = Player();
        var song = Song("Runaway");
        vm.ReplaceQueueAndPlay(new[] { song }, 0);
        Assert.True(vm.IsPlayingMusicVideoAudio);

        EngineFallsBackTo(engine, song.FilePath);

        Assert.False(vm.IsPlayingMusicVideoAudio);
        Assert.StartsWith("FLAC", vm.SignalPathStages[0].Detail);
        Assert.Equal("Lossless", vm.SignalPathQuality);
    }

    // ── 7. LRC editor stamping ──

    [AvaloniaFact]
    public void LrcEditor_DoesNotStamp_WhileTheClipsAudioPlays()
    {
        var (vm, engine) = Player();
        var song = Song("Runaway");
        vm.ReplaceQueueAndPlay(new[] { song }, 0);
        var editor = new LrcEditorViewModel(song, vm, new StubMetadata(), syncedLyrics: null, plainLyrics: "One\nTwo");

        editor.StampCurrentCommand.Execute(null);
        editor.StampLineCommand.Execute(editor.Lines[1]);

        Assert.All(editor.Lines, l => Assert.Null(l.Timestamp));
        Assert.Equal(0, editor.SelectedIndex);
        Assert.Contains("music video", editor.StatusText);

        // Back on the song file, stamping works again.
        EngineFallsBackTo(engine, song.FilePath);
        editor.StampCurrentCommand.Execute(null);
        Assert.NotNull(editor.Lines[0].Timestamp);
    }

    // ── 8. Waveform ──

    private sealed class InstantDecoder : IWaveformDecoder
    {
        public int Count;
        public WaveformData? Decode(string path, CancellationToken ct)
        {
            Interlocked.Increment(ref Count);
            return new WaveformData(new byte[] { 10, 200, 90, 30 }, new byte[] { 5, 150, 60, 20 });
        }
    }

    [AvaloniaFact]
    public void Waveform_IsHidden_WhileTheClipsAudioPlays()
    {
        var (vm, engine) = Player();
        var song = Song("Runaway");
        var decoder = new InstantDecoder();
        using var service = new WaveformService(decoder, new WaveformCache(Path.Combine(_dir, "cache")),
            new WaveformService.Options(TimeSpan.Zero, LowPriorityThread: false));
        vm.SetWaveformService(service);
        vm.WaveformSeekBarEnabled = true;

        vm.ReplaceQueueAndPlay(new[] { song }, 0);
        Pump(() => decoder.Count > 0);
        Pump(() => false, 150); // let the ready post land

        Assert.Equal(1, decoder.Count);
        Assert.True(vm.IsPlayingMusicVideoAudio);
        Assert.Null(vm.CurrentWaveform);

        // The engine fell back to the song file: its waveform is right again.
        EngineFallsBackTo(engine, song.FilePath);
        Assert.NotNull(vm.CurrentWaveform);
        service.Dispose();
    }

    // ── 9. Engine helpers ──

    [Theory]
    [InlineData(true, true, false)]   // clip opened with an audio stream: play it
    [InlineData(true, false, true)]   // video-only clip: the song file
    [InlineData(false, false, true)]  // unreadable clip: the song file
    public void Engine_FallsBackToTheSongFile_ForAClipThatWontPlay(bool parsed, bool hasAudio, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.ShouldPlayFallback(parsed, hasAudio));
    }

    [Theory]
    [InlineData(@"C:\Music\09 - Runaway.mp4", true)]
    [InlineData(@"C:\Music\videos\09 - Runaway.MKV", true)]
    [InlineData("/music/Runaway.webm", true)]
    [InlineData(@"C:\Music\09 - Runaway.flac", false)]
    [InlineData(@"C:\Music\09 - Runaway.m4a", false)]
    public void Engine_VideoContainerCheck_MatchesTheClipFinder(string path, bool expected)
    {
        Assert.Equal(expected, VlcAudioPlayer.IsVideoContainerPath(path));
    }

    // ── 10. A clip in the library is its own "clip" ──

    [AvaloniaFact]
    public void ClipPlayedAsATrack_FindsItself_AndPlaysWithoutFallback()
    {
        var clip = Clip("Runaway");
        File.WriteAllBytes(clip, new byte[] { 1 });
        Assert.Equal(clip, MusicVideoLocator.Find(clip));

        var (vm, engine) = Player();
        vm.ReplaceQueueAndPlay(new[] { new Track { Id = Guid.NewGuid(), Title = "Runaway", FilePath = clip, Duration = TimeSpan.FromMinutes(9) } }, 0);

        Assert.Equal(clip, engine.PlayedPaths.Single());
        Assert.Null(engine.PlayedFallbacks.Single());
        Assert.False(vm.IsPlayingMusicVideoAudio);
    }

    // ── 11. Decision 2: lyrics open on Plain when the clip's length is off ──

    private static string Lrc() => string.Join("\n",
        Enumerable.Range(0, 20).Select(i => $"[00:{i * 3:00}.00]Line {i}"));

    private async Task<(LyricsViewModel Lyrics, PlayerViewModel Vm, FakeAudioPlayer Engine)> PlayWithLyrics(TimeSpan? clipLength)
    {
        var (vm, engine) = Player();
        var song = Song("Runaway");
        song.SyncedLyrics = Lrc();
        vm.ReplaceQueueAndPlay(new[] { song }, 0);
        if (clipLength is { } length)
        {
            engine.Duration = length;
            engine.RaiseDurationResolved(length);
            Dispatcher.UIThread.RunJobs();
        }

        var lyrics = new LyricsViewModel(vm, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), new FakeLibraryService());
        lyrics.SetLyricsSurfaceVisible(true);
        lyrics.EnsureLyricsForCurrentTrack();
        var deadline = Environment.TickCount64 + 5000;
        while (Environment.TickCount64 < deadline && !lyrics.HasSyncedLyricsAvailable)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Assert.True(lyrics.HasSyncedLyricsAvailable, "harness failed to load synced lyrics");
        return (lyrics, vm, engine);
    }

    [AvaloniaFact]
    public async Task Lyrics_OpenOnPlain_WhenTheClipIsMoreThanTwoSecondsOff()
    {
        var (lyrics, vm, _) = await PlayWithLyrics(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(24));

        Assert.True(vm.MusicVideoAudioLengthDiffers);
        Assert.True(lyrics.IsUnsyncTabSelected);
        Assert.False(lyrics.IsSyncTabSelected);

        lyrics.SelectSyncTabCommand.Execute(null); // Synced stays one click away
        Assert.True(lyrics.IsSyncTabSelected);
    }

    [AvaloniaFact]
    public async Task Lyrics_StayOnSynced_WhenTheClipIsWithinTwoSeconds()
    {
        var (lyrics, vm, _) = await PlayWithLyrics(TimeSpan.FromMinutes(3) + TimeSpan.FromSeconds(1.5));

        Assert.True(vm.IsPlayingMusicVideoAudio);
        Assert.False(vm.MusicVideoAudioLengthDiffers);
        Assert.True(lyrics.IsSyncTabSelected);
    }

    [AvaloniaFact]
    public async Task Lyrics_MoveToPlain_WhenTheEngineReportsTheLengthAfterTheyOpened()
    {
        var (lyrics, _, engine) = await PlayWithLyrics(clipLength: null);
        Assert.True(lyrics.IsSyncTabSelected);

        engine.RaiseDurationResolved(TimeSpan.FromMinutes(4));
        Dispatcher.UIThread.RunJobs();

        Assert.True(lyrics.IsUnsyncTabSelected);
    }

    // ── 12. Decision 3: ReplayGain borrowed from the song file ──

    [Fact]
    public void ReplayGain_ClipWithoutTags_BorrowsTheSongFiles()
    {
        const string song = @"C:\Music\09 - Runaway.flac";
        Assert.True(VlcAudioPlayer.BorrowsReplayGain((null, null), song));
        // The clip's own tags win.
        Assert.False(VlcAudioPlayer.BorrowsReplayGain((-6.2, null), song));
        Assert.False(VlcAudioPlayer.BorrowsReplayGain((null, -7.1), song));
        // A plain play (no song file behind it) never borrows.
        Assert.False(VlcAudioPlayer.BorrowsReplayGain((null, null), null));
    }

    // ── Music video sync (existing-bug fixes that ride along) ──

    [Theory]
    [InlineData(false, 10_000, 10_200, 200_000, 350, false, "None")]    // within tolerance
    [InlineData(false, 10_000, 10_400, 200_000, 350, false, "Seek")]    // drifted past it
    [InlineData(false, 10_000, 10_000, 200_000, 350, true, "Seek")]     // forced on attach
    [InlineData(true, 0, 0, 200_000, 350, false, "Restart")]            // ended, song replayed
    [InlineData(true, 0, 199_900, 200_000, 350, false, "None")]         // song past the clip's end
    [InlineData(true, 0, 5_000, 0, 350, false, "None")]                 // length unknown: no loop
    public void VideoSync_SeeksOrRestartsAnEndedClip(bool ended, long timeMs, long targetMs, long lengthMs,
        int toleranceMs, bool force, string expected)
    {
        Assert.Equal(expected, VideoBackdrop.PlanSync(ended, timeMs, targetMs, lengthMs, toleranceMs, force).ToString());
    }

    [AvaloniaFact]
    public void VideoSync_FollowsWhatIsHeard_NotWhatTheEngineFed()
    {
        var (vm, engine) = Player(videoAudio: false);
        engine.OutputLatency = TimeSpan.FromMilliseconds(100);
        vm.CurrentTrack = Song("Runaway");
        Assert.True(vm.HasMusicVideo);

        vm.Position = TimeSpan.FromSeconds(30);
        Assert.Equal(TimeSpan.FromMilliseconds(29_900), vm.MusicVideoSyncPosition);
        vm.Position = TimeSpan.FromMilliseconds(40);
        Assert.Equal(TimeSpan.Zero, vm.MusicVideoSyncPosition);
    }
}
