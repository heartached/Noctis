using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Words the model missed at the start (or end) of a line are spread at the song's own singing
/// pace, measured from consecutive heard words, instead of a fixed 0.42 s/word: a fast (rap)
/// line whose first words were missed otherwise starts too early.
/// </summary>
public class LyricsAlignerPaceTests
{
    private static RecognizedWord W(string text, double start, double dur = 0.2) =>
        new(text, TimeSpan.FromSeconds(start), TimeSpan.FromSeconds(start + dur), 0.9f);

    private static TimeSpan S(double sec) => TimeSpan.FromMilliseconds(Math.Round(sec * 1000));

    /// <summary>Four lines of four words heard at 0.25 s/word, then a line whose first two words were not heard.</summary>
    private static (string[] Lines, List<RecognizedWord> Heard) FastSong()
    {
        var vocab = new[] { "river", "stone", "window", "silver", "garden", "thunder", "candle", "harbor", "meadow", "lantern", "forest", "valley", "ocean", "mirror", "shadow", "canyon" };
        var lines = new List<string>();
        var heard = new List<RecognizedWord>();
        var t = 1.0;
        for (var l = 0; l < 4; l++)
        {
            var words = vocab.Skip(l * 4).Take(4).ToArray();
            lines.Add(string.Join(' ', words));
            foreach (var w in words) { heard.Add(W(w, t)); t += 0.25; }
            t += 1.0;
        }
        lines.Add("quickly spoken purple elephant dancing");
        // "quickly spoken" missed; "purple" heard at 12.0.
        heard.Add(W("purple", 12.0));
        heard.Add(W("elephant", 12.25));
        heard.Add(W("dancing", 12.5));
        return (lines.ToArray(), heard);
    }

    [Fact]
    public void LeadingUnheardWords_BackOffAtTheSongsPace()
    {
        var (lines, heard) = FastSong();

        var aligned = LyricsAligner.Align(lines, heard, S(30));

        Assert.Equal(S(12.0), aligned[4].Words[2].Start);
        Assert.Equal(S(12.0 - 2 * 0.25), aligned[4].Start); // not 12.0 − 2 × 0.42
        Assert.Equal(S(11.75), aligned[4].Words[1].Start);
    }

    [Fact]
    public void TooFewHeardPairs_KeepTheDefaultPace()
    {
        var lines = new[] { "quickly spoken purple elephant" };
        var heard = new[] { W("purple", 5.0), W("elephant", 5.25) };

        var aligned = LyricsAligner.Align(lines, heard, S(30));

        Assert.Equal(S(5.0 - 2 * 0.42), aligned[0].Start);
    }
}
