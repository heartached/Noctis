using Noctis.Helpers;
using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// LRC files whose stamps never roll the seconds into minutes ("[00:168.80]" for 2:48.80):
/// 121 of the library's .lrc files were written that way, 4,349 of their lines with three-digit
/// seconds. Every reader dropped those lines (the stamp patterns took exactly two second
/// digits), so the songs showed lyrics up to 1:40 and nothing after, and a Studio save wrote
/// them without those lines. They are read as seconds; the writers keep writing mm:ss.xx.
/// </summary>
public class LrcSecondsPastAMinuteTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    private const string Song =
        "[ti:Song]\n" +
        "[00:59.50]Before the minute\n" +
        "[00:75.20]Past the minute, two digits\n" +
        "[00:168.80]Three digits\n" +
        "[00:201.05]<00:201.05>Timed <00:201.60>words<00:202.10>\n";

    [Fact]
    public void LyricsPage_ReadsEveryLine_AtItsTime()
    {
        var lines = LrcParser.Parse(Song).Where(l => !l.IsIntroPlaceholder).ToList();

        Assert.Equal(new TimeSpan?[] { S(59.5), S(75.2), S(168.8), S(201.05) }, lines.Select(l => l.Timestamp));
        Assert.Equal("Three digits", lines[2].Text);
        Assert.Equal(new[] { S(201.05), S(201.6) }, lines[3].Words!.Select(w => w.Start));
        Assert.Equal(S(202.1), lines[3].EndTimestamp);
    }

    [Fact]
    public void WordTags_WithThreeDigitSeconds_AreWords()
    {
        var (text, words) = EnhancedLrcParser.ParseLine("<00:168.80>Hello <00:169.20>world<00:170.00>");

        Assert.Equal("Hello world", text);
        Assert.Equal(new[] { S(168.8), S(169.2) }, words!.Select(w => w.Start));
        Assert.Equal(S(170), words![^1].End);
        Assert.True(EnhancedLrcParser.ContainsWordTags("<00:168.80>Hello"));
        Assert.Equal("Hello world", EnhancedLrcParser.StripWordTags("<00:168.80>Hello <00:169.20>world<00:170.00>"));
    }

    [Fact]
    public void Studio_LoadsEveryLine_AndSavesThemAsMinutesAndSeconds()
    {
        var lines = ExistingLyricsLoader.ParseTimed(Song);

        Assert.Equal(new[] { S(59.5), S(75.2), S(168.8), S(201.05) }, lines.Select(l => l.Start));
        Assert.Equal(new[] { S(201.05), S(201.6) }, lines[3].Words.Select(w => w.Start));
        Assert.Equal(
            "[00:59.50]Before the minute\n[01:15.20]Past the minute, two digits\n[02:48.80]Three digits\n" +
            "[03:21.05]<03:21.05>Timed <03:21.60>words<03:22.10>",
            TimedLyricsBuilder.BuildElrc(lines));
        // The text the Studio times (lines of a song's own synced lyrics) keeps them too.
        Assert.Equal(new[] { "Before the minute", "Past the minute, two digits", "Three digits", "Timed words" },
            LyricsStudioEngine.FirstText(Song));
    }

    [Fact]
    public void FormatDetection_AndTextHelpers_SeeTheStamps()
    {
        Assert.Equal(LyricsFormat.Lrc, LyricsFormatDetector.Detect(null, "[00:168.80]Three digits"));
        Assert.Equal(LyricsFormat.Elrc, LyricsFormatDetector.Detect(null, "[00:201.05]<00:201.05>Timed <00:201.60>words<00:202.10>"));
        Assert.True(LyricsTextHelper.ContainsTimestamps("[00:168.80]Three digits"));
        Assert.Equal("Three digits", LyricsTextHelper.StripTimestamps("[00:168.80]Three digits"));
        Assert.Equal("Timed words", LyricsTextHelper.StripTimestamps("[00:201.05]<00:201.05>Timed <00:201.60>words<00:202.10>"));
        // A shift re-emits the tags as mm:ss.xx.
        Assert.Equal("[02:49.80]<02:49.80>Three <02:50.30>digits", LyricsTextHelper.ShiftAllTimestamps("[00:168.80]<00:168.80>Three <00:169.30>digits", TimeSpan.FromSeconds(1)));
        Assert.Equal("[00:168.80]<02:49.80>Three", LyricsTextHelper.ShiftWordTimestamps("[00:168.80]<00:168.80>Three", TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void LrcEditor_KeepsTheLines_AndWritesMinutesAndSeconds()
    {
        var rows = LrcEditorViewModel.ParseLrc("[00:168.80]Three digits\n[00:201.05]<00:201.05>Timed <00:201.60>words<00:202.10>");

        Assert.Equal(new TimeSpan?[] { S(168.8), S(201.05) }, rows.Select(r => r.Time));
        Assert.Equal("[02:48.80]Three digits", LrcEditorViewModel.BuildLrc(new[] { (rows[0].Time!.Value, rows[0].Text) }).TrimEnd());
        var row = new SyncedLyricEditorLine(rows[1].Time, rows[1].Text);
        Assert.True(row.HasWordTiming);
        Assert.Equal("Timed words", row.DisplayText);
    }

    [Theory]
    [InlineData("[00:5.00]One second digit")]
    [InlineData("[00:1688.00]Four second digits")]
    [InlineData("[00:16a.00]Not a number")]
    public void MalformedStamps_StayText(string line)
    {
        Assert.False(LyricsTextHelper.ContainsTimestamps(line));
        Assert.Empty(ExistingLyricsLoader.ParseTimed(line));
        Assert.DoesNotContain(LrcParser.Parse(line), l => l.Timestamp.HasValue);
    }
}
