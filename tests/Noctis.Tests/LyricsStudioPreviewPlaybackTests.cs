using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// How the Studio starts the song (Preview, a line's time pill, a word chip). Owner 10-01:
/// Preview after "Time every word" played no audio while the timeline moved; playing another
/// song from the Songs page first made it work.
/// </summary>
public class LyricsStudioPreviewPlaybackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-studio-play-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioPreviewPlaybackTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    // First line at 0:05, so Preview starts at 0:03 (two seconds of lead-in).
    private const string TwoLines = "[00:05.00]First line here\n[00:12.50]Second line here\n";

    private Track SongWithLrc(string name)
    {
        var path = Path.Combine(_root, name + ".mp3");
        File.WriteAllText(path, string.Empty);
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), TwoLines);
        return new Track { Title = name, Artist = "A", FilePath = path, Duration = TimeSpan.FromMinutes(3) };
    }

    private (LyricsStudioViewModel Studio, PlayerViewModel Player, FakeAudioPlayer Audio) Studio(Track song)
    {
        var audio = new FakeAudioPlayer();
        var player = new PlayerViewModel(audio, new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var studio = new LyricsStudioViewModel(new[] { song }, new NoEngine(), new LyricsWriter(null!, null), new FakeLibraryService(), player,
            () => new AppSettings(), _ => { })
        {
            ShowOnLyricsPage = (_, _) => Task.CompletedTask,
        };
        studio.Selected = studio.Queue[0]; // shows its .lrc
        Assert.True(studio.HasReview);
        return (studio, player, audio);
    }

    // ── A song that is not loaded opens at the time, with no seek racing the open ──

    [AvaloniaFact]
    public async Task Preview_OfASongNotLoaded_OpensItAtTheLeadIn_WithoutASeekAfterTheStart()
    {
        var song = SongWithLrc("song");
        var (studio, player, audio) = Studio(song);

        await studio.PreviewOnLyricsPageCommand.ExecuteAsync(null);

        Assert.Equal(song.FilePath, audio.PlayedPaths.Single());
        // The engine opens the file at 0:03 (":start-time", as a restored session resumes)…
        Assert.Equal(3000, audio.PendingSeekMs);
        // …instead of a seek sent while it was still opening the song: PlayTrack sets the
        // tag's length at once, so the old wait for a length never waited, and VlcAudioPlayer
        // dropped the seek (no media yet) or applied it to the song before.
        Assert.Empty(audio.Seeks);
        Assert.Equal(PlaybackState.Playing, player.State);
        Assert.Equal(TimeSpan.FromSeconds(3), player.Position);
    }

    [AvaloniaFact]
    public async Task TimePill_OfASongNotLoaded_OpensItAtTheLine()
    {
        var song = SongWithLrc("song");
        var (studio, player, audio) = Studio(song);

        await studio.PlayFromLineCommand.ExecuteAsync(studio.ReviewLines[1]);

        Assert.Equal(song.FilePath, audio.PlayedPaths.Single());
        Assert.Equal(12_500, audio.PendingSeekMs);
        Assert.Empty(audio.Seeks);
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    [AvaloniaFact]
    public async Task TimePill_WhileAnotherSongPlays_ReplacesItAndOpensAtTheLine()
    {
        var song = SongWithLrc("song");
        var other = new Track { Title = "other", Artist = "B", FilePath = Path.Combine(_root, "other.mp3"), Duration = TimeSpan.FromMinutes(4) };
        File.WriteAllText(other.FilePath, string.Empty);
        var (studio, player, audio) = Studio(song);
        player.ReplaceQueueAndPlay(new[] { other }, 0);

        await studio.PlayFromLineCommand.ExecuteAsync(studio.ReviewLines[0]);

        Assert.Equal(new[] { other.FilePath, song.FilePath }, audio.PlayedPaths);
        Assert.Equal(5000, audio.PendingSeekMs);
        Assert.Empty(audio.Seeks);
        Assert.Same(song, player.CurrentTrack);
        Assert.Empty(player.UpNext);
    }

    // ── A song already loaded is moved in place ──

    [AvaloniaFact]
    public async Task TimePill_OfTheLoadedSong_SeeksInPlace_AndResumes()
    {
        var song = SongWithLrc("song");
        var (studio, player, audio) = Studio(song);
        await studio.PlayFromLineCommand.ExecuteAsync(studio.ReviewLines[0]);
        player.PlayPauseCommand.Execute(null); // paused
        Assert.Equal(PlaybackState.Paused, player.State);

        await studio.PlayFromLineCommand.ExecuteAsync(studio.ReviewLines[1]);

        Assert.Single(audio.PlayedPaths);
        Assert.Equal(TimeSpan.FromSeconds(12.5), audio.Seeks.Single());
        Assert.Equal(PlaybackState.Playing, player.State);
    }

    private sealed class NoEngine : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = new(Path.Combine(Path.GetTempPath(), "noctis-no-models"));
        public IDisposable OpenSession(WhisperModelSize model) => new MemoryStream();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct)
            => throw new NotSupportedException();
    }
}
