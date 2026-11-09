using Noctis.Models;
using Noctis.Services;
using Noctis.Services.LyricsPage;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

public class LyricsPageLineLayoutTests
{
    private static readonly LyricsPageGeometry G = LyricsPageLayout.Measure(ShareCardFormat.Story, true);

    private static IReadOnlyList<PageLineLayout> Layout(IReadOnlyList<LyricLine> lines)
    {
        using var face = ShareCardRenderer.ResolveTypeface(SKFontStyleWeight.Bold);
        return LyricsPageLineLayout.Build(lines, G, face);
    }

    private static LyricLine Line(string text, LyricVoice voice = LyricVoice.Default) =>
        new() { Timestamp = TimeSpan.FromSeconds(1), Text = text, Voice = voice };

    private const string LongLine = "every single word of this line keeps rolling on and on forever and ever";

    [Fact]
    public void Rows_wrap_at_the_viewport_width_and_follow_the_voice()
    {
        var lines = new[]
        {
            Line(LongLine),
            Line(LongLine, LyricVoice.Voice2),
            Line("tiny dancer", LyricVoice.Group),
            Line("hold me closer"),
        };
        var layout = Layout(lines);
        var vp = G.Viewport;

        Assert.True(layout[0].Rows.Length >= 2);
        Assert.All(layout[0].Rows, r => Assert.Equal(vp.Left, r.X, 3));
        Assert.True(layout[1].Rows.Length >= 2);
        Assert.All(layout[1].Rows, r => Assert.Equal(vp.Right, r.X + r.Width, 3));
        var centred = Assert.Single(layout[2].Rows);
        Assert.True(Math.Abs((centred.X - vp.Left) - (vp.Right - (centred.X + centred.Width))) <= 0.5f);

        Assert.Equal(0f, layout[0].Top);
        for (int i = 0; i < layout.Count; i++)
        {
            Assert.All(layout[i].Rows, r => Assert.True(r.Width <= vp.Width + 0.5f));
            Assert.Equal(layout[i].Rows.Length * G.RowHeight, layout[i].Height, 3);
            Assert.False(layout[i].IsDots);
            if (i + 1 < layout.Count)
                Assert.Equal(layout[i].Top + layout[i].Height + G.LineGap, layout[i + 1].Top, 3);
        }
    }

    [Fact]
    public void Word_timed_rows_map_back_to_their_tokens_and_edited_text_degrades()
    {
        var built = LyricsPageEngine.BuildLines(LyricsPageTestData.Song());
        var layout = Layout(built);
        int wrapping = built.FindIndex(l => l.Timestamp == TimeSpan.FromSeconds(30));
        var words = built[wrapping].Words!.Select(w => w.Text.Trim()).ToArray();

        var rows = layout[wrapping].Rows;
        Assert.True(rows.Length >= 2);
        int next = 0;
        foreach (var row in rows)
        {
            Assert.NotNull(row.Range);
            Assert.Equal(next, row.Range!.Value.Start);
            Assert.Equal(row.Tokens.Length, row.Range.Value.Count);
            Assert.Equal(words.Skip(next).Take(row.Range.Value.Count), row.Tokens);
            Assert.Equal(row.Tokens.Length, row.TokenX.Length);
            Assert.Equal(row.Tokens.Length, row.TokenWidth.Length);
            next += row.Range.Value.Count;
        }
        Assert.Equal(words.Length, next);

        var edited = LyricsPageEngine.BuildLines(new[]
        {
            LyricsPageTestData.Words(1, "hold me closer") with { Text = "hold me tighter" },
        });
        var editedRows = Layout(edited)[0].Rows;
        Assert.NotEmpty(editedRows);
        Assert.All(editedRows, r => Assert.Null(r.Range));
    }

    [Fact]
    public void Dots_line_is_28_scaled_px_tall_and_adlib_row_adds_its_own_height()
    {
        var built = LyricsPageEngine.BuildLines(new List<LyricsPageLine>
        {
            LyricsPageTestData.Words(10, "hold me closer"),
            LyricsPageTestData.Words(24, "tiny dancer") with
                { BackgroundWords = new[] { new KaraokeWord("ooh", 25, 26), new KaraokeWord("yeah", 26, 27) } },
        });
        var layout = Layout(built);

        foreach (int dots in new[] { 0, 2 })   // intro dots, interlude dots
        {
            Assert.True(layout[dots].IsDots);
            Assert.Equal(28 * G.Scale, layout[dots].Height, 3);
            Assert.Empty(layout[dots].Rows);
            Assert.Empty(layout[dots].BackgroundRows);
        }

        var adlib = layout[3];
        Assert.NotEmpty(adlib.Rows);
        Assert.NotEmpty(adlib.BackgroundRows);
        float mainBottom = adlib.Rows.Length * G.RowHeight;
        Assert.Equal(mainBottom + 6 * G.Scale, adlib.BackgroundTop, 3);
        Assert.Equal(mainBottom + 6 * G.Scale + adlib.BackgroundRows.Length * 1.2f * G.BackgroundVocalSize,
            adlib.Height, 3);
        var bgRow = Assert.Single(adlib.BackgroundRows);
        Assert.Equal((0, 2), bgRow.Range);
        Assert.Equal(new[] { "ooh", "yeah" }, bgRow.Tokens);
        Assert.Equal(G.Viewport.Left, bgRow.X, 3);
    }

    [Fact]
    public void Cjk_and_overlong_lines_wrap_inside_the_column()   // Review Focus 2
    {
        var cjk = string.Concat(Enumerable.Repeat("夜空に光る星が見える", 4));   // 40 chars, no spaces
        var cjkLine = new LyricsPageLine
        {
            Text = cjk, StartSeconds = 1,
            Words = cjk.Select((c, i) => new KaraokeWord(c.ToString(), 1 + i * 0.2, 1.2 + i * 0.2)).ToList(),
        };
        var latin = string.Join(' ', Enumerable.Range(0, 30).Select(i => "word" + i));
        var built = LyricsPageEngine.BuildLines(new[] { cjkLine, LyricsPageTestData.Words(10, latin) });
        var layout = Layout(built);
        var vp = G.Viewport;
        Assert.Equal(2, layout.Count);   // no intro or interlude dots between the two

        foreach (var line in layout)
        {
            Assert.True(line.Rows.Length >= 2);
            Assert.All(line.Rows, r =>
            {
                Assert.True(r.Width <= vp.Width + 0.5f, $"row '{r.Text}' is {r.Width} px wide");
                Assert.True(r.X >= vp.Left - 0.5f);
            });
        }
        Assert.Equal(cjk, string.Concat(layout[0].Rows.Select(r => r.Text)));
        var cjkRows = layout[0].Rows;
        Assert.True(cjkRows.All(r => r.Range != null) || cjkRows.All(r => r.Range == null));
        Assert.All(layout[1].Rows, r => Assert.NotNull(r.Range));
    }

    [Fact]
    public void Background_only_line_draws_only_its_small_row()   // Review Focus 5
    {
        var built = LyricsPageEngine.BuildLines(new[]
        {
            new LyricsPageLine
            {
                Text = "ooh yeah", StartSeconds = 1, IsBackgroundOnly = true,
                BackgroundWords = new[] { new KaraokeWord("ooh", 1, 2), new KaraokeWord("yeah", 2, 3) },
            },
        });
        var line = Assert.Single(Layout(built));

        Assert.Empty(line.Rows);
        Assert.NotEmpty(line.BackgroundRows);
        Assert.Equal(0f, line.BackgroundTop);   // preflight F25: no leading gap without main rows
        Assert.Equal(line.BackgroundRows.Length * 1.2f * G.BackgroundVocalSize, line.Height, 3);
    }
}
