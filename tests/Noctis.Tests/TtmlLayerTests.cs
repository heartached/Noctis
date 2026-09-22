using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// TTML translation (x-translation) and romanization (x-roman) layers — issue #61's
/// "Japanese word-synced line with the English translation underneath, same timing".
/// </summary>
public class TtmlLayerTests
{
    // The issue author's own example line, inline-role form.
    private const string InlineDoc = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xml:lang="ja">
          <body><div>
            <p begin="1.000" end="4.000" itunes:key="L1"><span begin="1.000" end="1.600">例えば</span><span begin="1.600" end="2.200">俺が</span><span begin="2.200" end="2.800">俺じゃ</span><span begin="2.800" end="3.300">ない</span><span begin="3.300" end="4.000">として</span><span ttm:role="x-translation" xml:lang="en">For example, if I were not myself</span><span ttm:role="x-roman" xml:lang="ja-Latn">tatoeba ore ga ore ja nai to shite</span></p>
          </div></body>
        </tt>
        """;

    // Apple Music head form: layers keyed by itunes:key; two translation languages.
    private const string HeadDoc = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal" xml:lang="ja">
          <head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal">
            <translations>
              <translation type="subtitle" xml:lang="en"><text for="L1">For example, if I were not myself</text></translation>
              <translation type="subtitle" xml:lang="fr"><text for="L1">Par exemple</text></translation>
            </translations>
            <transliterations>
              <transliteration xml:lang="ja-Latn"><text for="L1"><span begin="1.0" end="1.6">tatoeba </span><span begin="1.6" end="2.2">ore ga</span></text></transliteration>
            </transliterations>
          </iTunesMetadata></metadata></head>
          <body><div>
            <p begin="1.0" end="4.0" itunes:key="L1"><span begin="1.0" end="2.0">例えば</span><span begin="2.0" end="4.0">俺が</span></p>
            <p begin="5.0" end="6.0" itunes:key="L2"><span begin="5.0" end="6.0">次</span></p>
          </div></body>
        </tt>
        """;

    [Fact]
    public void InlineLayers_AttachToTheLine_AndWordTimingIsPreservedExactly()
    {
        var (lines, plain) = TtmlParser.Parse(InlineDoc);

        var line = Assert.Single(lines!);
        Assert.Equal("例えば俺が俺じゃないとして", line.Text);
        Assert.Equal("例えば俺が俺じゃないとして", plain);
        Assert.Equal("For example, if I were not myself", line.Translation);
        Assert.Equal("tatoeba ore ga ore ja nai to shite", line.Romanization);
        Assert.True(line.HasTranslation);
        Assert.True(line.HasRomanization);

        var words = line.Words!;
        Assert.Equal(new[] { "例えば", "俺が", "俺じゃ", "ない", "として" }, words.Select(w => w.Text));
        Assert.Equal(TimeSpan.FromSeconds(1.0), words[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.2), words[2].Start);
        Assert.Equal(TimeSpan.FromSeconds(2.8), words[2].End);
        Assert.Equal(TimeSpan.FromSeconds(4.0), words[4].End);
    }

    [Fact]
    public void TranslationSpan_NeverLeaksIntoWordsOrText()
    {
        // Regression: the untimed-wrapper recursion used to append the English to the line
        // text and glue it onto the last Japanese word's tail.
        var line = TtmlParser.Parse(InlineDoc).Lines![0];
        Assert.DoesNotContain("For example", line.Text);
        Assert.DoesNotContain(line.Words!, w => w.Text.Contains("For") || w.Text.Contains("tatoeba"));
    }

    [Fact]
    public void HeadLayers_MatchByItunesKey_FirstLanguageOnly()
    {
        var lines = TtmlParser.Parse(HeadDoc).Lines!;

        Assert.Equal("For example, if I were not myself", lines[0].Translation);
        Assert.Equal("tatoeba ore ga", lines[0].Romanization);
        Assert.Null(lines[1].Translation);
        Assert.False(lines[1].HasTranslation);
    }

    [Fact]
    public void InlineTranslation_WinsOverHeadTranslation()
    {
        var both = HeadDoc.Replace(
            """<span begin="2.0" end="4.0">俺が</span></p>""",
            """<span begin="2.0" end="4.0">俺が</span><span xmlns:ttm="http://www.w3.org/ns/ttml#metadata" ttm:role="x-translation" xml:lang="en">Inline wins</span></p>""");

        var line = TtmlParser.Parse(both).Lines![0];
        Assert.Equal("Inline wins", line.Translation);
    }

    [Fact]
    public void LayerInsideBackgroundVocals_IsDropped_AndNeverJoinsTheAdlib()
    {
        const string doc = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div>
                <p begin="1.0" end="3.0"><span begin="1.0" end="2.0">main</span> <span ttm:role="x-bg"><span begin="2.0" end="3.0">(ah)</span><span ttm:role="x-translation">(ah-en)</span></span></p>
              </div></body>
            </tt>
            """;

        var line = TtmlParser.Parse(doc).Lines![0];
        Assert.Equal("main", line.Text);
        Assert.Null(line.Translation);
        Assert.DoesNotContain(line.BackgroundWords!, w => w.Text.Contains("ah-en"));
    }

    [Fact]
    public void HeadTransliterationSpaces_DoNotChangeBodyWordSplitting()
    {
        // The head's transliteration spans carry authored spaces ("spaced "). The authored-space
        // heuristic (issue #32) must look at the body's own spans only; counting the head
        // would treat the body's indentation as formatting and fuse "two words" into one.
        const string doc = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal">
              <head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal"><transliterations><transliteration xml:lang="ja-Latn"><text for="L1"><span begin="1.0" end="1.5">spaced </span></text></transliteration></transliterations></iTunesMetadata></metadata></head>
              <body><div><p begin="1.0" end="3.0" itunes:key="L1">
                <span begin="1.0" end="2.0">two</span>
                <span begin="2.0" end="3.0">words</span>
              </p></div></body>
            </tt>
            """;

        var line = TtmlParser.Parse(doc, joinSplitWords: true).Lines![0];
        Assert.Equal("two words", line.Text);
        Assert.Equal(2, line.Words!.Count);
    }

    [Fact]
    public void FileWithoutLayers_HasNone()
    {
        const string doc = """
            <tt xmlns="http://www.w3.org/ns/ttml"><body><div><p begin="1.0" end="2.0"><span begin="1.0" end="2.0">only</span></p></div></body></tt>
            """;

        var line = TtmlParser.Parse(doc).Lines![0];
        Assert.False(line.HasTranslation);
        Assert.False(line.HasRomanization);
    }

    [Fact]
    public void InlineRomanizationAuthoredSpace_DoesNotAffectBodyWordSplitting()
    {
        // The only authored-space span in this document sits inside an inline x-roman
        // span. UsesAuthoredSpaces must exclude it (AncestorsAndSelf guard) so the main
        // words, separated only by newline+indent, are NOT fused by the "authored spaces"
        // heuristic. Fix-round-1 review: verified this fails (produces one fused word
        // instead of two) if the guard clause is removed from UsesAuthoredSpaces.
        const string doc = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div><p begin="1.0" end="3.5">
                <span begin="1.0" end="2.0">two</span>
                <span begin="2.0" end="3.0">words</span>
                <span ttm:role="x-roman"><span begin="3.0" end="3.5">spaced </span></span>
              </p></div></body>
            </tt>
            """;

        var line = TtmlParser.Parse(doc, joinSplitWords: true).Lines![0];
        Assert.Equal("two words", line.Text);
        Assert.Equal(2, line.Words!.Count);
    }

    [Fact]
    public void BackgroundTextNestedInsideTranslationSpan_IsExcludedFromTranslation()
    {
        // The reverse nesting of LayerInsideBackgroundVocals_IsDropped: here the x-bg
        // span sits INSIDE the translation span. Spec rule: x-bg text nested inside a
        // layer is excluded from it (it is the adlib, not part of the translation).
        const string doc = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div>
                <p begin="1.0" end="3.0"><span begin="1.0" end="2.0">main</span><span ttm:role="x-translation">Translated <span ttm:role="x-bg">(ah)</span> text</span></p>
              </div></body>
            </tt>
            """;

        var line = TtmlParser.Parse(doc).Lines![0];
        Assert.Equal("Translated text", line.Translation);
    }

    [Fact]
    public void BrInsideTranslationSpan_ContributesASpace()
    {
        // Matches the main-line walk's <br> → space rule (CollectContent), so a
        // multi-line translation doesn't fuse its lines into one word.
        const string doc = """
            <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
              <body><div>
                <p begin="1.0" end="3.0"><span begin="1.0" end="2.0">main</span><span ttm:role="x-translation">Line one<br/>Line two</span></p>
              </div></body>
            </tt>
            """;

        var line = TtmlParser.Parse(doc).Lines![0];
        Assert.Equal("Line one Line two", line.Translation);
    }
}
