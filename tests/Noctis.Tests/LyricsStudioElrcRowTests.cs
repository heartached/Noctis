using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio review rows in ELRC (09-23): with word timings chosen each row shows the line
/// as it will be saved, word tags included, and the tags can be edited in place. Edits are read
/// back on commit; a bad time leaves the words alone; nudges and shifts rewrite the row.
/// </summary>
public class LyricsStudioElrcRowTests : IDisposable
{
    private const string Body = "<00:37.09>Pop <00:37.45>a <00:37.75>pill <00:38.34>I <00:38.49>feel <00:39.08>fucked <00:40.00>up<00:41.00>";

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "noctis-studio-elrc-row-" + Guid.NewGuid().ToString("N"));

    public LyricsStudioElrcRowTests() => Directory.CreateDirectory(_tmp);

    public void Dispose()
    {
        try { Directory.Delete(_tmp, true); } catch { }
    }

    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    /// <summary>A song whose .elrc has one word-timed line and one line-level line, open for review.</summary>
    private LyricsStudioViewModel Review(bool wordTimings)
    {
        var track = new Track { Title = "song", Artist = "A", FilePath = Path.Combine(_tmp, "song.mp3") };
        File.WriteAllText(Path.ChangeExtension(track.FilePath, ".elrc"), $"[00:37.09]{Body}\n[00:45.00]no word times here");
        var vm = new LyricsStudioViewModel(new[] { track }, new NoEngine(_tmp), new LyricsWriter(null!, null),
            new FakeLibraryService(), null, () => new AppSettings { LyricsStudioWordTimings = wordTimings }, _ => { });
        vm.Selected = vm.Queue[0];
        Assert.Equal(2, vm.ReviewLines.Count);
        return vm;
    }

    private static List<(string, TimeSpan)> Snapshot(ReviewLine line) => line.Words.Select(w => (w.Text, w.Start)).ToList();

    [AvaloniaFact]
    public void Row_ShowsTheSavedElrc_WhenWordTimingsOn_PlainWhenOff_AndFollowsTheChip()
    {
        var vm = Review(wordTimings: true);
        var line = vm.ReviewLines[0];

        Assert.Equal(Body, line.RowText);
        Assert.Equal("no word times here", vm.ReviewLines[1].RowText); // written as plain [t]text
        Assert.Equal($"[00:37.09]{Body}\n[00:45.00]no word times here", vm.BuildReviewText());

        var raised = new List<string?>();
        line.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        vm.WordTimings = false;
        Assert.Contains(nameof(ReviewLine.RowText), raised);
        Assert.Equal("Pop a pill I feel fucked up", line.RowText);
        Assert.Equal("no word times here", vm.ReviewLines[1].RowText);

        vm.WordTimings = true;
        Assert.Equal(Body, line.RowText);
    }

    [AvaloniaFact]
    public void EditingATimeAndAWord_UpdatesTheWords_AndTheSavedElrc_OnCommit()
    {
        var vm = Review(wordTimings: true);
        var line = vm.ReviewLines[0];
        var changed = 0;
        line.Changed += () => changed++;

        var edited = "<00:37.09>Pop <00:37.45>a <00:37.80>pill <00:38.34>I <00:38.49>feel <00:39.08>messed <00:40.00>up<00:41.20>";
        line.RowText = edited;
        Assert.Equal(S(37.75), line.Words[2].Start); // typing is held until Enter / focus loss
        Assert.Equal(0, changed);

        Assert.True(line.CommitRowText());

        Assert.Equal(S(37.80), line.Words[2].Start);
        Assert.Equal("messed", line.Words[5].Text);
        Assert.Equal("Pop a pill I feel messed up", line.Text);
        Assert.Equal(S(41.20), line.End);
        Assert.Equal(edited, line.RowText);
        Assert.False(line.HasEditError);
        Assert.True(changed > 0, "the edit marks the review dirty");
        Assert.StartsWith($"[00:37.09]{edited}\n", vm.BuildReviewText());
    }

    [AvaloniaFact]
    public void UntaggedWord_SharesTheSpanUpToTheNextTag()
    {
        var line = Review(wordTimings: true).ReviewLines[0];

        line.RowText = "<00:37.09>Pop <00:37.45>a big <00:37.75>pill <00:38.34>I <00:38.49>feel <00:39.08>fucked <00:40.00>up<00:41.00>";
        Assert.True(line.CommitRowText());

        Assert.Equal(8, line.Words.Count);
        Assert.Equal("big", line.Words[2].Text);
        Assert.Equal(S(37.60), line.Words[2].Start);
        Assert.Equal(S(37.75), line.Words[3].Start);
    }

    [AvaloniaFact]
    public void EditingOneWord_KeepsTheOtherTimesToTheMillisecond()
    {
        var line = new ReviewLine(new AlignedLine("one two", S(1.005), S(2.007),
            new[] { new AlignedWord("one", S(1.005), S(1.503)), new AlignedWord("two", S(1.503), S(2.007)) }, 0.9, false)) { ShowWordTags = true };
        Assert.Equal("<00:01.00>one <00:01.50>two<00:02.00>", line.RowText);

        line.RowText = "<00:01.00>won <00:01.50>two<00:02.00>";
        Assert.True(line.CommitRowText());

        Assert.Equal("won", line.Words[0].Text);
        Assert.Equal(S(1.005), line.Words[0].Start);
        Assert.Equal(S(1.503), line.Words[1].Start);
        Assert.Equal(S(2.007), line.End);
    }

    [AvaloniaTheory]
    [InlineData("<00:37.09>Pop <00:3x.45>a <00:37.75>pill <00:38.34>I <00:38.49>feel <00:39.08>fucked <00:40.00>up<00:41.00>")] // not a time
    [InlineData("<00:37.09>Pop <00:37.95>a <00:37.75>pill <00:38.34>I <00:38.49>feel <00:39.08>fucked <00:40.00>up<00:41.00>")] // backwards
    [InlineData("<00:37.09>Pop <00:37.45>a <00:37.75>pill <00:38.34>I <00:38.49>feel <00:39.08>fucked <00:40.00>up<00:39.50>")] // end before "up"
    [InlineData("<03:37.09>Pop <03:37.45>a <03:37.75>pill <03:38.34>I <03:38.49>feel <03:39.08>fucked <03:40.00>up<03:41.00>")] // minute typo
    [InlineData("<00:37.09>Pop <00:77.45>a <00:37.75>pill <00:38.34>I <00:38.49>feel <00:39.08>fucked <00:40.00>up<00:41.00>")] // 77 seconds
    public void InvalidEdit_LeavesTheWordsAlone_MarksTheRow_AndEscRestores(string typed)
    {
        var line = Review(wordTimings: true).ReviewLines[0];
        var before = Snapshot(line);
        var end = line.End;
        var changed = 0;
        line.Changed += () => changed++;

        line.RowText = typed;
        Assert.False(line.CommitRowText());

        Assert.Equal(before, Snapshot(line));
        Assert.Equal(end, line.End);
        Assert.Equal(0, changed);
        Assert.True(line.HasEditError);
        Assert.False(string.IsNullOrWhiteSpace(line.EditError));
        Assert.Equal(typed, line.RowText); // kept so it can be fixed

        Assert.True(line.RevertRowText());
        Assert.Equal(Body, line.RowText);
        Assert.False(line.HasEditError);
    }

    [AvaloniaFact]
    public void RefusedEdit_OnFocusLoss_ShowsTheLineAgain_StillMarked_UntilTypedIn()
    {
        var line = Review(wordTimings: true).ReviewLines[0];

        line.RowText = "<00:38.00>Pop <00:37.45>a";
        if (!line.CommitRowText()) line.RevertRowText(keepError: true); // what the row's LostFocus does

        Assert.Equal(Body, line.RowText);
        Assert.True(line.HasEditError);
        line.RowText = Body + " ";
        Assert.False(line.HasEditError);
    }

    [AvaloniaFact]
    public void NudgeShiftAndTap_RewriteTheRow()
    {
        var vm = Review(wordTimings: true);
        var line = vm.ReviewLines[0];
        var raised = 0;
        line.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(ReviewLine.RowText)) raised++; };

        line.NudgeWord(line.Words[1], TimeSpan.FromMilliseconds(50));
        Assert.Contains("<00:37.50>a ", line.RowText);
        Assert.True(raised > 0);

        vm.NudgeLaterCommand.Execute(null); // "Shift all lines" +0.1 s, also the ] key
        Assert.StartsWith("<00:37.19>Pop <00:37.60>a ", line.RowText);
        Assert.EndsWith("<00:40.10>up<00:41.10>", line.RowText);

        line.TapWord(0, S(37.30));
        Assert.StartsWith("<00:37.30>Pop <00:37.60>a ", line.RowText);
    }

    [AvaloniaFact]
    public void LineLevelRow_GetsWordTimings_WhenTagsAreTypedIntoIt()
    {
        var line = Review(wordTimings: true).ReviewLines[1];
        Assert.False(line.HasWordTimings);

        line.RowText = "<00:45.00>no <00:45.40>word <00:45.80>times <00:46.20>here<00:46.60>";
        Assert.True(line.CommitRowText());

        Assert.True(line.HasWordTimings);
        Assert.Equal(S(45.80), line.Words[2].Start);
        Assert.Equal("<00:45.00>no <00:45.40>word <00:45.80>times <00:46.20>here<00:46.60>", line.RowText);
    }

    private sealed class NoEngine : ILyricsStudioEngine
    {
        public NoEngine(string root) => Models = new WhisperModelManager(root);
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; }
        public IDisposable OpenSession(WhisperModelSize model) => throw new NotSupportedException();

        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException();
    }
}
