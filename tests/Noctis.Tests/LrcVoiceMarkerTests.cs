using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// End-to-end LRC parsing of duet voice markers (Gramophone/iTunes "v1:"/"v2:"/"v3:")
/// through LyricsViewModel.ParseLrcContent: the marker is stripped from display text,
/// the voice lands on the line, and word timing survives a marker before the first tag.
/// </summary>
public class LrcVoiceMarkerTests
{
    [Fact]
    public void Voice2LineWithWordTiming_ParsesWordsAndVoice()
    {
        var lines = LyricsViewModel.ParseLrcContent("[00:12.34]v2: <00:12.50>word <00:13.00>two<00:13.40>");

        var line = Assert.Single(lines);
        Assert.Equal(LyricVoice.Voice2, line.Voice);
        Assert.Equal("word two", line.Text);
        Assert.NotNull(line.Words);
        Assert.Equal(2, line.Words!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(12_500), line.Words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(13_400), line.Words[1].End);
    }

    [Fact]
    public void V1Marker_StrippedFromText_MapsToDefault()
    {
        var lines = LyricsViewModel.ParseLrcContent("[00:10.00]v1: Hello there");

        var line = Assert.Single(lines);
        Assert.Equal(LyricVoice.Default, line.Voice);
        Assert.Equal("Hello there", line.Text);
    }

    [Fact]
    public void V3Marker_MapsToGroup()
    {
        var lines = LyricsViewModel.ParseLrcContent("[00:10.00]v3: Both of us");

        var line = Assert.Single(lines);
        Assert.Equal(LyricVoice.Group, line.Voice);
        Assert.Equal("Both of us", line.Text);
    }

    [Fact]
    public void SingleLeadingSpace_Accepted()
    {
        var lines = LyricsViewModel.ParseLrcContent("[00:10.00] v2: Hey now");

        var line = Assert.Single(lines);
        Assert.Equal(LyricVoice.Voice2, line.Voice);
        Assert.Equal("Hey now", line.Text);
    }

    [Fact]
    public void UppercaseAndDoubleSpaceVariants_StayLiteralText()
    {
        // Gramophone parity: markers are lowercase with at most one leading space;
        // anything else is lyric text and must surface verbatim.
        var upper = Assert.Single(LyricsViewModel.ParseLrcContent("[00:10.00]V2: Hey"));
        Assert.Equal(LyricVoice.Default, upper.Voice);
        Assert.Equal("V2: Hey", upper.Text);

        var spaced = Assert.Single(LyricsViewModel.ParseLrcContent("[00:10.00]  v2: Hey"));
        Assert.Equal(LyricVoice.Default, spaced.Voice);
        Assert.Equal("v2: Hey", spaced.Text);
    }

    [Fact]
    public void NoMarker_RegressionUnchanged()
    {
        var lines = LyricsViewModel.ParseLrcContent("[00:12.34]Hello world");

        var line = Assert.Single(lines);
        Assert.Equal(LyricVoice.Default, line.Voice);
        Assert.Equal("Hello world", line.Text);
        Assert.Null(line.Words);
    }

    [Fact]
    public void MultiTimestampLine_EveryOccurrenceCarriesVoice()
    {
        var lines = LyricsViewModel.ParseLrcContent("[00:05.00][00:35.00]v2: Chorus");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l =>
        {
            Assert.Equal(LyricVoice.Voice2, l.Voice);
            Assert.Equal("Chorus", l.Text);
        });
    }

    [Fact]
    public void ParenAdlibAfterVoicedLine_FoldsIntoVoicedLine()
    {
        var lines = LyricsViewModel.ParseLrcContent(
            "[00:10.00]v2: <00:10.00>lead <00:10.80>line\n" +
            "[00:11.00]<00:11.00>(ooh)<00:11.90>");

        var line = Assert.Single(lines);
        Assert.Equal(LyricVoice.Voice2, line.Voice);
        Assert.True(line.HasBackgroundWords);
        Assert.Equal("lead line", line.Text);
    }

    [Fact]
    public void StripTimestamps_RemovesVoiceMarkersFromTimestampedLines()
    {
        // Plain derivations (share card, plain save, unsync tab) must not show markers.
        var plain = LyricsTextHelper.StripTimestamps(
            "[00:10.00]v1: First\n[00:12.00]v2: <00:12.00>Second\n[00:14.00]Third");

        Assert.Equal("First" + Environment.NewLine + "Second" + Environment.NewLine + "Third", plain);
    }

    [Fact]
    public void StripTimestamps_LeavesMarkerLookalikesOnPlainLinesAlone()
    {
        // Without a timestamp there is no sync point, so "v2:" is ordinary text.
        var plain = LyricsTextHelper.StripTimestamps("v2: not a marker\n[00:10.00]Real line");

        Assert.Equal("v2: not a marker" + Environment.NewLine + "Real line", plain);
    }
}
