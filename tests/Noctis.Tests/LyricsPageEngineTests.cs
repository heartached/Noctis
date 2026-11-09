using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsPage;
using Xunit;

namespace Noctis.Tests;

public class LyricsPageEngineTests
{
    [Fact]
    public void A_long_gap_inside_the_window_gets_interlude_dots_and_voice_and_adlibs_carry_over()
    {
        var lines = new List<LyricsPageLine>
        {
            LyricsPageTestData.Words(10, "hold me closer"),
            LyricsPageTestData.Words(24, "tiny dancer", LyricVoice.Voice2) with
                { BackgroundWords = new[] { new KaraokeWord("ooh", 25, 26) } },
        };
        var built = LyricsPageEngine.BuildLines(lines);
        Assert.True(built[0].IsIntroPlaceholder && !built[0].IsInterlude);     // intro dots (first line > 2 s)
        Assert.True(built[2].IsInterlude);                                     // 11.5 → 24 s gap ≥ 6 s
        Assert.Equal(11.5 + 0.35, built[2].Timestamp!.Value.TotalSeconds, 3);  // sung end + InterludeDelay
        Assert.Equal(LyricVoice.Voice2, built[3].Voice);
        Assert.Equal("ooh", built[3].BackgroundWords![0].Text);
        Assert.Equal(new[] { "hold ", "me ", "closer" }, built[1].Words!.Select(w => w.Text));
    }

    [Fact]
    public void Plain_lrc_lines_get_no_interlude_and_unsynced_lines_are_dropped()
    {
        var built = LyricsPageEngine.BuildLines(new[] { LyricsPageTestData.Plain(1, "a"),
            new LyricsPageLine { Text = "x" }, LyricsPageTestData.Plain(20, "b") });
        Assert.Equal(new[] { "a", "b" }, built.Select(l => l.Text));
    }

    [Fact]
    public void Line_end_tags_without_words_bound_the_gap_for_interlude_dots()   // preflight F14
    {
        var built = LyricsPageEngine.BuildLines(new[]
        {
            LyricsPageTestData.Plain(1, "a") with { EndSeconds = 4 },
            LyricsPageTestData.Plain(20, "b"),
        });
        Assert.Equal(new[] { "a", "...", "b" }, built.Select(l => l.Text));
        Assert.True(built[1].IsInterlude);
        Assert.Equal(4 + 0.35, built[1].Timestamp!.Value.TotalSeconds, 3);
        Assert.Equal(4, built[0].EndTimestamp!.Value.TotalSeconds, 3);
    }

    [Fact]
    public void Background_only_lines_keep_their_flag_and_adlib_end()
    {
        var built = LyricsPageEngine.BuildLines(new[]
        {
            new LyricsPageLine
            {
                Text = "ooh yeah", StartSeconds = 1, IsBackgroundOnly = true, Voice = LyricVoice.Group,
                BackgroundWords = new[] { new KaraokeWord("ooh", 1, 2), new KaraokeWord("yeah", 2, 3) },
            },
        });
        var line = Assert.Single(built);
        Assert.True(line.IsBackgroundOnly);
        Assert.Equal(LyricVoice.Group, line.Voice);
        Assert.Null(line.Words);
        Assert.Equal(new[] { "ooh ", "yeah" }, line.BackgroundWords!.Select(w => w.Text));
        Assert.Equal(3, line.BackgroundEndTimestamp!.Value.TotalSeconds, 3);
    }
}
