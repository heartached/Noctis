using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #116 in Lyrics Studio: LRC lines that share a timestamp (original, romaji,
/// translation) are one sung line with layers. Studio shows and times only the sung line
/// and writes the layers back at its timestamp, so a save or a re-time never scatters the
/// romaji and translation across the song as if they were sung.
/// </summary>
public class LyricsStudioSameTimestampTests
{
    [Fact]
    public void ParseTimed_KeepsFileOrderAmongLinesSharingAStart_InALongFile()
    {
        // List.Sort is unstable past 16 items: same-start lines came back in any order.
        var lrc = string.Join("\n", Enumerable.Range(0, 30).SelectMany(i => new[]
        {
            $"[00:{i:00}.00]first {i}",
            $"[00:{i:00}.00]second {i}",
        }));

        var lines = ExistingLyricsLoader.ParseTimed(lrc);

        for (var i = 0; i < 30; i++)
            Assert.StartsWith($"first {i}", Texts(lines)[i]);
    }

    private const string IssueLrc =
        "[00:05.54]泣きじゃくる子供のように\n" +
        "[00:05.54]nakijakuru kodomo no you ni\n" +
        "[00:05.54]Como un niño que llora sin consuelo\n" +
        "[00:09.00]next";

    private static TimeSpan S(double s) => TimeSpan.FromSeconds(s);

    [Fact]
    public void ParseTimed_SameTimestampLines_AreOneSungLineWithCompanions()
    {
        var lines = ExistingLyricsLoader.ParseTimed(IssueLrc);

        Assert.Equal(2, lines.Count);
        var line = lines[0];
        Assert.Equal("泣きじゃくる子供のように", line.Text);
        Assert.Equal(new[] { "nakijakuru kodomo no you ni", "Como un niño que llora sin consuelo" }, line.Companions);
        // Its window runs to the next sung line, not to the romaji at the same instant.
        Assert.Equal(S(5.54), line.Start);
        Assert.Equal(S(9), line.End);
        Assert.Null(lines[1].Companions);
    }

    [Fact]
    public void ParseTimed_WordTimedOrOtherVoiceLinesAtOneStart_StaySung()
    {
        var lines = ExistingLyricsLoader.ParseTimed(
            "[00:01.00]<00:01.00>first <00:01.50>voice<00:02.00>\n" +
            "[00:01.00]<00:01.10>second <00:01.70>voice<00:02.20>\n" +
            "[00:03.00]v1:I sing\n[00:03.00]v2:you sing");

        Assert.Equal(4, lines.Count);
        Assert.All(lines, l => Assert.Null(l.Companions));
    }

    [Fact]
    public void Writers_PutCompanionsBackAtTheLineStart_AndTheLyricsPageMergesThem()
    {
        var lines = ExistingLyricsLoader.ParseTimed(IssueLrc);

        var lrc = TimedLyricsBuilder.BuildLrc(lines);
        Assert.Equal(IssueLrc, lrc);

        var timed = new[]
        {
            new AlignedLine("泣き じゃくる", S(5.54), S(7), new[] { new AlignedWord("泣き", S(5.54), S(6)), new AlignedWord("じゃくる", S(6), S(7)) }, 1, false)
            {
                Companions = lines[0].Companions,
            },
        };
        var elrc = TimedLyricsBuilder.BuildElrc(timed);
        Assert.Equal(
            "[00:05.54]<00:05.54>泣き <00:06.00>じゃくる<00:07.00>\n" +
            "[00:05.54]nakijakuru kodomo no you ni\n" +
            "[00:05.54]Como un niño que llora sin consuelo", elrc);
        var shown = Assert.Single(LrcParser.Parse(elrc));
        Assert.True(shown.HasWords);
        Assert.Equal(("nakijakuru kodomo no you ni", "Como un niño que llora sin consuelo"), (shown.Transliteration, shown.Translation));

        Assert.Equal(
            "泣きじゃくる子供のように\nnakijakuru kodomo no you ni\nComo un niño que llora sin consuelo\nnext",
            TimedLyricsBuilder.BuildPlain(lines));
    }

    [Fact]
    public void BuildTtml_WritesCompanionsAsRomanizationAndTranslationLayers()
    {
        var ttml = TimedLyricsBuilder.BuildTtml(ExistingLyricsLoader.ParseTimed(IssueLrc));

        var (parsed, _) = TtmlParser.Parse(ttml);
        Assert.Equal(2, parsed!.Count);
        Assert.Equal("泣きじゃくる子供のように", parsed[0].Text);
        Assert.Equal("nakijakuru kodomo no you ni", parsed[0].Transliteration);
        Assert.Equal("Como un niño que llora sin consuelo", parsed[0].Translation);
        Assert.Null(parsed[1].Translation);
    }

    [Fact]
    public void ReviewLine_KeepsCompanions_ThroughEditsAndExport()
    {
        var review = new Noctis.ViewModels.ReviewLine(ExistingLyricsLoader.ParseTimed(IssueLrc)[0]);
        review.Shift(S(1));

        var exported = review.ToAlignedLine();

        Assert.Equal(S(6.54), exported.Start);
        Assert.Equal(new[] { "nakijakuru kodomo no you ni", "Como un niño que llora sin consuelo" }, exported.Companions);
        Assert.StartsWith("[00:06.54]泣きじゃくる子供のように\n[00:06.54]nakijakuru", TimedLyricsBuilder.BuildLrc(new[] { exported }));
    }

    [Fact]
    public void CarryCompanions_ReattachesThemToReTimedLines_ByText()
    {
        var source = ExistingLyricsLoader.ParseTimed(IssueLrc);
        var retimed = new[]
        {
            new AlignedLine("泣きじゃくる子供のように", S(5.8), S(8), Array.Empty<AlignedWord>(), 0.9, false),
            new AlignedLine("next", S(9.1), S(10), Array.Empty<AlignedWord>(), 0.9, false),
        };

        var carried = ExistingLyricsLoader.CarryCompanions(source, retimed);

        Assert.Equal(S(5.8), carried[0].Start);
        Assert.Equal(source[0].Companions, carried[0].Companions);
        Assert.Null(carried[1].Companions);
        Assert.Same(retimed[1], carried[1]);
    }

    [Fact]
    public void Draft_RoundTripsCompanions()
    {
        var dir = Path.Combine(Path.GetTempPath(), "noctis-116-draft-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LyricsStudioDraftStore(dir);
            var id = Guid.NewGuid();
            var track = new Noctis.Models.Track { Id = id, Title = "t" };
            var result = new LyricsStudioResult(track, ExistingLyricsLoader.ParseTimed(IssueLrc), LyricsStudioSource.ExistingFile, 1, "", 0);
            store.Save(id, LyricsStudioDraft.From(result));

            Assert.True(store.TryLoad(id, out var draft));
            Assert.Equal(new[] { "nakijakuru kodomo no you ni", "Como un niño que llora sin consuelo" }, draft.Lines[0].Companions);
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }

    private static List<string> Texts(IReadOnlyList<AlignedLine> lines) =>
        lines.Select(l => l.Companions is { Count: > 0 } c ? l.Text + " | " + string.Join(" | ", c) : l.Text).ToList();
}
