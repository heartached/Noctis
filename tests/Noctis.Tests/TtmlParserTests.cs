using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class TtmlParserTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/ns/ttml\"";

    [Fact]
    public void Parse_LineTimedDocument_ReturnsSyncedLines()
    {
        var ttml = $@"<tt {Ns}><body>
            <div>
                <p begin=""0:12.500"" end=""0:15.200"">Hello world</p>
                <p begin=""0:15.200"" end=""0:18.000"">Second line</p>
            </div>
        </body></tt>";

        var (lines, plain) = TtmlParser.Parse(ttml);

        Assert.NotNull(lines);
        Assert.Equal(2, lines!.Count);
        Assert.Equal("Hello world", lines[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(12_500), lines[0].Timestamp);
        Assert.Equal(TimeSpan.FromMilliseconds(15_200), lines[0].EndTimestamp);
        Assert.True(lines[0].IsSynced);
        Assert.Null(lines[0].Words);
        Assert.Equal("Hello world\nSecond line", plain);
    }

    [Fact]
    public void Parse_WordTimedSpans_ProducesWordTimings()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000"">
                <span begin=""0:10.000"" end=""0:10.500"">Hello</span> <span begin=""0:10.500"" end=""0:11.200"">world</span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.NotNull(words);
        Assert.Equal(2, words!.Count);
        Assert.Equal("Hello ", words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(10_500), words[0].End);
        Assert.Equal("world", words[1].Text);
        Assert.Equal("Hello world", lines[0].Text);
    }

    [Fact]
    public void Parse_BackgroundVocalSpan_SplitsIntoBackgroundWords()
    {
        // Apple background vocals: a wrapper span with ttm:role="x-bg" containing its
        // own timed spans. They must become the line's background layer, not main words.
        var ttml = $@"<tt {Ns} xmlns:ttm=""http://www.w3.org/ns/ttml#metadata""><body><div>
            <p begin=""0:10.000"" end=""0:16.000"">
                <span begin=""0:10.000"" end=""0:10.500"">Wait</span> <span begin=""0:10.500"" end=""0:11.000"">for</span> <span begin=""0:11.000"" end=""0:11.500"">me</span>
                <span ttm:role=""x-bg""><span begin=""0:12.000"" end=""0:13.000"">(I</span> <span begin=""0:13.000"" end=""0:14.000"">will)</span></span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var line = lines![0];
        Assert.Equal("Wait for me", line.Text);
        Assert.Equal(3, line.Words!.Count);

        Assert.True(line.HasBackgroundWords);
        var bg = line.BackgroundWords!;
        Assert.Equal(2, bg.Count);
        Assert.Equal("(I ", bg[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(12_000), bg[0].Start);
        Assert.Equal("will)", bg[1].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(14_000), line.BackgroundEndTimestamp);
    }

    [Fact]
    public void Parse_BackgroundOnlyParagraph_KeptAsBackgroundOnlyLine()
    {
        // A <p> whose entire content is background vocals must not be dropped: it
        // becomes a line with no main words that renders only the small bg row.
        var ttml = $@"<tt {Ns} xmlns:ttm=""http://www.w3.org/ns/ttml#metadata""><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"" end=""0:10.500"">Lead</span></p>
            <p begin=""0:12.000"" end=""0:14.000""><span ttm:role=""x-bg""><span begin=""0:12.000"" end=""0:13.000"">(Ooh)</span></span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Equal(2, lines!.Count);
        var bgLine = lines[1];
        Assert.True(bgLine.HasBackgroundWords);
        Assert.Null(bgLine.Words);
        Assert.True(bgLine.IsBackgroundOnly);
        Assert.False(bgLine.ShowLineText);
        Assert.Equal("(Ooh)", bgLine.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(12_000), bgLine.Timestamp);
    }

    [Fact]
    public void Parse_NoBackgroundSpan_LeavesBackgroundNull()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"" end=""0:10.500"">Hello</span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.False(lines![0].HasBackgroundWords);
    }

    [Fact]
    public void Parse_SyllableSpansWithoutWhitespace_MergeIntoOneWord()
    {
        // Apple-style syllable timing: no whitespace between spans of the same word.
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"" end=""0:10.300"">tal</span><span begin=""0:10.300"" end=""0:10.800"">king</span> <span begin=""0:10.800"" end=""0:11.500"">now</span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.Equal(2, words!.Count);
        Assert.Equal("talking ", words[0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(10_000), words[0].Start);
        Assert.Equal(TimeSpan.FromMilliseconds(10_800), words[0].End);
        Assert.Equal("now", words[1].Text);
    }

    /// <summary>
    /// Issue #32: pretty-printed Apple TTML writes its word spacing inside the spans
    /// ("Is ", "that ") and splits "compromise" across three of them. The newline+indent
    /// between those spans is XML formatting, not a word break — rendering it as one
    /// showed "Is that a com pro mise?".
    /// </summary>
    [Fact]
    public void Parse_PrettyPrintedSyllableSpans_JoinIntoOneWord()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""00:00:54.822"" end=""00:00:58.399"">
                <span begin=""00:00:54.822"" end=""00:00:55.164"">Is </span>
                <span begin=""00:00:55.164"" end=""00:00:55.458"">that </span>
                <span begin=""00:00:55.458"" end=""00:00:55.647"">a </span>
                <span begin=""00:00:55.647"" end=""00:00:56.352"">com</span>
                <span begin=""00:00:56.352"" end=""00:00:56.728"">pro</span>
                <span begin=""00:00:56.728"" end=""00:00:58.399"">mise?</span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.Equal(4, words!.Count);
        Assert.Equal(new[] { "Is ", "that ", "a ", "compromise?" }, words.Select(w => w.Text));
        Assert.Equal("Is that a compromise?", lines[0].Text);

        // The joined word still carries all three syllable windows for the sweep.
        var joined = words[3];
        Assert.Equal(TimeSpan.FromMilliseconds(55_647), joined.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(58_399), joined.End);
        Assert.Equal(3, joined.Syllables!.Count);
        Assert.Equal(new[] { 3, 3, 5 }, joined.Syllables.Select(s => s.Length));
        Assert.Equal(TimeSpan.FromMilliseconds(56_352), joined.Syllables[1].Start);
    }

    [Fact]
    public void Parse_PrettyPrintedSyllableSpans_JoinDisabled_KeepsSpaces()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000"">
                <span begin=""0:10.000"" end=""0:10.500"">a </span>
                <span begin=""0:10.500"" end=""0:11.000"">com</span>
                <span begin=""0:11.000"" end=""0:12.000"">mise</span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml, joinSplitWords: false);

        Assert.Equal(new[] { "a ", "com ", "mise" }, lines![0].Words!.Select(w => w.Text));
    }

    /// <summary>
    /// The opposite convention: no span writes its own spacing, so the whitespace
    /// between elements IS the word separator. Dropping it would fuse the line into a
    /// single unwrappable word, so those documents keep the original handling.
    /// </summary>
    [Fact]
    public void Parse_PrettyPrintedWithoutAuthoredSpaces_KeepsWordBoundaries()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000"">
                <span begin=""0:10.000"" end=""0:10.500"">Hello</span>
                <span begin=""0:10.500"" end=""0:12.000"">world</span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Equal(new[] { "Hello ", "world" }, lines![0].Words!.Select(w => w.Text));
        Assert.Equal("Hello world", lines[0].Text);
    }

    /// <summary>A real space between two spans on the same line is authored content,
    /// not indentation, so it still separates words in a join-enabled document.</summary>
    [Fact]
    public void Parse_SameLineSpaceBetweenSpans_StillSeparatesWords()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"" end=""0:10.400"">Let </span><span begin=""0:10.400"" end=""0:11.000"">your</span> <span begin=""0:11.000"" end=""0:12.000"">go</span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Equal(new[] { "Let ", "your ", "go" }, lines![0].Words!.Select(w => w.Text));
    }

    [Fact]
    public void Parse_BackgroundVocalWrapperSpan_TimedChildrenBecomeBackgroundWords()
    {
        var ttml = $@"<tt {Ns} xmlns:ttm=""http://www.w3.org/ns/ttml#metadata""><body><div>
            <p begin=""0:20.000"" end=""0:24.000"">
                <span begin=""0:20.000"" end=""0:21.000"">Lead</span> <span ttm:role=""x-bg""><span begin=""0:21.000"" end=""0:22.000"">(echo)</span></span>
            </p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var line = lines![0];
        Assert.Single(line.Words!);
        Assert.Equal("Lead", line.Words![0].Text.Trim());
        Assert.Single(line.BackgroundWords!);
        Assert.Equal("(echo)", line.BackgroundWords![0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(21_000), line.BackgroundWords![0].Start);
    }

    [Fact]
    public void Parse_MissingWordEnds_BackfillsFromNextStartAndLineEnd()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:10.000"" end=""0:12.000""><span begin=""0:10.000"">One</span> <span begin=""0:10.800"">two</span></p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        var words = lines![0].Words;
        Assert.Equal(TimeSpan.FromMilliseconds(10_800), words![0].End);
        Assert.Equal(TimeSpan.FromMilliseconds(12_000), words[1].End);
    }

    [Fact]
    public void Parse_LinesOutOfOrder_SortsByTimestamp()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:30.000"">Later</p>
            <p begin=""0:10.000"">Earlier</p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Equal("Earlier", lines![0].Text);
        Assert.Equal("Later", lines[1].Text);
    }

    [Fact]
    public void Parse_EmptyOrWhitespaceLines_AreSkipped()
    {
        var ttml = $@"<tt {Ns}><body><div>
            <p begin=""0:05.000"" end=""0:06.000""> </p>
            <p begin=""0:10.000"">Real line</p>
        </div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Single(lines!);
        Assert.Equal("Real line", lines![0].Text);
    }

    [Fact]
    public void Parse_MalformedOrNonTtmlContent_ReturnsNull()
    {
        Assert.Null(TtmlParser.Parse(null).Lines);
        Assert.Null(TtmlParser.Parse("").Lines);
        Assert.Null(TtmlParser.Parse("not xml at all").Lines);
        Assert.Null(TtmlParser.Parse("<html><body><p>web page</p></body></html>").Lines);
        Assert.Null(TtmlParser.Parse($"<tt {Ns}><body></body></tt>").Lines);
    }

    [Theory]
    [InlineData("0:12.500", 12_500)]
    [InlineData("00:01:02.250", 62_250)]
    [InlineData("1:02:03.456", 3_723_456)]
    [InlineData("7.5s", 7_500)]
    [InlineData("1500ms", 1_500)]
    [InlineData("2m", 120_000)]
    [InlineData("1h", 3_600_000)]
    [InlineData("12.34", 12_340)]
    public void ParseTime_SupportedFormats_ReturnsExpectedMs(string value, double expectedMs)
    {
        Assert.Equal(TimeSpan.FromMilliseconds(expectedMs), TtmlParser.ParseTime(value));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("25f")]
    [InlineData("10t")]
    [InlineData("1:2:3:4")]
    public void ParseTime_UnsupportedFormats_ReturnsNull(string? value)
    {
        Assert.Null(TtmlParser.ParseTime(value));
    }

    [Fact]
    public void Parse_AppleStyleDocument_EndToEnd()
    {
        // Shape matches Apple Music lyric exports (itunes namespace, agents, keys).
        var ttml = @"<tt xmlns=""http://www.w3.org/ns/ttml"" xmlns:ttm=""http://www.w3.org/ns/ttml#metadata"" xmlns:itunes=""http://music.apple.com/lyric-ttml-internal"" itunes:timing=""Word"" xml:lang=""en"">
  <head><metadata><ttm:agent type=""person"" xml:id=""v1""/></metadata></head>
  <body dur=""3:20.416"">
    <div begin=""15.94"" end=""22.198"" itunes:songPart=""Verse"">
      <p begin=""15.94"" end=""18.512"" itunes:key=""L1"" ttm:agent=""v1""><span begin=""15.94"" end=""16.278"">Never</span> <span begin=""16.278"" end=""16.5"">gonna</span> <span begin=""16.5"" end=""16.943"">give</span></p>
    </div>
  </body>
</tt>";

        var (lines, plain) = TtmlParser.Parse(ttml);

        Assert.Single(lines!);
        Assert.Equal("Never gonna give", lines![0].Text);
        Assert.Equal(TimeSpan.FromMilliseconds(15_940), lines[0].Timestamp);
        Assert.Equal(3, lines[0].Words!.Count);
        Assert.Equal("Never gonna give", plain);
    }

    // ── Translation / romanization layers (GitHub #78) ──

    /// <summary>The issue's sample document, verbatim.</summary>
    private const string Issue78Sample = """
        <?xml version='1.0' encoding='UTF-8'?>
        <tt xmlns="http://www.w3.org/ns/ttml"
            xmlns:itunes="http://music.apple.com/lyric-ttml-internal"
            xmlns:ttm="http://www.w3.org/ns/ttml#metadata"
            itunes:timing="Word"
            xml:lang="ja">
          <head>
            <metadata>
              <ttm:agent type="person" xml:id="v1"/>
              <iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal">
                <translations>
                  <translation type="subtitle" xml:lang="en-US">
                    <text for="L1">The heavy rain pouring down</text>
                    <text for="L2">The end of the ideal I drew</text>
                  </translation>
                </translations>
                <transliterations>
                  <transliteration xml:lang="ja-Latn">
                    <text for="L1">
                      <span begin="26.250" end="26.430">fu</span><span begin="26.430" end="26.650">ri</span>
                    </text>
                  </transliteration>
                </transliterations>
              </iTunesMetadata>
            </metadata>
          </head>
          <body dur="4:47.168">
            <div begin="26.250" end="4:47.168" itunes:songPart="Verse" ttm:agent="v1">
              <p begin="26.250" end="29.445" itunes:key="L1" ttm:agent="v1">
                <span begin="26.250" end="26.430">ふ</span><span begin="26.430" end="26.650">り</span>
              </p>
            </div>
          </body>
        </tt>
        """;

    [Fact]
    public void Parse_Issue78Sample_EndToEnd()
    {
        var (lines, plain) = TtmlParser.Parse(Issue78Sample);

        var line = Assert.Single(lines!);
        Assert.Equal("ふり", line.Text);
        Assert.Equal(2, line.Words!.Count);                       // CJK cells stay separate
        Assert.Equal("The heavy rain pouring down", line.Translation);
        Assert.True(line.ShowTranslation);

        // Romaji syllables with no whitespace between them join into one word, which
        // still sweeps on each syllable's own clock — same as the main line's spans.
        var romaji = Assert.Single(line.TransliterationWords!);
        Assert.Equal("furi", romaji.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(26_250), romaji.Start);
        Assert.Equal(TimeSpan.FromMilliseconds(26_650), romaji.End);
        Assert.Equal(2, romaji.Syllables!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(26_650), line.TransliterationEndTimestamp);
        Assert.True(line.ShowTransliterationWords);
        Assert.False(line.ShowTransliterationText);

        // Header content never becomes a line or reaches the Unsync text.
        Assert.Equal("ふり", plain);
    }

    private static string LayeredDoc(string metadata, string body) => $"""
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:itunes="http://music.apple.com/lyric-ttml-internal">
          <head><metadata><iTunesMetadata xmlns="http://music.apple.com/lyric-ttml-internal">{metadata}</iTunesMetadata></metadata></head>
          <body><div>{body}</div></body>
        </tt>
        """;

    [Fact]
    public void Parse_TranslationAttachesByKey_NotByOrder()
    {
        var ttml = LayeredDoc(
            """
            <translations><translation xml:lang="en">
              <text for="L2">second</text>
              <text for="L1">first</text>
            </translation></translations>
            """,
            """
            <p begin="1" end="2" itunes:key="L1">eins</p>
            <p begin="3" end="4" itunes:key="L2">zwei</p>
            <p begin="5" end="6">drei</p>
            """);

        var (lines, plain) = TtmlParser.Parse(ttml);

        Assert.Equal("first", lines![0].Translation);
        Assert.Equal("second", lines[1].Translation);
        Assert.Null(lines[2].Translation);                          // no key → no layer
        Assert.False(lines[2].ShowTranslation);
        Assert.Equal("eins\nzwei\ndrei", plain);
    }

    [Fact]
    public void Parse_UnknownKey_GetsNoLayer()
    {
        var ttml = LayeredDoc(
            """
            <translations><translation xml:lang="en"><text for="L9">orphan</text></translation></translations>
            <transliterations><transliteration xml:lang="ja-Latn"><text for="L9">orphan</text></transliteration></transliterations>
            """,
            """<p begin="1" end="2" itunes:key="L1">line</p>""");

        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);

        Assert.Null(line.Translation);
        Assert.Null(line.Transliteration);
        Assert.Null(line.TransliterationWords);
        Assert.False(line.HasTranslation);
        Assert.False(line.HasTransliteration);
    }

    [Fact]
    public void Parse_UntimedTransliteration_IsStaticText()
    {
        var ttml = LayeredDoc(
            """<transliterations><transliteration xml:lang="ja-Latn"><text for="L1">furi sosogu</text></transliteration></transliterations>""",
            """<p begin="1" end="2" itunes:key="L1">降り注ぐ</p>""");

        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);

        Assert.Equal("furi sosogu", line.Transliteration);
        Assert.Null(line.TransliterationWords);
        Assert.True(line.ShowTransliterationText);
        Assert.False(line.ShowTransliterationWords);
    }

    [Fact]
    public void Parse_TimedTransliteration_WhitespaceBetweenSpansSplitsWords()
    {
        var ttml = LayeredDoc(
            """
            <transliterations><transliteration xml:lang="ja-Latn"><text for="L1"><span begin="1.0" end="1.2">fu</span><span begin="1.2" end="1.4">ri</span> <span begin="1.5" end="1.7">so</span><span begin="1.7" end="1.9">so</span><span begin="1.9">gu</span></text></transliteration></transliterations>
            """,
            """<p begin="1" end="2.5" itunes:key="L1">降り注ぐ</p>""");

        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);
        var words = line.TransliterationWords!;

        Assert.Equal(2, words.Count);
        Assert.Equal("furi ", words[0].Text);
        Assert.Equal("sosogu", words[1].Text);
        Assert.Equal(TimeSpan.FromSeconds(1.5), words[1].Start);
        // The open last syllable is bounded by the line end, like main-line words.
        Assert.Equal(TimeSpan.FromSeconds(2.5), words[1].End);
        Assert.Equal("furi sosogu", line.Transliteration);
    }

    [Theory]
    [InlineData("es", "hola")]
    [InlineData("es-MX", "hola")]
    [InlineData("fr-FR", "salut")]
    [InlineData("de", "hello")]   // no match → first in the file
    [InlineData(null, "hello")]
    public void Parse_SeveralTranslations_PicksTheUiLanguage_ElseTheFirst(string? ui, string expected)
    {
        var ttml = LayeredDoc(
            """
            <translations>
              <translation xml:lang="en-US"><text for="L1">hello</text></translation>
              <translation xml:lang="es-ES"><text for="L1">hola</text></translation>
              <translation xml:lang="fr"><text for="L1">salut</text></translation>
            </translations>
            """,
            """<p begin="1" end="2" itunes:key="L1">konnichiwa</p>""");

        var line = Assert.Single(TtmlParser.Parse(ttml, preferredLanguage: ui).Lines!);

        Assert.Equal(expected, line.Translation);
    }

    [Fact]
    public void Parse_SeveralTransliterations_UseTheSameLanguageRule()
    {
        var ttml = LayeredDoc(
            """
            <transliterations>
              <transliteration xml:lang="ja-Latn"><text for="L1">romaji</text></transliteration>
              <transliteration xml:lang="ko-Latn"><text for="L1">romaja</text></transliteration>
            </transliterations>
            """,
            """<p begin="1" end="2" itunes:key="L1">x</p>""");

        Assert.Equal("romaja", TtmlParser.Parse(ttml, preferredLanguage: "ko-KR").Lines![0].Transliteration);
        Assert.Equal("romaji", TtmlParser.Parse(ttml, preferredLanguage: "en").Lines![0].Transliteration);
    }

    [Fact]
    public void Parse_HeaderParagraphs_NeverBecomeLines()
    {
        var ttml = """
            <tt xmlns="http://www.w3.org/ns/ttml"><head><metadata><p begin="0" end="1">header</p></metadata></head>
            <body><div><p begin="1" end="2">body</p></div></body></tt>
            """;

        var (lines, plain) = TtmlParser.Parse(ttml);

        Assert.Equal("body", Assert.Single(lines!).Text);
        Assert.Equal("body", plain);
    }

    [Fact]
    public void Parse_MoreParagraphsThanLineCap_StopsAtCap()
    {
        // The lyrics list is not virtualized: a hostile sidecar of 200k <p>s realized
        // every line on the UI thread.
        var body = string.Concat(Enumerable.Range(0, EnhancedLrcParser.MaxLyricLines + 50)
            .Select(i => $"<p begin=\"{i}s\">l{i}</p>"));
        var ttml = $"<tt {Ns}><body><div>{body}</div></body></tt>";

        var (lines, _) = TtmlParser.Parse(ttml);

        Assert.Equal(EnhancedLrcParser.MaxLyricLines, lines!.Count);
        Assert.Equal("l0", lines[0].Text);
    }

    [Fact]
    public void Parse_LineWithMoreWordsThanCap_KeepsTextDropsWordTiming()
    {
        var spans = string.Join(" ", Enumerable.Range(0, EnhancedLrcParser.MaxWordsPerLine + 1)
            .Select(i => $"<span begin=\"1s\" end=\"2s\">w{i}</span>"));
        var bg = string.Join(" ", Enumerable.Range(0, EnhancedLrcParser.MaxWordsPerLine + 1)
            .Select(i => $"<span begin=\"1s\" end=\"2s\">b{i}</span>"));
        var ttml = $@"<tt {Ns} xmlns:ttm=""http://www.w3.org/ns/ttml#metadata""><body><div>
            <p begin=""1s"" end=""2s"">{spans} <span ttm:role=""x-bg"">{bg}</span></p>
        </div></body></tt>";

        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);

        Assert.Null(line.Words);
        Assert.False(line.HasBackgroundWords);
        Assert.StartsWith("w0 w1 ", line.Text);
        Assert.EndsWith($"w{EnhancedLrcParser.MaxWordsPerLine}", line.Text);
    }

    [Fact]
    public void Parse_LineWithWordsAtCap_KeepsWordTiming()
    {
        var spans = string.Join(" ", Enumerable.Range(0, EnhancedLrcParser.MaxWordsPerLine)
            .Select(i => $"<span begin=\"1s\" end=\"2s\">w{i}</span>"));
        var ttml = $"<tt {Ns}><body><div><p begin=\"1s\" end=\"2s\">{spans}</p></div></body></tt>";

        var line = Assert.Single(TtmlParser.Parse(ttml).Lines!);

        Assert.Equal(EnhancedLrcParser.MaxWordsPerLine, line.Words!.Count);
    }
}
