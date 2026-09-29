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
        // An interpolated line's words are the aligner's even spread, not heard times.
        HasWordTimings = line.Words.Count > 0 && !line.Interpolated;
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

    /// <summary>ELRC chosen in the Studio toolbar: the row shows, and edits, the line as ELRC will save it.</summary>
    [ObservableProperty] private bool _showWordTags;

    /// <summary>Why the last row edit was refused (a bad or backwards time); null when the row is fine.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasEditError))]
    private string? _editError;

    public bool HasEditError => EditError is not null;

    partial void OnShowWordTagsChanged(bool value) => RaiseRow();
    partial void OnHasWordTimingsChanged(bool value) => RaiseRow();

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
        RaiseRow();
        SnapshotBaseline();
        Changed?.Invoke();
    }

    // ── Row text (the review row's text box) ──────────────────────────────────

    // ELRC typing waits here until Enter / focus loss, so a half-typed time never reaches the words.
    private string? _rowDraft;

    /// <summary>
    /// What the row shows. LRC: <see cref="Text"/>, applied per keystroke as always. ELRC: the
    /// line exactly as it will be saved (<see cref="TimedLyricsBuilder.BuildElrcBody"/>, without
    /// the line stamp the time pill shows) — a line with no word timings is its plain text.
    /// ELRC typing is held until <see cref="CommitRowText"/>.
    /// </summary>
    public string RowText
    {
        get => _rowDraft ?? (ShowWordTags ? TimedLyricsBuilder.BuildElrcBody(ToAlignedLine()) : Text);
        set
        {
            if (!ShowWordTags) { Text = value; return; }
            _rowDraft = value ?? string.Empty;
            EditError = null;
        }
    }

    /// <summary>
    /// Reads the typed ELRC row back into words (Enter, focus loss). Tags become word times;
    /// with no tags left it is a plain text edit (times kept as in LRC). A bad or backwards time
    /// changes nothing and sets <see cref="EditError"/>; the typing stays until
    /// <see cref="RevertRowText"/>. Returns false when the edit was refused.
    /// </summary>
    public bool CommitRowText()
    {
        if (_rowDraft is not { } draft) return true;
        _rowDraft = null;
        if (draft == RowText) { EditError = null; return true; }
        if (!TimedLyricsBuilder.TryParseElrcBody(draft, Start, End, out var words, out var error))
        {
            _rowDraft = draft;
            EditError = error;
            return false;
        }
        if (words is null) Text = draft;
        else ApplyWords(words);
        RaiseRow();
        return true;
    }

    /// <summary>
    /// Drops unapplied typing so the row shows the line again (Esc; or focus leaving a refused
    /// edit, which keeps its mark until the row is typed in again). False when there was nothing to undo.
    /// </summary>
    public bool RevertRowText(bool keepError = false)
    {
        if (_rowDraft is null && EditError is null) return false;
        _rowDraft = null;
        if (!keepError) EditError = null;
        OnPropertyChanged(nameof(RowText));
        return true;
    }

    /// <summary>
    /// Puts parsed row words on the line. A time the row showed unchanged keeps its full
    /// precision (the row rounds to 10 ms), so fixing one word never moves the others.
    /// </summary>
    private void ApplyWords(IReadOnlyList<AlignedWord> parsed)
    {
        var old = Words.Select(w => w.Start).ToList();
        TimeSpan Keep(TimeSpan t, int i)
        {
            var shown = TimedLyricsBuilder.FormatTimestamp(t);
            if (i < old.Count && TimedLyricsBuilder.FormatTimestamp(old[i]) == shown) return old[i];
            foreach (var o in old)
                if (TimedLyricsBuilder.FormatTimestamp(o) == shown) return o;
            return t;
        }

        _applyingWords = true;
        try
        {
            while (Words.Count > parsed.Count) Words.RemoveAt(Words.Count - 1);
            var floor = TimeSpan.Zero;
            for (var i = 0; i < parsed.Count; i++)
            {
                var start = Keep(parsed[i].Start, i);
                if (start < floor) start = floor;
                floor = start;
                if (i < Words.Count) { Words[i].Text = parsed[i].Text; Words[i].Start = start; }
                else Words.Add(new ReviewWord(this, parsed[i].Text, start));
            }
        }
        finally { _applyingWords = false; }
        var end = TimedLyricsBuilder.FormatTimestamp(parsed[^1].End) == TimedLyricsBuilder.FormatTimestamp(End) ? End : parsed[^1].End;
        End = end < Words[^1].Start ? Words[^1].Start : end;
        _text = JoinWords();
        HasWordTimings = true;
        SnapshotBaseline();
        OnPropertyChanged(nameof(Text));
        RaiseTimes();
        Changed?.Invoke();
    }

    /// <summary>The line changed under the row: show it, dropping unapplied typing and a stale refusal.</summary>
    private void RaiseRow()
    {
        _rowDraft = null;
        EditError = null;
        OnPropertyChanged(nameof(RowText));
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
        // A line the user timed by hand is no longer a guess: keep its words on the next rebuild.
        return new AlignedLine(text, Start, end, words, Confidence, Interpolated && !HasWordTimings);
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
        RaiseRow();
    }
}

public sealed partial class ReviewWord
{
    internal void OnEndChanged() => OnPropertyChanged(nameof(End));
}
