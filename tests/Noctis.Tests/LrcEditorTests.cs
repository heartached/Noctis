using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class LrcEditorTests
{
    [Fact]
    public void ParseLrc_ReadsTimestampedLines()
    {
        var lines = LrcEditorViewModel.ParseLrc("[00:12.34]Hello world\n[01:02.5]Second line");

        Assert.Equal(2, lines.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(12_340), lines[0].Time);
        Assert.Equal("Hello world", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(62_500), lines[1].Time);
    }

    [Fact]
    public void ParseLrc_PlainLines_GetNullTimestamps()
    {
        var lines = LrcEditorViewModel.ParseLrc("First line\nSecond line\n\n");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Null(l.Time));
    }

    [Fact]
    public void ParseLrc_SkipsMetadataTags_AndStacksOfTimestamps()
    {
        var lines = LrcEditorViewModel.ParseLrc("[ar:Artist]\n[ti:Title]\n[00:05.00][00:35.00]Chorus");

        Assert.Single(lines);
        Assert.Equal(TimeSpan.FromSeconds(5), lines[0].Time);
        Assert.Equal("Chorus", lines[0].Text);
    }

    [Fact]
    public void BuildLrc_OrdersByTime_AndFormats()
    {
        var lrc = LrcEditorViewModel.BuildLrc(new[]
        {
            (TimeSpan.FromSeconds(65.5), "Later"),
            (TimeSpan.FromSeconds(5.25), "Earlier"),
        });

        var lines = lrc.Trim().Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal("[00:05.25]Earlier", lines[0]);
        Assert.Equal("[01:05.50]Later", lines[1]);
    }

    [Fact]
    public void ParseThenBuild_RoundTrips()
    {
        const string original = "[00:10.00]Line one\n[00:20.50]Line two\n";
        var parsed = LrcEditorViewModel.ParseLrc(original)
            .Select(l => (l.Time!.Value, l.Text));

        var rebuilt = LrcEditorViewModel.BuildLrc(parsed).Replace("\r\n", "\n");

        Assert.Equal(original, rebuilt);
    }

    [Fact]
    public void FormatTimestamp_PadsCorrectly()
    {
        Assert.Equal("00:05.07", LrcEditorViewModel.FormatTimestamp(TimeSpan.FromMilliseconds(5_070)));
        Assert.Equal("12:34.99", LrcEditorViewModel.FormatTimestamp(new TimeSpan(0, 0, 12, 34, 990)));
    }

    [Fact]
    public void BuildLrcPreservingUntimed_KeepsLinesTheUserHasNotStampedYet()
    {
        // Saving a half-finished sync used to write only the stamped lines over the
        // existing sidecar, so the rest of the song vanished from the file.
        var lines = new List<(TimeSpan?, string)>
        {
            (TimeSpan.FromSeconds(20), "second"),
            (null, "not stamped yet"),
            (TimeSpan.FromSeconds(10), "first"),
            (null, "also not stamped"),
        };

        var lrc = LrcEditorViewModel.BuildLrcPreservingUntimed(lines).Replace("\r\n", "\n");

        Assert.Equal(
            "[00:10.00]first\n[00:20.00]second\nnot stamped yet\nalso not stamped\n",
            lrc);
    }

    [Fact]
    public void BuildLrcPreservingUntimed_RoundTripsThroughParseLrc()
    {
        var original = "[00:01.00]a\n[00:02.00]b\nuntimed\n";

        var reparsed = LrcEditorViewModel.ParseLrc(original);
        var rebuilt = LrcEditorViewModel.BuildLrcPreservingUntimed(reparsed).Replace("\r\n", "\n");

        Assert.Equal(original, rebuilt);
    }

    // ── Duet voice markers ("v1:"/"v2:") round-trip and display ───────────────

    [Fact]
    public void ParseLrc_KeepsVoiceMarkerInRawText()
    {
        var lines = LrcEditorViewModel.ParseLrc("[00:10.00]v2: Hey");

        Assert.Single(lines);
        Assert.Equal("v2: Hey", lines[0].Text);
    }

    [Fact]
    public void VoiceMarkers_RoundTripThroughParseAndBuild()
    {
        var original = "[00:10.00]v1: First voice\n[00:12.00]v2: Second voice\n";

        var reparsed = LrcEditorViewModel.ParseLrc(original);
        var rebuilt = LrcEditorViewModel.BuildLrcPreservingUntimed(reparsed).Replace("\r\n", "\n");

        Assert.Equal(original, rebuilt);
    }

    [Fact]
    public void LrcEditorLine_DisplayText_StripsVoiceMarkerAndWordTags()
    {
        var line = new LrcEditorLine(null, "v2: <00:10.00>Hey <00:10.50>now");

        Assert.Equal("v2: <00:10.00>Hey <00:10.50>now", line.Text);
        Assert.Equal("Hey now", line.DisplayText);
    }

    [Fact]
    public void SyncedLyricEditorLine_DisplayText_StripsVoiceMarker_FlagTracksWordTagsOnly()
    {
        var timed = new SyncedLyricEditorLine(null, "v2: <00:10.00>Hey");
        Assert.Equal("v2: <00:10.00>Hey", timed.Text);
        Assert.Equal("Hey", timed.DisplayText);
        Assert.True(timed.HasWordTiming);

        // A marker alone must not read as word timing.
        var plain = new SyncedLyricEditorLine(null, "v2: Hey");
        Assert.Equal("Hey", plain.DisplayText);
        Assert.False(plain.HasWordTiming);
    }
}
