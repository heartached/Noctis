using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Services.LyricsStudio;

namespace Noctis.ViewModels;

/// <summary>
/// One word in the Lyrics Studio review pane. Its <see cref="Start"/> is the only stored
/// time; <see cref="End"/> is the next word's start (or the line end for the last word), so
/// nudging one word never desynchronises its neighbours.
/// </summary>
public sealed partial class ReviewWord : ObservableObject
{
    internal ReviewWord(ReviewLine line, string text, TimeSpan start)
    {
        Line = line;
        _text = text;
        _start = start;
    }

    public ReviewLine Line { get; }

    [ObservableProperty] private string _text;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimeText))]
    private TimeSpan _start;

    /// <summary>Set by tap-to-time; cleared by a re-sync.</summary>
    [ObservableProperty] private bool _isTapped;

    /// <summary>The word whose chip is highlighted; nudges apply to it.</summary>
    [ObservableProperty] private bool _isSelected;

    /// <summary>The word tap mode is waiting for.</summary>
    [ObservableProperty] private bool _isTapTarget;

    public TimeSpan End => Line.EndOf(this);
    public string TimeText => TimedLyricsBuilder.FormatTimestamp(Start);

    partial void OnTextChanged(string value) => Line.WordTextEdited();
}

/// <summary>
/// An editable lyric line in the review pane: a list of timed words. The line's own start is
/// derived from its first word, so there is exactly one place a time lives. Line-level input
/// (an .lrc, or a line the aligner could not hear) still gets words, spread evenly, but
/// <see cref="HasWordTimings"/> stays false so LRC export is untouched and ELRC export writes
/// that line without word tags until the user times it (upgrade, nudge or tap).
/// </summary>
public sealed partial class ReviewLine : ObservableObject
{
    /// <summary>Minimum distance kept between two neighbouring word starts.</summary>
    public static readonly TimeSpan MinWordGap = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan DefaultWordSpan = TimeSpan.FromMilliseconds(420);

    // The reconciliation baseline: word times survive a text edit that keeps the word count.
    private List<(string Text, TimeSpan Start)> _baseline = new();
    private bool _applyingWords;

    /// <summary>Raised after any edit (text, time, nudge, tap) so the owner can persist a draft.</summary>
    public event Action? Changed;

    public ReviewLine(AlignedLine line)
    {
        Confidence = line.Confidence;
        Interpolated = line.Interpolated;
        HasWordTimings = line.Words.Count > 0;
        var start = line.Start;
        var end = line.End > line.Start ? line.End : line.Start;

        Words = new ObservableCollection<ReviewWord>();
        if (line.Words.Count > 0)
        {
            foreach (var w in line.Words)
                Words.Add(new ReviewWord(this, w.Text, w.Start));
            End = line.Words[^1].End > line.Words[^1].Start ? line.Words[^1].End : end;
        }
        else
        {
            var tokens = Tokenise(line.Text);
            if (end <= start) end = start + DefaultWordSpan * Math.Max(1, tokens.Length);
            var slice = tokens.Length == 0 ? TimeSpan.Zero : (end - start) / tokens.Length;
            for (var i = 0; i < tokens.Length; i++)
                Words.Add(new ReviewWord(this, tokens[i], start + slice * i));
            End = end;
        }
        _fallbackStart = start;
        _text = JoinWords();
        SnapshotBaseline();
        Words.CollectionChanged += (_, _) => RaiseTimes();
    }

    public ObservableCollection<ReviewWord> Words { get; }

    public double Confidence { get; }
    public bool Interpolated { get; }
    public bool IsLow => Interpolated || Confidence < 0.5;

    /// <summary>True when the words carry real (heard, nudged or tapped) times rather than an even spread.</summary>
    [ObservableProperty] private bool _hasWordTimings;

    /// <summary>Word strip open under the line.</summary>
    [ObservableProperty] private bool _isExpanded;

    private readonly TimeSpan _fallbackStart;
    public TimeSpan Start => Words.Count > 0 ? Words[0].Start : _fallbackStart;
    public TimeSpan End { get; private set; }
    public string TimeText => TimedLyricsBuilder.FormatTimestamp(Start);

    // ── Text ──────────────────────────────────────────────────────────────────

    private string _text;

    /// <summary>
    /// The line as one string. Setting it re-tokenises: with the same word count the existing
    /// times stay on the words in order; with a different count the words are spread evenly
    /// between the line's start and end. Restoring the original count brings the times back.
    /// </summary>
    public string Text
    {
        get => _text;
        set
        {
            var text = value ?? string.Empty;
            if (text == _text) return;
            _text = text;
            Retokenise(text);
            OnPropertyChanged();
            Changed?.Invoke();
        }
    }

    private void Retokenise(string text)
    {
        var tokens = Tokenise(text);
        _applyingWords = true;
        try
        {
            if (tokens.Length == _baseline.Count)
            {
                // Same shape as the baseline: put the baseline times back under the new words.
                while (Words.Count > tokens.Length) Words.RemoveAt(Words.Count - 1);
                for (var i = 0; i < tokens.Length; i++)
                {
                    if (i < Words.Count) { Words[i].Text = tokens[i]; Words[i].Start = _baseline[i].Start; }
                    else Words.Add(new ReviewWord(this, tokens[i], _baseline[i].Start));
                }
            }
            else
            {
                var start = Start;
                var end = End > start ? End : start + DefaultWordSpan * Math.Max(1, tokens.Length);
                var slice = tokens.Length == 0 ? TimeSpan.Zero : (end - start) / tokens.Length;
                while (Words.Count > tokens.Length) Words.RemoveAt(Words.Count - 1);
                for (var i = 0; i < tokens.Length; i++)
                {
                    var s = start + slice * i;
                    if (i < Words.Count) { Words[i].Text = tokens[i]; Words[i].Start = s; }
                    else Words.Add(new ReviewWord(this, tokens[i], s));
                }
            }
        }
        finally { _applyingWords = false; }
        RaiseTimes();
    }

    internal void WordTextEdited()
    {
        if (_applyingWords) return;
        _text = JoinWords();
        OnPropertyChanged(nameof(Text));
        SnapshotBaseline();
        Changed?.Invoke();
    }

    // ── Times ─────────────────────────────────────────────────────────────────

    /// <summary>Moves the whole line: every word and the end by the same delta, never below zero.</summary>
    public void Shift(TimeSpan delta)
    {
        if (Words.Count > 0)
        {
            var floor = Words[0].Start + delta;
            if (floor < TimeSpan.Zero) delta -= floor;
        }
        foreach (var w in Words) w.Start += delta;
        End += delta;
        SnapshotBaseline();
        RaiseTimes();
        Changed?.Invoke();
    }

    /// <summary>
    /// Moves one word only, kept between its neighbours (and inside the line end for the last
    /// word). Marks the line as word-timed. Returns the time actually applied.
    /// </summary>
    public TimeSpan NudgeWord(ReviewWord word, TimeSpan delta) => SetWordStart(word, word.Start + delta);

    /// <summary>Tap-to-time: stamps a time on one word with the same clamping as a nudge.</summary>
    public TimeSpan SetWordStart(ReviewWord word, TimeSpan time, bool tapped = false)
    {
        var i = Words.IndexOf(word);
        if (i < 0) return word.Start;
        var lower = i > 0 ? Words[i - 1].Start + MinWordGap : TimeSpan.Zero;
        var upper = i + 1 < Words.Count ? Words[i + 1].Start - MinWordGap : End;
        if (upper < lower) upper = lower;
        if (time < lower) time = lower;
        if (time > upper) time = upper;
        word.Start = time;
        if (tapped) word.IsTapped = true;
        HasWordTimings = true;
        SnapshotBaseline();
        RaiseTimes();
        Changed?.Invoke();
        return time;
    }

    /// <summary>
    /// Tap-to-time: stamps the playback position on word <paramref name="index"/>. Unlike a
    /// nudge it is not clamped by the words after it — those still carry old (or spread)
    /// times and are pushed forward to stay in order, since the user will tap them next.
    /// Returns the time applied.
    /// </summary>
    public TimeSpan TapWord(int index, TimeSpan time)
    {
        if (index < 0 || index >= Words.Count) return time;
        var lower = index > 0 ? Words[index - 1].Start + MinWordGap : TimeSpan.Zero;
        if (time < lower) time = lower;
        Words[index].Start = time;
        Words[index].IsTapped = true;
        for (var i = index + 1; i < Words.Count; i++)
        {
            var floor = Words[i - 1].Start + MinWordGap;
            if (Words[i].Start < floor) Words[i].Start = floor;
        }
        var lastFloor = Words[^1].Start + MinWordGap;
        if (End < lastFloor) End = lastFloor;
        HasWordTimings = true;
        SnapshotBaseline();
        RaiseTimes();
        Changed?.Invoke();
        return time;
    }

    /// <summary>Clears tap marks (a new tap pass, or a re-sync).</summary>
    public void ClearTapMarks()
    {
        foreach (var w in Words) { w.IsTapped = false; w.IsTapTarget = false; }
    }

    /// <summary>The last word owns the line end; tapping past it or nudging it later stretches the line.</summary>
    public void SetEnd(TimeSpan end)
    {
        var floor = Words.Count > 0 ? Words[^1].Start : Start;
        End = end < floor ? floor : end;
        RaiseTimes();
        Changed?.Invoke();
    }

    public TimeSpan EndOf(ReviewWord word)
    {
        var i = Words.IndexOf(word);
        if (i < 0) return word.Start;
        var end = i + 1 < Words.Count ? Words[i + 1].Start : End;
        return end < word.Start ? word.Start : end;
    }

    // ── Export ────────────────────────────────────────────────────────────────

    /// <summary>Words are exported only when they carry real times; a spread line exports as line-level.</summary>
    public AlignedLine ToAlignedLine()
    {
        var text = JoinWords();
        var end = End > Start ? End : Start;
        IReadOnlyList<AlignedWord> words = HasWordTimings
            ? Words.Select(w => new AlignedWord(w.Text, w.Start, w.End)).ToList()
            : Array.Empty<AlignedWord>();
        return new AlignedLine(text, Start, end, words, Confidence, Interpolated);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string[] Tokenise(string text) =>
        (text ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private string JoinWords() => string.Join(' ', Words.Select(w => w.Text).Where(t => t.Length > 0));

    private void SnapshotBaseline() => _baseline = Words.Select(w => (w.Text, w.Start)).ToList();

    private void RaiseTimes()
    {
        OnPropertyChanged(nameof(Start));
        OnPropertyChanged(nameof(End));
        OnPropertyChanged(nameof(TimeText));
        foreach (var w in Words) w.OnEndChanged();
    }
}

public sealed partial class ReviewWord
{
    internal void OnEndChanged() => OnPropertyChanged(nameof(End));
}
