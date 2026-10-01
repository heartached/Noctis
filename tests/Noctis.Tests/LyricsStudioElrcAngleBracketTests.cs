using System;
using System.Collections.Generic;
using System.Linq;
using Noctis.Services;
using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics a user types may hold "&lt;" or "&gt;" ("&lt;3", "-&gt;"). ELRC word tags use the same
/// characters, so a word holding them must still read back as the same words at the same times
/// through both readers: the Studio's loader and the lyrics page's parser.
/// </summary>
public class LyricsStudioElrcAngleBracketTests
{
    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    private static AlignedLine Line(double start, params string[] words)
    {
        var timed = words.Select((w, i) => new AlignedWord(w, S(start + i * 0.5), S(start + i * 0.5 + 0.4))).ToList();
        return new AlignedLine(string.Join(' ', words), timed[0].Start, timed[^1].End, timed, 0.9, false);
    }

    public static IEnumerable<object[]> Words() => new[]
    {
        new object[] { new[] { "you", "<3", "me" } },
        new object[] { new[] { "left", "->", "right" } },
        new object[] { new[] { "a>b", "and", "c<d" } },
        new object[] { new[] { "<", "alone" } },
        new object[] { new[] { "<3>", "twice" } },
    };

    [Theory]
    [MemberData(nameof(Words))]
    public void WordsHoldingAngleBrackets_ReadBackUnchanged(string[] words)
    {
        var lines = new[] { Line(10, words), Line(20, "next", "line") };
        var elrc = TimedLyricsBuilder.BuildElrc(lines);

        var studio = ExistingLyricsLoader.ParseTimed(elrc);
        Assert.Equal(2, studio.Count);
        Assert.Equal(words, studio[0].Words.Select(w => w.Text).ToArray());
        for (var i = 0; i < words.Length; i++)
            Assert.True(Math.Abs((studio[0].Words[i].Start - lines[0].Words[i].Start).TotalMilliseconds) <= 10, $"studio word {i} time");

        var body = elrc.Split('\n')[0];
        body = body[(body.IndexOf(']') + 1)..];
        var (_, page) = EnhancedLrcParser.ParseLine(body);
        Assert.NotNull(page);
        Assert.Equal(words, page!.Select(w => w.Text.Trim()).ToArray());
    }
}
