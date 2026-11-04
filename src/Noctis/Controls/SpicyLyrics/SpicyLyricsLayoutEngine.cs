// Faithful port of LineWrapper.kt and CjkDetector.kt from spicylyrics/mobile (AGPL-3.0)

using System;
using System.Collections.Generic;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Noctis.Models;

namespace Noctis.Controls.SpicyLyrics;

/// <summary>
/// Detects Chinese, Japanese, and Korean characters for typography and line-wrapping.
/// Port of CjkDetector.kt.
/// </summary>
public static class SpicyLyricsCjkDetector
{
    public static bool IsCjk(char c)
    {
        // CJK Unified Ideographs & Extensions
        if (c >= 0x4E00 && c <= 0x9FFF) return true;
        if (c >= 0x3400 && c <= 0x4DBF) return true;
        if (c >= 0xF900 && c <= 0xFAFF) return true;
        // Hiragana & Katakana
        if (c >= 0x3040 && c <= 0x309F) return true;
        if (c >= 0x30A0 && c <= 0x30FF) return true;
        // Hangul (Syllables, Jamo, Compatibility)
        if (c >= 0xAC00 && c <= 0xD7AF) return true;
        if (c >= 0x1100 && c <= 0x11FF) return true;
        if (c >= 0x3130 && c <= 0x318F) return true;

        return false;
    }
}

/// <summary>
/// Faithful port of LineWrapper.kt and LyricsLayoutCalculator.kt from spicylyrics/mobile.
/// Determines where a lyric line wraps to match browser layout fidelity.
/// </summary>
public sealed class SpicyLyricsLayoutEngine
{
    // Closing punctuation a line of text never starts with
    private const string NoBreakBefore =
        "\u3001\u3002\uff0c\uff0e\u30fb\uff1a\uff1b\uff1f\uff01\u309b\u309c\u30fd\u30fe\u309d\u309e\u3005\uff09\uff3d\uff5d\u300d\u300f\u3011\u3015\u3009\u300b\u3019\u3017\u301f\u2019\u201d\uff60\u00bb,.!?)]}\u2026\u2025";

    // Opening brackets a line never ends with
    private const string NoBreakAfter =
        "\uff08\uff3b\uff5b\u300c\u300e\u3010\u3014\u3008\u300a\u3018\u3016\u301d\u2018\u201c\uff5f\u00ab([{";

    public sealed class WordItem : IDisposable
    {
        public required WordTiming Timing { get; init; }
        public required TextLayout Layout { get; init; }
        public required string Text { get; init; }
        public bool GluedToPrevious { get; init; }
        public double Width => Layout.WidthIncludingTrailingWhitespace;
        public double Height => Layout.Height;

        public void Dispose() => Layout.Dispose();
    }

    public sealed class Row : IDisposable
    {
        public List<WordItem> Words { get; } = new();
        public double Width { get; set; }
        public double Height { get; set; }

        public void Dispose()
        {
            foreach (var word in Words)
                word.Dispose();
        }
    }

    /// <summary>
    /// Port of LineWrapper.breaks(...)
    /// </summary>
    public static List<int> CalculateBreaks(
        IReadOnlyList<WordItem> pieces,
        double maxWidth,
        double wordGap,
        double trailingGap,
        bool textMode)
    {
        var n = pieces.Count;
        var lineBreaks = new List<int> { 0 };
        if (n == 0)
        {
            lineBreaks.Add(0);
            return lineBreaks;
        }

        // Whether each piece's glued run (word group) fits a row by itself
        var runFits = new bool[n];
        var runStart = 0;
        var runWidth = 0.0;
        for (var idx = 0; idx < n; idx++)
        {
            if (idx > 0 && !pieces[idx].GluedToPrevious)
            {
                var fits = runWidth + trailingGap <= maxWidth;
                for (var k = runStart; k < idx; k++)
                    runFits[k] = fits;
                runStart = idx;
                runWidth = 0;
            }
            runWidth += pieces[idx].Width;
        }
        for (var k = runStart; k < n; k++)
            runFits[k] = runWidth <= maxWidth;

        bool EndsRun(int idx) => idx == n - 1 || !pieces[idx + 1].GluedToPrevious;

        bool PunctuationAllows(int idx)
        {
            var prevText = pieces[idx - 1].Text;
            var curText = pieces[idx].Text;
            if (string.IsNullOrEmpty(prevText) || string.IsNullOrEmpty(curText))
                return true;

            var prev = prevText[^1];
            var cur = curText[0];
            return NoBreakBefore.IndexOf(cur) < 0 && NoBreakAfter.IndexOf(prev) < 0;
        }

        bool TextBreakBefore(int idx)
        {
            var piece = pieces[idx];
            if (!piece.GluedToPrevious) return true;
            if (!PunctuationAllows(idx)) return false;

            var prevText = pieces[idx - 1].Text;
            var curText = piece.Text;
            if (string.IsNullOrEmpty(prevText) || string.IsNullOrEmpty(curText))
                return true;

            var prev = prevText[^1];
            var cur = curText[0];
            return SpicyLyricsCjkDetector.IsCjk(prev) || SpicyLyricsCjkDetector.IsCjk(cur) || !runFits[idx];
        }

        bool CanBreakBefore(int idx)
        {
            if (idx == 0) return true;
            if (textMode) return TextBreakBefore(idx);
            if (!pieces[idx].GluedToPrevious) return true;
            return !runFits[idx] && PunctuationAllows(idx);
        }

        // Greedy row filling
        var rowWidth = 0.0;
        var lastBreakCandidate = 0;
        var i = 0;
        while (i < n)
        {
            var piece = pieces[i];
            var gap = (rowWidth > 0 && !piece.GluedToPrevious) ? wordGap : 0.0;
            if (CanBreakBefore(i))
                lastBreakCandidate = i;
            var trailing = (EndsRun(i) && i < n - 1) ? trailingGap : 0.0;

            if (rowWidth + gap + piece.Width + trailing > maxWidth && rowWidth > 0.0)
            {
                var breakIdx = lastBreakCandidate > lineBreaks[^1] ? lastBreakCandidate : i;
                lineBreaks.Add(breakIdx);
                i = breakIdx;
                rowWidth = 0.0;
            }
            else
            {
                rowWidth += gap + piece.Width;
                i++;
            }
        }

        // Prevent isolated broken word piece left alone on the last row
        if (lineBreaks.Count >= 2)
        {
            var lastStart = lineBreaks[^1];
            var prevStart = lineBreaks[^2];
            var moved = lastStart - 1;
            if (n - lastStart == 1 && pieces[lastStart].GluedToPrevious && moved - prevStart >= 2 && CanBreakBefore(moved))
            {
                var width = 0.0;
                for (var k = moved; k < n; k++)
                    width += pieces[k].Width + (k > moved && !pieces[k].GluedToPrevious ? wordGap : 0.0);
                if (width <= maxWidth)
                    lineBreaks[^1] = moved;
            }
        }

        lineBreaks.Add(n);
        return lineBreaks;
    }

    /// <summary>
    /// Builds layout rows using exact LineWrapper algorithm.
    /// </summary>
    public List<Row> BuildRows(
        IReadOnlyList<WordTiming>? words,
        Typeface typeface,
        double fontSize,
        IBrush measureBrush,
        double availableWidth)
    {
        var rows = new List<Row>();
        if (words is null || words.Count == 0)
            return rows;

        availableWidth = Math.Max(10, availableWidth);

        // 1. Measure all pieces
        var items = new List<WordItem>(words.Count);
        for (var i = 0; i < words.Count; i++)
        {
            var timing = words[i];
            var text = timing.DisplayText ?? string.Empty;
            var layout = new TextLayout(text, typeface, fontSize, measureBrush);

            var glued = false;
            if (i > 0)
            {
                var prevText = words[i - 1].DisplayText ?? string.Empty;
                // If previous word did NOT end with space and current doesn't start with space,
                // they are glued syllables or continuous CJK.
                if (!prevText.EndsWith(' ') && !text.StartsWith(' '))
                {
                    glued = true;
                }
            }

            items.Add(new WordItem
            {
                Timing = timing,
                Layout = layout,
                Text = text,
                GluedToPrevious = glued
            });
        }

        // 2. Compute break points
        var breaks = CalculateBreaks(items, availableWidth, wordGap: 0, trailingGap: 0, textMode: false);

        // 3. Assemble rows
        for (var b = 0; b < breaks.Count - 1; b++)
        {
            var start = breaks[b];
            var end = breaks[b + 1];
            if (start >= end) continue;

            var row = new Row();
            for (var k = start; k < end; k++)
            {
                var item = items[k];
                row.Words.Add(item);
                row.Width += item.Width;
                row.Height = Math.Max(row.Height, item.Height);
            }
            rows.Add(row);
        }

        return rows;
    }
}
