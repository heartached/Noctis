using Noctis.Models;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

public class LyricsStudioDraftStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-lsd-" + Guid.NewGuid().ToString("N"));
    private readonly LyricsStudioDraftStore _store;

    public LyricsStudioDraftStoreTests() => _store = new LyricsStudioDraftStore(_dir);

    private static TimeSpan S(double sec) => TimeSpan.FromSeconds(sec);

    private static LyricsStudioResult SampleResult(Track track) => new(
        track,
        new[]
        {
            new AlignedLine("Hello world", S(5.41), S(6.4),
                new[] { new AlignedWord("Hello", S(5.41), S(5.9)), new AlignedWord("world", S(5.9), S(6.4)) }, 0.9, false),
            new AlignedLine("Again", S(65.017), S(66), new[] { new AlignedWord("Again", S(65.017), S(66)) }, 0, true),
        },
        LyricsStudioSource.Lrclib, 0.8, "es", 42);

    [Fact]
    public void RoundTrip_KeepsLinesWordsTimesAndMetadata()
    {
        var track = new Track { Title = "Song", FilePath = @"C:\music\song.flac" };
        var result = SampleResult(track);

        _store.Save(track.Id, LyricsStudioDraft.From(result));
        Assert.True(_store.TryLoad(track.Id, out var draft));

        var restored = draft.ToResult(track);
        Assert.Equal(result.Source, restored.Source);
        Assert.Equal(result.Confidence, restored.Confidence);
        Assert.Equal("es", restored.Language);
        Assert.Equal(42, restored.HeardWords);
        Assert.Equal(2, restored.Lines.Count);
        Assert.Equal("Hello world", restored.Lines[0].Text);
        Assert.Equal(S(5.41), restored.Lines[0].Start);
        Assert.Equal(2, restored.Lines[0].Words.Count);
        Assert.Equal(S(5.9), restored.Lines[0].Words[1].Start);
        Assert.True(restored.Lines[1].Interpolated);
        Assert.Equal(@"C:\music\song.flac", draft.FilePath);
    }

    [Fact]
    public void Save_WithEditedLines_StoresTheEditsNotTheOriginal()
    {
        var track = new Track { Title = "Song" };
        var result = SampleResult(track);
        var edited = new[] { new AlignedLine("Hello there", S(6), S(7), new[] { new AlignedWord("Hello", S(6), S(6.5)), new AlignedWord("there", S(6.5), S(7)) }, 0.9, false) };

        _store.Save(track.Id, LyricsStudioDraft.From(result, edited));

        Assert.True(_store.TryLoad(track.Id, out var draft));
        Assert.Single(draft.Lines);
        Assert.Equal("Hello there", draft.Lines[0].Text);
        Assert.Equal(S(6), draft.Lines[0].Start);
    }

    [Fact]
    public void Delete_RemovesDraft_AndMissingIsNotAnError()
    {
        var track = new Track { Title = "Song" };
        Assert.False(_store.TryLoad(track.Id, out _));

        _store.Save(track.Id, LyricsStudioDraft.From(SampleResult(track)));
        Assert.True(_store.TryLoad(track.Id, out _));

        _store.Delete(track.Id);
        Assert.False(_store.TryLoad(track.Id, out _));
        _store.Delete(track.Id); // idempotent
    }

    [Fact]
    public void TryLoad_CorruptFile_ReturnsFalse()
    {
        var id = Guid.NewGuid();
        Directory.CreateDirectory(_dir);
        File.WriteAllText(Path.Combine(_dir, id.ToString("N") + ".json"), "{ not json");

        Assert.False(_store.TryLoad(id, out _));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }
}
