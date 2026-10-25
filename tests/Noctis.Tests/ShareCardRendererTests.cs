using Noctis.Services;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

public class ShareCardRendererTests
{
    // ── Font-fallback run splitting (CJK tofu fix) ──────────────────────
    // Which fallback face gets matched depends on the OS's installed fonts, so these
    // assert only platform-independent properties: no characters lost, ASCII stays
    // on the primary face, runs are non-empty.

    [Fact]
    public void SplitFallbackRuns_AsciiIsSingleRunOnPrimaryFace()
    {
        using var face = SKTypeface.CreateDefault();
        var runs = ShareCardRenderer.SplitFallbackRuns("hello world", face);
        Assert.Single(runs);
        Assert.Equal("hello world", runs[0].Run);
        Assert.Same(face, runs[0].Face);
    }

    [Fact]
    public void SplitFallbackRuns_MixedScriptsLoseNoCharacters()
    {
        using var face = SKTypeface.CreateDefault();
        const string text = "잘 봐 One, two, three 가만히 바라봐";
        var runs = ShareCardRenderer.SplitFallbackRuns(text, face);
        Assert.Equal(text, string.Concat(runs.Select(r => r.Run)));
        Assert.All(runs, r => Assert.False(string.IsNullOrEmpty(r.Run)));
        Assert.All(runs, r => Assert.NotNull(r.Face));
    }

    [Fact]
    public void SplitFallbackRuns_EmptyTextYieldsNoRuns()
    {
        using var face = SKTypeface.CreateDefault();
        Assert.Empty(ShareCardRenderer.SplitFallbackRuns("", face));
    }

    // Fake measurer: every character is 10 units wide.
    private static float Measure(string s) => s.Length * 10f;

    [Fact]
    public void WrapText_ShortLine_StaysSingle()
    {
        var lines = ShareCardRenderer.WrapText("hello world", 200f, Measure);
        Assert.Single(lines);
        Assert.Equal("hello world", lines[0]);
    }

    [Fact]
    public void WrapText_BreaksAtWordBoundaries()
    {
        // maxWidth 120 = 12 chars per line
        var lines = ShareCardRenderer.WrapText("and when we're apart", 120f, Measure);
        Assert.Equal(new[] { "and when", "we're apart" }, lines);
    }

    [Fact]
    public void WrapText_HardBreaksOversizedWord()
    {
        var lines = ShareCardRenderer.WrapText("abcdefghij", 50f, Measure);
        Assert.Equal(new[] { "abcde", "fghij" }, lines);
    }

    /// <summary>
    /// Bug: the hard break of an oversized word went char by char (UTF-16), so it could cut
    /// an emoji's surrogate pair or split a Thai consonant from its vowel / tone marks
    /// (Thai has no spaces, so every long Thai line takes this path): the card drew a tofu
    /// box, or the mark on a dotted circle at the start of the next row.
    /// </summary>
    [Theory]
    [InlineData("\U0001F600\U0001F600\U0001F600", 30f)]   // three emoji
    [InlineData("ที่นี่", 20f)]   // Thai "here", two clusters
    public void WrapText_HardBreak_KeepsCharactersWhole(string word, float maxWidth)
    {
        var rows = ShareCardRenderer.WrapText(word, maxWidth, Measure);
        Assert.Equal(word, string.Concat(rows));
        Assert.True(rows.Count > 1);
        // Every cut falls on a text-element (grapheme) boundary.
        var boundaries = new HashSet<int>(System.Globalization.StringInfo.ParseCombiningCharacters(word));
        int at = 0;
        foreach (var row in rows.Take(rows.Count - 1))
            Assert.Contains(at += row.Length, boundaries);
    }

    /// <summary>Bug (same cause): Ellipsize cut the title/artist mid surrogate pair, drawing
    /// half an emoji (a tofu box) before the "…".</summary>
    [Fact]
    public void Ellipsize_DoesNotCutASurrogatePair()
    {
        var cut = ShareCardRenderer.Ellipsize("ab\U0001F600cd", 40f, Measure);
        Assert.EndsWith("…", cut);
        Assert.Equal(cut.Length, System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(cut)).Length);
        Assert.DoesNotContain('�', System.Text.Encoding.UTF8.GetString(System.Text.Encoding.UTF8.GetBytes(cut)));
    }

    [Fact]
    public void WrapText_EmptyInput_ReturnsNoLines()
    {
        Assert.Empty(ShareCardRenderer.WrapText("   ", 100f, Measure));
        Assert.Empty(ShareCardRenderer.WrapText("", 100f, Measure));
    }

    [Theory]
    [InlineData(0, 0, 0, false)]        // black bg -> white text
    [InlineData(255, 255, 255, true)]   // white bg -> dark text
    [InlineData(26, 26, 46, false)]     // app navy -> white text
    [InlineData(160, 160, 160, true)]   // light gray (like the Spotify gray card) -> dark text
    public void UseDarkText_PicksByLuminance(byte r, byte g, byte b, bool expectDark)
    {
        Assert.Equal(expectDark, ShareCardRenderer.UseDarkText(r, g, b));
    }

    [Fact]
    public void SanitizeForRender_FoldsExoticSpaces_ToPlainSpace()
    {
        // The culprits seen rendering as tofu boxes on the share card: non-breaking (U+00A0),
        // narrow no-break (U+202F), thin (U+2009) and ideographic (U+3000) spaces. Each folds
        // to a plain ASCII space so the single render font always has a glyph for it.
        var input = "the right ****, only lunch time　now";
        Assert.Equal("the right ****, only lunch time now",
            ShareCardRenderer.SanitizeForRender(input));
    }

    [Fact]
    public void SanitizeForRender_TurnsZeroWidthSeparatorsIntoSpaces_NotJoinedWords()
    {
        // Lyric providers join words with a zero-width / format char the render font can't
        // draw. It must become a real space ("leave her"), not vanish ("leaveher").
        Assert.Equal("leave her", ShareCardRenderer.SanitizeForRender("leave​her"));   // ZWSP
        Assert.Equal("keep her", ShareCardRenderer.SanitizeForRender("keep⁠her"));     // word joiner
        Assert.Equal("doesn't make", ShareCardRenderer.SanitizeForRender("doesn't﻿make")); // BOM/ZWNBSP
        Assert.Equal("mamacita, yeah", ShareCardRenderer.SanitizeForRender("mamacita,​yeah"));
    }

    [Fact]
    public void SanitizeForRender_CollapsesRuns_TrimsEnds_AndFoldsTab()
    {
        // A leading BOM, doubled separators, a tab and trailing whitespace all reduce to
        // single interior spaces with trimmed ends.
        Assert.Equal("a b c", ShareCardRenderer.SanitizeForRender("﻿a ​b\tc  "));
    }

    [Fact]
    public void SanitizeForRender_LeavesPlainTextUntouched()
    {
        var input = "I done did the right ****, only on the wrong night";
        Assert.Equal(input, ShareCardRenderer.SanitizeForRender(input));
    }

    [Fact]
    public void Ellipsize_KeepsFittingText()
    {
        Assert.Equal("short", ShareCardRenderer.Ellipsize("short", 100f, Measure));
    }

    [Fact]
    public void Ellipsize_TruncatesWithEllipsis()
    {
        var result = ShareCardRenderer.Ellipsize("a very long track title", 100f, Measure);
        Assert.EndsWith("…", result);
        Assert.True(Measure(result) <= 100f);
    }

    [Fact]
    public void RenderLyricCard_Square_ProducesPng()
    {
        var png = ShareCardRenderer.RenderLyricCardStyled(new LyricCardSpec
        {
            Title = "Those Eyes",
            Artist = "New West",
            ArtworkPath = null,
            Lines = new[] { "And when we're apart, and I'm missing you", "I close my eyes and all I see is you" },
            Format = ShareCardFormat.Square,
        });

        Assert.True(png.Length > 1000);
        // PNG magic bytes
        Assert.Equal(0x89, png[0]);
        Assert.Equal((byte)'P', png[1]);
        Assert.Equal((byte)'N', png[2]);
        Assert.Equal((byte)'G', png[3]);
    }

    [Fact]
    public void RenderLyricCard_Story_ProducesPng()
    {
        var png = ShareCardRenderer.RenderLyricCardStyled(new LyricCardSpec
        {
            Title = "Those Eyes",
            Artist = "New West",
            ArtworkPath = "Z:\\does\\not\\exist.jpg",
            Lines = new[] { "I close my eyes and all I see is you" },
            Format = ShareCardFormat.Story,
        });

        Assert.True(png.Length > 1000);
        Assert.Equal(0x89, png[0]);
    }
}
