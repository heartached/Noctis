using Noctis.Services.LyricsStudio;
using Whisper.net;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Word timing from Whisper's DTW token times, and the one-window-per-call loop that works
/// around whisper.cpp dropping DTW segments after the first window.
/// </summary>
public class WhisperDtwTimingTests
{
    private static WhisperTranscriber.DtwTokenView T(string text, long t0, long t1, long dtw, float p = 0.9f) => new(text, t0, t1, dtw, p);

    private static List<RecognizedWord> Words(IEnumerable<WhisperTranscriber.DtwTokenView> tokens, ref long? previousEnd) =>
        WhisperTranscriber.WordsFromTokens(WhisperTranscriber.DtwTokenSpans(tokens, ref previousEnd), TimeSpan.Zero, TimeSpan.FromSeconds(30), null);

    private static TimeSpan Cs(long cs) => TimeSpan.FromMilliseconds(cs * 10);

    [Fact]
    public void DtwTime_IsTheTokenEnd_SoAWordStartsWhereThePreviousOneEnded()
    {
        long? previous = null;
        var words = Words(new[]
        {
            T(" I", 90, 100, 110),
            T(" love", 100, 130, 140),
            T(" hel", 130, 150, 160),
            T("lo", 150, 170, 175),
        }, ref previous);

        Assert.Equal(new[] { "I", "love", "hello" }, words.Select(w => w.Text));
        Assert.Equal(Cs(90), words[0].Start);   // window's first token: its own t0 (within the lead limit)
        Assert.Equal(Cs(110), words[0].End);
        Assert.Equal(Cs(110), words[1].Start);  // = end of "I", not its own DTW time (140)
        Assert.Equal(Cs(140), words[1].End);
        Assert.Equal(Cs(140), words[2].Start);  // a word's pieces merge: start of the first, end of the last
        Assert.Equal(Cs(175), words[2].End);
        Assert.Equal(175, previous);
    }

    [Fact]
    public void WordAfterAPause_StartsAtMostTheLeadLimitBeforeItsEnd()
    {
        long? previous = 150;
        var words = Words(new[] { T(" world", 160, 420, 400) }, ref previous);

        Assert.Equal(Cs(400 - WhisperTranscriber.MaxWordLeadCs), words[0].Start);
        Assert.Equal(Cs(400), words[0].End);
    }

    [Fact]
    public void ContinuationPiece_IsNotLimited_OnlyAWordsFirstPiece()
    {
        long? previous = 100;
        var words = Words(new[] { T(" to", 100, 110, 120), T("night", 110, 200, 300) }, ref previous);

        Assert.Single(words);
        Assert.Equal("tonight", words[0].Text);
        Assert.Equal(Cs(100), words[0].Start);
        Assert.Equal(Cs(300), words[0].End);
    }

    [Fact]
    public void WindowFirstToken_UsesT0_ButNoEarlierThanTheLeadLimit()
    {
        long? previous = null;
        var words = Words(new[] { T(" Yeah", 0, 50, 200) }, ref previous);
        Assert.Equal(Cs(200 - WhisperTranscriber.MaxWordLeadCs), words[0].Start);

        previous = null;
        words = Words(new[] { T(" Yeah", 190, 210, 200) }, ref previous);
        Assert.Equal(Cs(190), words[0].Start);
    }

    [Fact]
    public void SpecialTokens_AndTokensWithoutDtw_KeepHeuristicTimes_AndDoNotMoveTheRunningEnd()
    {
        long? previous = null;
        var spans = WhisperTranscriber.DtwTokenSpans(new[]
        {
            T("[_BEG_]", 0, 0, -1),
            T(" hey", 10, 40, 60),
            T(" you", 40, 80, -1),
            T("[_TT_40]", 80, 80, -1),
        }, ref previous);

        Assert.Equal(new WhisperTranscriber.TokenView("[_BEG_]", 0, 0, 0.9f), spans[0]);
        Assert.Equal(new WhisperTranscriber.TokenView(" hey", 30, 60, 0.9f), spans[1]);
        Assert.Equal(new WhisperTranscriber.TokenView(" you", 40, 80, 0.9f), spans[2]);
        Assert.Equal(60, previous);
    }

    [Fact]
    public void RunningEnd_CarriesAcrossSegmentsOfTheSameWindow()
    {
        long? previous = null;
        var first = Words(new[] { T(" one", 0, 50, 50) }, ref previous);
        var second = Words(new[] { T(" two", 60, 90, 70) }, ref previous);

        Assert.Equal(Cs(50), second[0].Start); // the previous segment's last DTW end, not this segment's t0
        Assert.Equal(Cs(20), first[0].Start);
    }

    [Fact]
    public void Start_NeverAfterEnd_EvenIfDtwTimesStepBack()
    {
        long? previous = 300;
        var spans = WhisperTranscriber.DtwTokenSpans(new[] { T(" back", 200, 260, 250) }, ref previous);
        Assert.Equal(250, spans[0].StartCs);
        Assert.Equal(250, spans[0].EndCs);
    }

    private const int Sr = PcmDecoder16k.SampleRate;
    private const int Window = 30 * Sr;

    [Fact]
    public void NextWindow_StartsWhereTheLastSegmentEnded()
    {
        // whisper.cpp reports floor(100 * 24.3 / 30) = 81 % as it starts the next window.
        Assert.Equal((int)(24.3 * Sr), WhisperTranscriber.NextWindowOffset(TimeSpan.FromSeconds(24.3), 81, Window));
    }

    [Fact]
    public void NextWindow_SkipsTheRestOfTheChunk_WhenWhisperDid()
    {
        // Single timestamp ending: whisper.cpp consumed the whole 30 s although the text stopped at 12 s.
        Assert.Equal(Window, WhisperTranscriber.NextWindowOffset(TimeSpan.FromSeconds(12), 100, Window));
        // Last timestamp after the last text: follow the reported position.
        Assert.Equal(18 * Sr, WhisperTranscriber.NextWindowOffset(TimeSpan.FromSeconds(12), 60, Window));
    }

    [Fact]
    public void NextWindow_WithoutSegments_FollowsTheReportedPosition_AndNeverStalls()
    {
        Assert.Equal(Window, WhisperTranscriber.NextWindowOffset(null, 100, Window));
        Assert.Equal(15 * Sr, WhisperTranscriber.NextWindowOffset(null, 50, Window));
        Assert.Equal(Window, WhisperTranscriber.NextWindowOffset(null, 0, Window));
        Assert.Equal(Window, WhisperTranscriber.NextWindowOffset(TimeSpan.FromSeconds(0.4), 1, Window));
    }

    [Fact]
    public void NextWindow_ShortTail_ReportsAgainstItsOwnLength()
    {
        var tail = 12 * Sr;
        Assert.Equal(tail, WhisperTranscriber.NextWindowOffset(TimeSpan.FromSeconds(5), 100, tail));
        Assert.Equal(9 * Sr, WhisperTranscriber.NextWindowOffset(TimeSpan.FromSeconds(9), 75, tail));
    }

    [Fact]
    public void DtwShift_PullsMediumEarlier_LeavesBaseAlone()
    {
        Assert.Equal(-10, WhisperTranscriber.DtwShiftCs(WhisperAlignmentHeadsPreset.Medium));
        Assert.Equal(0, WhisperTranscriber.DtwShiftCs(WhisperAlignmentHeadsPreset.Base));
        Assert.Equal(0, WhisperTranscriber.DtwShiftCs(WhisperAlignmentHeadsPreset.None));
    }

    [Theory]
    [InlineData(WhisperModelSize.Tiny, WhisperAlignmentHeadsPreset.Base)]
    [InlineData(WhisperModelSize.Base, WhisperAlignmentHeadsPreset.Base)]
    [InlineData(WhisperModelSize.Small, WhisperAlignmentHeadsPreset.Medium)]
    [InlineData(WhisperModelSize.Medium, WhisperAlignmentHeadsPreset.Medium)]
    public void AlignmentHeads_FollowTheModelFileThatIsLoaded(WhisperModelSize size, WhisperAlignmentHeadsPreset expected)
    {
        Assert.Equal(expected, WhisperModelManager.AlignmentHeads(size));
    }
}
