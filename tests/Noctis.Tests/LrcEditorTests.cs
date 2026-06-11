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
}
