using Noctis.Models;
using SkiaSharp;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// One wrapped row of a Lyrics Page line, measured once: its left edge on the canvas (aligned
/// by the line's voice), its width, which of the line's words it shows (null Range = no word
/// mapping, the row lights as a line), and each token's start (from the row's left edge) and
/// glyph width.
/// </summary>
internal sealed record PageRow(string Text, float X, float Width, (int Start, int Count)? Range,
    string[] Tokens, float[] TokenX, float[] TokenWidth);

/// <summary>
/// A line's place in the scrolling list. <see cref="Top"/> is from the top of the list (line 0
/// at 0); <see cref="BackgroundTop"/> is from the line's own top. Main rows are
/// <see cref="LyricsPageGeometry.RowHeight"/> tall, adlib rows 1.2 × the adlib font. Dots lines
/// have no rows.
/// </summary>
internal sealed record PageLineLayout(float Top, float Height, PageRow[] Rows, float BackgroundTop,
    PageRow[] BackgroundRows, bool IsDots);

internal static class LyricsPageLineLayout
{
    /// <summary>The page's dots row: 12 px dots with 8 px margin above and below, at a 46 px font.</summary>
    private const float DotsHeight = 28f;

    /// <summary>Gap between a line's main rows and its adlib row, at a 46 px font.</summary>
    private const float BackgroundGap = 6f;

    internal static IReadOnlyList<PageLineLayout> Build(IReadOnlyList<LyricLine> lines, LyricsPageGeometry g, SKTypeface face)
    {
        using var mainPaint = ShareCardRenderer.TextPaint(face, g.LyricSize, SKColors.White);
        using var bgPaint = ShareCardRenderer.TextPaint(face, g.BackgroundVocalSize, SKColors.White);
        float bgRowHeight = g.BackgroundVocalSize * 1.2f;

        var result = new PageLineLayout[lines.Count];
        float top = 0;
        for (int i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            PageLineLayout layout;
            if (line.IsIntroPlaceholder)
            {
                layout = new PageLineLayout(top, DotsHeight * g.Scale, [], 0, [], IsDots: true);
            }
            else
            {
                var rows = line.IsBackgroundOnly ? [] : Rows(line.Text, line.Words, mainPaint, line.Voice, g.Viewport);
                var bgRows = line.BackgroundWords is { Count: > 0 } bg
                    ? Rows(string.Join(' ', bg.Select(w => ShareCardRenderer.SanitizeForRender(w.Text))),
                        bg, bgPaint, line.Voice, g.Viewport)
                    : [];
                float mainHeight = rows.Length * g.RowHeight;
                // No leading gap when there are no main rows (a background-only line).
                float bgTop = rows.Length > 0 ? mainHeight + BackgroundGap * g.Scale : 0;
                float height = bgRows.Length > 0 ? bgTop + bgRows.Length * bgRowHeight : mainHeight;
                layout = new PageLineLayout(top, height, rows, bgTop, bgRows, IsDots: false);
            }
            result[i] = layout;
            top += layout.Height + g.LineGap;
        }
        return result;
    }

    /// <summary>Row left edge for the line's voice: Default left, Voice2 right, Group centred.</summary>
    internal static float AlignX(LyricVoice voice, float rowWidth, SKRect viewport) => voice switch
    {
        LyricVoice.Voice2 => viewport.Right - rowWidth,
        LyricVoice.Group => viewport.Left + (viewport.Width - rowWidth) / 2f,
        _ => viewport.Left,
    };

    /// <summary>Wraps <paramref name="text"/> at the viewport width and maps the rows back to
    /// <paramref name="words"/>; rows that don't re-assemble the words (edited text, hard-broken
    /// words) all get a null range.</summary>
    private static PageRow[] Rows(string text, IReadOnlyList<WordTiming>? words, SKPaint paint,
        LyricVoice voice, SKRect viewport)
    {
        var wrapped = ShareCardRenderer.WrapText(ShareCardRenderer.SanitizeForRender(text), viewport.Width,
            s => ShareCardRenderer.MeasureTextFallback(paint, s));
        var ranges = words is { Count: > 0 }
            ? KaraokeSweep.MapRowsToTokenRanges(words.Select(w => ShareCardRenderer.SanitizeForRender(w.Text)).ToList(), wrapped)
            : null;
        var rows = new PageRow[wrapped.Count];
        for (int r = 0; r < rows.Length; r++)
        {
            var rowText = wrapped[r];
            var (tokens, xs, ws) = ShareCardRenderer.MeasureRowTokens(paint, rowText);
            float width = ShareCardRenderer.MeasureTextFallback(paint, rowText);
            rows[r] = new PageRow(rowText, AlignX(voice, width, viewport), width, ranges?[r], tokens, xs, ws);
        }
        return rows;
    }
}
