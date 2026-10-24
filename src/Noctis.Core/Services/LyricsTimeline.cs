using Noctis.Models;

namespace Noctis.Services;

/// <summary>Result of one <see cref="LyricsTimeline.Update"/>.</summary>
/// <param name="LineChanged">True when the active line is a different line than before the call.</param>
/// <param name="ActiveIndex">Index of the active line in the timeline's list, or -1.</param>
/// <param name="ActiveLine">The active line, or null before the first synced line.</param>
/// <param name="ActiveSetChanged">True when <see cref="LyricsTimeline.ActiveIndices"/> changed —
/// always when <paramref name="LineChanged"/>, and also when an overlapping earlier line
/// joined or left the set while the active line stayed.</param>
public readonly record struct LyricsTimelineStep(
    bool LineChanged, int ActiveIndex, LyricLine? ActiveLine, bool ActiveSetChanged);

/// <summary>
/// The synced-lyrics engine, UI-free: walks a monotonic line cursor per position
/// sample (O(1) amortised, rewinds on seek), flips <see cref="LyricLine.IsActive"/>
/// on the outgoing/incoming lines, and drives the word layers of the active lines
/// (<see cref="LyricLine.CurrentWordIndex"/>, <see cref="LyricLine.BackgroundWordIndex"/>,
/// <see cref="LyricLine.TransliterationWordIndex"/>, <see cref="WordTiming.Progress"/>).
/// Lifted verbatim from the desktop LyricsViewModel so the Android lyrics page and the
/// desktop page share one clock.
/// The caller owns the line list and feeds the audible position (engine position
/// minus output latency, smoothed by <see cref="LyricsPlaybackClock"/>).
///
/// Overlapping lines (TTML duets: each &lt;p&gt; has its own begin/end, so the second
/// voice can start while the first is still singing) light together, as Apple Music
/// does: <see cref="ActiveLine"/> stays the newest started line (the scroll anchor), and
/// an earlier line whose sung end runs well past the next line's start stays
/// co-active — lit, its words sweeping on their own clock — until that end passes.
/// </summary>
public sealed class LyricsTimeline
{
    /// <summary>
    /// A line only stays lit past the next line's start when its sung end overlaps that
    /// start by at least this much. Shorter overlaps are boundary timing slop — ELRC end
    /// tags written a beat late, Apple's pre-roll on the incoming line — not two voices,
    /// and honouring them would flash the finished line lit for a frame or two.
    /// </summary>
    public static readonly TimeSpan MinOverlap = TimeSpan.FromMilliseconds(300);

    /// <summary>How many lines before the active one are checked for a still-sung
    /// overlap. Lines are sorted by start but not by end, so this bounds the scan.</summary>
    private const int CoActiveScanBack = 8;

    private readonly IReadOnlyList<LyricLine> _lines;
    private readonly TimeSpan _wordLookahead;
    private readonly TimeSpan _lineLookahead;

    private int _cursor;
    private TimeSpan _lastPosition = TimeSpan.MinValue;

    // The active set (co-active lines then the active line, ascending by index), and a
    // scratch pair the next set is resolved into before the two are swapped — reused
    // so the per-frame word clock allocates nothing.
    private List<LyricLine> _activeLines = new();
    private List<int> _activeIndices = new();
    private List<LyricLine> _nextLines = new();
    private List<int> _nextIndices = new();

    /// <param name="lines">The synced line list to walk; the caller owns and mutates it.</param>
    /// <param name="wordLookahead">Lead for word-timed lines (desktop: 80 ms). Small, so the
    /// sweep matches the vocal instead of trailing UI dispatch latency.</param>
    /// <param name="lineLookahead">Lead for lines WITHOUT word timings (desktop: 80 ms plus half
    /// the line-glide travel time). A plain line has no sweep, so its only cue is the glide
    /// bringing it to the anchor; activating it early lets the glide cover half its travel
    /// when the vocal starts.</param>
    public LyricsTimeline(IReadOnlyList<LyricLine> lines, TimeSpan wordLookahead, TimeSpan lineLookahead)
    {
        _lines = lines;
        _wordLookahead = wordLookahead;
        _lineLookahead = lineLookahead;
    }

    /// <summary>
    /// The Apple Music word motion: sung words float up (<see cref="WordFloat"/>) and
    /// held notes swell, lift and glow letter by letter (<see cref="EmphasisMotion"/>).
    /// On by default; off leaves every word still (lift 0, no held-note clock) and the
    /// sweep alone marks the vocal. Turning it off settles the words at once.
    /// </summary>
    public bool WordMotion
    {
        get => _wordMotion;
        set
        {
            if (_wordMotion == value) return;
            _wordMotion = value;
            if (!value) ClearMotion();
        }
    }
    private bool _wordMotion = true;

    /// <summary>The active line, or null.</summary>
    public LyricLine? ActiveLine { get; private set; }

    /// <summary>Index of <see cref="ActiveLine"/> in the list, or -1.</summary>
    public int ActiveIndex { get; private set; } = -1;

    /// <summary>Indices of every lit line, ascending: still-sung overlapping lines before
    /// <see cref="ActiveIndex"/>, then <see cref="ActiveIndex"/> itself. Empty when nothing
    /// is active. The list is live — copy it to keep a snapshot.</summary>
    public IReadOnlyList<int> ActiveIndices => _activeIndices;

    /// <summary>The lines of <see cref="ActiveIndices"/>, in the same order.</summary>
    public IReadOnlyList<LyricLine> ActiveLines => _activeLines;

    /// <summary>Forget the cursor and the active lines (track change, lyrics reload).
    /// Deactivates the lines it was holding.</summary>
    public void Reset()
    {
        if (ActiveLine is { } l) l.IsActive = false;
        foreach (var line in _activeLines)
        {
            line.IsActive = false;
            line.InterludeProgress = double.NaN;
        }
        ClearLead();
        ClearMotion();
        _activeLines.Clear();
        _activeIndices.Clear();
        ActiveLine = null;
        ActiveIndex = -1;
        Rewind();
    }

    /// <summary>
    /// Distance from line <paramref name="index"/> to the nearest lit line — the active
    /// line <paramref name="primaryIndex"/> or any of <paramref name="activeIndices"/> —
    /// so the opacity/blur ramps put every co-active line at full focus.
    /// </summary>
    public static int DistanceToNearestActive(int index, int primaryIndex, IReadOnlyList<int> activeIndices)
    {
        var best = Math.Abs(index - primaryIndex);
        for (int i = 0; i < activeIndices.Count && best > 0; i++)
            best = Math.Min(best, Math.Abs(index - activeIndices[i]));
        return best;
    }

    /// <summary>
    /// How long line <paramref name="index"/> stays the scroll target: its start to the next
    /// synced line's start, in ms (the tempo the line glide adapts to). NaN when either start
    /// is unknown or the order is not forward.
    /// </summary>
    public static double HoldMs(IReadOnlyList<LyricLine> lines, int index)
    {
        if (index < 0 || index >= lines.Count || lines[index].Timestamp is not { } start) return double.NaN;
        for (int i = index + 1; i < lines.Count; i++)
        {
            if (lines[i].Timestamp is not { } next) continue;
            var ms = (next - start).TotalMilliseconds;
            return ms > 0 ? ms : double.NaN;
        }
        return double.NaN;
    }

    /// <summary>Rewind the cursor to the start without touching the active line; the next
    /// <see cref="Update"/> re-resolves from the top (an explicit seek).</summary>
    public void Rewind()
    {
        _cursor = 0;
        _lastPosition = TimeSpan.MinValue;
    }

    /// <summary>
    /// Advance to <paramref name="position"/>. Word-timed lines activate on the word
    /// lookahead, plain lines on the line lookahead: a single line-level lead would switch
    /// lines early, force-completing the outgoing line's final word sweep before its end
    /// and leaving the incoming words dark — a visible jump-then-pause at every boundary.
    /// </summary>
    public LyricsTimelineStep Update(TimeSpan position)
    {
        if (_lines.Count == 0) return new(false, ActiveIndex, ActiveLine, false);

        // Seek-backwards detection: rewind the cursor so we don't miss earlier lines.
        // 750ms threshold tolerates small non-monotonic jitter from the player position poll.
        // It counts as a rewind: landing before the first lyric must clear the old line, not
        // let the safety branch below hold it lit.
        var rewound = false;
        if (_lastPosition != TimeSpan.MinValue &&
            position + TimeSpan.FromMilliseconds(750) < _lastPosition)
        {
            _cursor = 0;
            rewound = true;
        }
        _lastPosition = position;

        TimeSpan AdjustedFor(LyricLine l) => Adjusted(position, l);

        // Clamp cursor into range (collection may have shrunk).
        if (_cursor >= _lines.Count) _cursor = _lines.Count - 1;
        if (_cursor < 0) _cursor = 0;

        // Advance forward while the next synced line's timestamp has been reached.
        while (_cursor + 1 < _lines.Count)
        {
            var next = _lines[_cursor + 1];
            if (next.Timestamp.HasValue && next.Timestamp.Value <= AdjustedFor(next))
                _cursor++;
            else
                break;
        }

        // Mirror walk backwards. The forward walk above has no threshold, but rewinding
        // used to depend solely on the 750ms reset — and a timeline drag never produces
        // a drop that large in one sample, because this runs every 100ms off the sync
        // timer and every rendered frame off the word clock. The cursor stayed parked on
        // a later line, no candidate matched, and the safety branch below held the stale
        // line: dragging backwards froze the lyrics until release finally delivered a big
        // enough discontinuity. Stepping back per sample makes both directions resolve on
        // the same tick, and still costs nothing during normal forward playback.
        while (_cursor > 0)
        {
            var current = _lines[_cursor];
            if (current.Timestamp.HasValue && current.Timestamp.Value > AdjustedFor(current))
            {
                _cursor--;
                rewound = true;
            }
            else
                break;
        }

        var candidate = _lines[_cursor];
        LyricLine? bestMatch = null;
        int bestIndex = -1;
        if (candidate.Timestamp.HasValue && candidate.Timestamp.Value <= AdjustedFor(candidate))
        {
            bestMatch = candidate;
            bestIndex = _cursor;
        }

        // Safety: if no match found but we're past the start and have a current line,
        // keep the current line active (prevents "all dimmed" state from transient glitches).
        // Skipped when the walk above rewound to the very first line and even that one is
        // still ahead: the position genuinely sits before the first lyric, so holding the
        // stale line there would re-freeze exactly what the backward walk exists to fix.
        if (bestMatch == null && !rewound && ActiveLine != null && position.TotalSeconds > 1)
        {
            // The held line stays; overlapping lines around it still come and go.
            var heldSetChanged = ResolveActiveSet(position);
            UpdateActiveWords(position);
            return new(false, ActiveIndex, ActiveLine, heldSetChanged);
        }

        var changed = bestMatch != ActiveLine;
        ActiveLine = bestMatch;
        ActiveIndex = bestIndex;
        var setChanged = ResolveActiveSet(position);

        UpdateActiveWords(position);
        return new(changed, ActiveIndex, ActiveLine, changed || setChanged);
    }

    /// <summary>
    /// The line the LIST should be glided to — <see cref="ActiveIndex"/>, or the next line
    /// up to <paramref name="scrollLead"/> early. Measured on Apple Music: the list sets off
    /// toward the next line well before its first word (within ~0-180 ms of the outgoing
    /// line's last word, and ~0.7 s ahead of the next line when the gap is longer), so the
    /// line has arrived when the voice starts. Only the scroll position leads — activation
    /// and the sweep stay on the word lookahead, for the reason <see cref="Update"/> gives.
    ///
    /// Never before every lit line's sung end: the line being sung never scrolls away
    /// from the anchor. Only a word-timed next line leads (a plain line's activation
    /// already carries its glide lead), and only out of a word-timed active line, whose
    /// sung end is known, or out of the "..." intro / interlude dots. Pure function of
    /// <paramref name="position"/> and the current state, so a seek resolves on the same
    /// tick as <see cref="Update"/>.
    ///
    /// Overlapping voices: while an earlier line is still being sung over the newest one
    /// (a duet, <see cref="ActiveIndices"/>), the list holds on the earliest lit line and
    /// moves once it finishes — Apple Music keeps a still-sung line at the anchor rather
    /// than scrolling it up toward the top edge the moment the next voice comes in. Up to
    /// <see cref="OverlapHoldMaxLines"/> lines back; further than that the newest line wins.
    /// </summary>
    public int ScrollIndex(TimeSpan position, TimeSpan scrollLead)
    {
        var active = ActiveIndex;
        if (active < 0) return active;
        if (_activeIndices.Count > 1)
        {
            var first = _activeIndices[0];
            return active - first <= OverlapHoldMaxLines ? first : active;
        }
        if (active + 1 >= _lines.Count || ActiveLine is not ({ HasWords: true } or { IsIntroPlaceholder: true })) return active;
        var next = _lines[active + 1];
        if (next.Timestamp is not { } nextStart || !next.HasWords) return active;

        var leadStart = nextStart - scrollLead;
        foreach (var lit in _activeLines)
        {
            if (SungEnd(lit) is not { } end)
            {
                // The dots have no sung end: they lead out on the lead alone.
                if (lit.IsIntroPlaceholder) continue;
                return active;
            }
            if (end > leadStart) leadStart = end;
        }
        return position + _wordLookahead >= leadStart ? active + 1 : active;
    }

    /// <summary>How many lines above the newest started line the list may hold on a line
    /// that is still being sung (see <see cref="ScrollIndex"/>).</summary>
    public const int OverlapHoldMaxLines = 2;

    /// <summary>
    /// <see cref="ScrollIndex"/>, applied to the lines: while the list leads into the next
    /// line, that line is <see cref="LyricLine.IsLeading"/> with every word layer reset to
    /// unsung (a line sung before a seek back still holds its words fully swept), so it
    /// comes in at the unsung level and lights only as the sweep reaches it. Call after
    /// <see cref="Update"/>: a lead line that just activated is already
    /// <see cref="LyricLine.IsActive"/> when its lead flag drops, so its word layers never
    /// flip back to the inactive look in between.
    /// </summary>
    public int Lead(TimeSpan position, TimeSpan scrollLead)
    {
        var index = ScrollIndex(position, scrollLead);
        var lead = index > ActiveIndex && index < _lines.Count ? _lines[index] : null;
        if (ReferenceEquals(lead, _leadLine)) return index;
        if (lead != null)
        {
            if (lead.HasWords) lead.CurrentWordIndex = -1;
            if (lead.HasBackgroundWords) lead.BackgroundWordIndex = -1;
            if (lead.HasTransliterationWords) lead.TransliterationWordIndex = -1;
            lead.IsLeading = true;
        }
        if (_leadLine != null) _leadLine.IsLeading = false;
        _leadLine = lead;
        return index;
    }

    private LyricLine? _leadLine;

    private void ClearLead()
    {
        if (_leadLine != null) _leadLine.IsLeading = false;
        _leadLine = null;
    }

    private TimeSpan Adjusted(TimeSpan position, LyricLine l) =>
        position + (l.HasWords || l.HasBackgroundWords || l.HasTransliterationWords ? _wordLookahead : _lineLookahead);

    /// <summary>
    /// Rebuilds the active set around <see cref="ActiveLine"/>: earlier lines still being
    /// sung over it (see <see cref="IsStillSungOverlap"/>), then the active line. Lines
    /// leaving the set are deactivated, lines in it are (re)activated. Returns true when
    /// the set differs from the previous one.
    /// </summary>
    private bool ResolveActiveSet(TimeSpan position)
    {
        _nextLines.Clear();
        _nextIndices.Clear();
        var primary = ActiveLine;
        if (primary != null)
        {
            // The index is only trusted while it still points at the line (the safety
            // branch can hold a line across a list the caller has since mutated).
            if (ActiveIndex >= 0 && ActiveIndex < _lines.Count && ReferenceEquals(_lines[ActiveIndex], primary))
            {
                for (int i = Math.Max(0, ActiveIndex - CoActiveScanBack); i < ActiveIndex; i++)
                {
                    if (IsStillSungOverlap(i, position))
                    {
                        _nextLines.Add(_lines[i]);
                        _nextIndices.Add(i);
                    }
                }
            }
            _nextLines.Add(primary);
            _nextIndices.Add(ActiveIndex);
        }

        var changed = _nextLines.Count != _activeLines.Count;
        for (int i = 0; !changed && i < _nextLines.Count; i++)
            changed = !ReferenceEquals(_nextLines[i], _activeLines[i]) || _nextIndices[i] != _activeIndices[i];

        foreach (var line in _activeLines)
        {
            if (!_nextLines.Contains(line))
                Deactivate(line, position);
        }
        // Safety: ensure the active lines stay active even if something reset them.
        foreach (var line in _nextLines)
        {
            if (!line.IsActive)
            {
                line.IsActive = true;
                // Back in the set (a seek, an overlap) before its words settled: the
                // line drives their lift again.
                if (_floatTail.Count > 0) ReclaimFloat(line);
            }
        }

        (_activeLines, _nextLines) = (_nextLines, _activeLines);
        (_activeIndices, _nextIndices) = (_nextIndices, _activeIndices);
        return changed;
    }

    /// <summary>
    /// True when the earlier line at <paramref name="index"/> is still being sung: it has
    /// started, its sung end (<see cref="SungEnd"/>) is still ahead on the same lookahead
    /// clock that activates it, and that end overlaps the next synced line's start by at
    /// least <see cref="MinOverlap"/>. Lines with no known end (plain LRC) never qualify.
    /// </summary>
    private bool IsStillSungOverlap(int index, TimeSpan position)
    {
        var line = _lines[index];
        if (line.Timestamp is not { } start || SungEnd(line) is not { } end) return false;
        if (NextSyncedLineStart(index) is not { } nextStart || end - nextStart < MinOverlap) return false;
        var adjusted = Adjusted(position, line);
        return start <= adjusted && adjusted < end;
    }

    /// <summary>The latest end any layer of the line knows (line end, last word ends,
    /// background and romanization ends); null when none is known.</summary>
    internal static TimeSpan? SungEnd(LyricLine line)
    {
        TimeSpan? end = line.EndTimestamp;
        static TimeSpan? Later(TimeSpan? a, TimeSpan? b) => a is null ? b : b is null ? a : (a > b ? a : b);
        if (line.HasWords) end = Later(end, line.Words![^1].End);
        end = Later(end, line.BackgroundEndTimestamp);
        if (line.HasBackgroundWords) end = Later(end, line.BackgroundWords![^1].End);
        end = Later(end, line.TransliterationEndTimestamp);
        if (line.HasTransliterationWords) end = Later(end, line.TransliterationWords![^1].End);
        return end;
    }

    /// <summary>
    /// Deactivates a line leaving the active set — leaving it fully swept (index past the
    /// end) so the bright overlay keeps covering the words while the base layer fades back
    /// to full opacity. Snapping to -1 here blanked the overlay instantly, which read as
    /// the finished line dimming for a beat. Re-entry recomputes the real index.
    /// </summary>
    private void Deactivate(LyricLine line, TimeSpan position)
    {
        line.IsActive = false;
        if (!double.IsNaN(line.InterludeProgress)) line.InterludeProgress = double.NaN;
        HandOffEmphasis(line.Words);
        HandOffEmphasis(line.BackgroundWords);
        HandOffEmphasis(line.TransliterationWords);
        var fallStart = (position + _wordLookahead).TotalMilliseconds;
        HandOffFloat(line.Words, fallStart);
        HandOffFloat(line.BackgroundWords, fallStart);
        HandOffFloat(line.TransliterationWords, fallStart);
        if (line.HasWords)
            line.CurrentWordIndex = line.Words!.Count;
        if (line.HasBackgroundWords)
            line.BackgroundWordIndex = line.BackgroundWords!.Count;
        if (line.HasTransliterationWords)
            line.TransliterationWordIndex = line.TransliterationWords!.Count;
    }

    /// <summary>Drives the word layers of every active line, each on its own clock, and
    /// lets held-note animations of lines that just left the set play out.</summary>
    private void UpdateActiveWords(TimeSpan position)
    {
        // Settling words first: a word the active set drives again ends on its value.
        DriveFloatTail(position + _wordLookahead);
        for (int i = 0; i < _activeLines.Count; i++)
        {
            var line = _activeLines[i];
            if (line.IsIntroPlaceholder)
                DriveInterlude(line, _activeIndices[i], position);
            else
                UpdateLineWords(line, _activeIndices[i], position);
        }
        DriveEmphasisTail(position + _wordLookahead);
    }

    /// <summary>
    /// Sets <see cref="LyricLine.InterludeProgress"/> on an active dots placeholder: how far
    /// the audible position is from its start to the next synced line's start (0..1). The
    /// raw position, not the word lookahead: the last dot fills as the voice comes back.
    /// </summary>
    private void DriveInterlude(LyricLine line, int index, TimeSpan position)
    {
        var p = InterludeProgressAt(line.Timestamp, NextSyncedLineStart(index), position);
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (line.InterludeProgress != p) line.InterludeProgress = p;
    }

    /// <summary>Progress through a gap from <paramref name="start"/> to
    /// <paramref name="end"/> at <paramref name="position"/>, clamped to 0..1; 0 when the
    /// gap is unknown or empty.</summary>
    public static double InterludeProgressAt(TimeSpan? start, TimeSpan? end, TimeSpan position)
    {
        if (start is not { } s || end is not { } e || e <= s) return 0;
        return Math.Clamp((position - s).TotalMilliseconds / (e - s).TotalMilliseconds, 0, 1);
    }

    /// <summary>
    /// Advances CurrentWordIndex on an active line when word-level timings are present.
    /// No-op when the line has no words — existing line-level highlight is all that renders.
    /// </summary>
    private void UpdateLineWords(LyricLine line, int index, TimeSpan position)
    {
        if (!line.HasWords && !line.HasBackgroundWords && !line.HasTransliterationWords) return;

        var adjusted = position + _wordLookahead;

        if (line.HasWords)
            DriveWordLayer(line, WordLayer.Main, line.Words!, adjusted, line.EndTimestamp, index);

        // Background vocals (adlibs) run as an independent layer with their own clock.
        if (line.HasBackgroundWords)
            DriveWordLayer(line, WordLayer.Background, line.BackgroundWords!, adjusted, line.BackgroundEndTimestamp, index);

        // Word-timed romanization (TTML transliteration) — its own clock, like the adlibs.
        if (line.HasTransliterationWords)
            DriveWordLayer(line, WordLayer.Transliteration, line.TransliterationWords!, adjusted, line.TransliterationEndTimestamp, index);
    }

    // Which word index a layer drives. A switch rather than a captured setter lambda:
    // this runs every rendered frame, and three closures per line per frame were garbage.
    private enum WordLayer { Main, Background, Transliteration }

    private static void SetWordIndex(LyricLine line, WordLayer layer, int target)
    {
        switch (layer)
        {
            case WordLayer.Main:
                if (line.CurrentWordIndex != target) line.CurrentWordIndex = target;
                break;
            case WordLayer.Background:
                if (line.BackgroundWordIndex != target) line.BackgroundWordIndex = target;
                break;
            default:
                if (line.TransliterationWordIndex != target) line.TransliterationWordIndex = target;
                break;
        }
    }

    // Held-note words whose line left the active set mid-animation. AMLL lets the letters
    // finish their swell after the line goes inactive; clearing them on deactivation
    // dropped a lifted, glowing word back to rest in one frame.
    private readonly List<WordTiming> _emphasisTail = new();

    /// <summary>True while a held-note animation is still playing out, or sung words are
    /// still settling, on a line that is no longer active — the frame clock must keep
    /// running for it.</summary>
    public bool HasEmphasisInFlight => _emphasisTail.Count > 0 || _floatTail.Count > 0;

    /// <summary>Sets <see cref="WordTiming.EmphasisMs"/> on the held-note words of a layer:
    /// the time since the word started while its letter animation is in flight, else NaN.</summary>
    private void DriveEmphasis(IReadOnlyList<WordTiming> words, TimeSpan adjusted)
    {
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (w.IsEmphasis) SetEmphasisClock(w, adjusted);
        }
    }

    /// <returns>True while the word's animation is still in flight.</returns>
    private bool SetEmphasisClock(WordTiming w, TimeSpan adjusted)
    {
        var rel = (adjusted - w.Start).TotalMilliseconds;
        var inFlight = _wordMotion && EmphasisMotion.IsInFlight(rel, w.EmphasisParams, w.EmphasisCharCount);
        var value = inFlight ? rel : double.NaN;
        if (!w.EmphasisMs.Equals(value)) w.EmphasisMs = value;
        return inFlight;
    }

    private void DriveEmphasisTail(TimeSpan adjusted)
    {
        for (int i = _emphasisTail.Count - 1; i >= 0; i--)
        {
            if (!SetEmphasisClock(_emphasisTail[i], adjusted))
                _emphasisTail.RemoveAt(i);
        }
    }

    private void HandOffEmphasis(IReadOnlyList<WordTiming>? words)
    {
        if (words == null) return;
        foreach (var w in words)
        {
            if (w.IsEmphasis && !double.IsNaN(w.EmphasisMs) && !_emphasisTail.Contains(w))
                _emphasisTail.Add(w);
        }
    }

    private void ClearEmphasisTail()
    {
        foreach (var w in _emphasisTail) w.EmphasisMs = double.NaN;
        _emphasisTail.Clear();
    }

    // Words of lines that left the active set while lifted, settling back to rest
    // (WordFloat.Fall from where each was). Structs in a reused list: no per-frame garbage.
    private readonly record struct FloatFall(WordTiming Word, double From, double StartMs);
    private readonly List<FloatFall> _floatTail = new();

    /// <summary>Sets <see cref="WordTiming.Lift"/> on every word of a driven layer: how far
    /// it has risen since it started (<see cref="WordFloat.Rise"/>), 0 before.</summary>
    private void DriveFloat(IReadOnlyList<WordTiming> words, TimeSpan adjusted, TimeSpan? layerEnd, int lineIndex)
    {
        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            double lift = 0;
            if (_wordMotion)
            {
                var since = (adjusted - w.Start).TotalMilliseconds;
                if (since > 0)
                    lift = WordFloat.Rise(since, (WordEnd(words, i, layerEnd, lineIndex) - w.Start).TotalMilliseconds);
            }
            SetLift(w, lift);
        }
    }

    private static void SetLift(WordTiming w, double lift)
    {
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (w.Lift != lift) w.Lift = lift;
    }

    private void HandOffFloat(IReadOnlyList<WordTiming>? words, double startMs)
    {
        if (words == null) return;
        foreach (var w in words)
        {
            if (w.Lift <= 0) continue;
            int at = -1;
            for (int i = 0; i < _floatTail.Count; i++)
                if (ReferenceEquals(_floatTail[i].Word, w)) { at = i; break; }
            var fall = new FloatFall(w, w.Lift, startMs);
            if (at >= 0) _floatTail[at] = fall;
            else _floatTail.Add(fall);
        }
    }

    private void DriveFloatTail(TimeSpan adjusted)
    {
        var now = adjusted.TotalMilliseconds;
        for (int i = _floatTail.Count - 1; i >= 0; i--)
        {
            var f = _floatTail[i];
            // A seek back to before the fall began restarts it from where the word is.
            if (now < f.StartMs) _floatTail[i] = f = f with { StartMs = now };
            var lift = WordFloat.Fall(f.From, now - f.StartMs);
            SetLift(f.Word, lift);
            if (lift <= 0) _floatTail.RemoveAt(i);
        }
    }

    private void ReclaimFloat(LyricLine line)
    {
        for (int i = _floatTail.Count - 1; i >= 0; i--)
        {
            var w = _floatTail[i].Word;
            if (Contains(line.Words, w) || Contains(line.BackgroundWords, w) || Contains(line.TransliterationWords, w))
                _floatTail.RemoveAt(i);
        }

        static bool Contains(IReadOnlyList<WordTiming>? words, WordTiming w)
        {
            if (words == null) return false;
            for (int i = 0; i < words.Count; i++)
                if (ReferenceEquals(words[i], w)) return true;
            return false;
        }
    }

    /// <summary>Every word back to rest: no lift, no held-note clock (track change,
    /// lyrics reload, motion turned off).</summary>
    private void ClearMotion()
    {
        ClearEmphasisTail();
        foreach (var f in _floatTail) SetLift(f.Word, 0);
        _floatTail.Clear();
        foreach (var line in _activeLines)
        {
            Rest(line.Words);
            Rest(line.BackgroundWords);
            Rest(line.TransliterationWords);
        }

        static void Rest(IReadOnlyList<WordTiming>? words)
        {
            if (words == null) return;
            foreach (var w in words)
            {
                SetLift(w, 0);
                if (!double.IsNaN(w.EmphasisMs)) w.EmphasisMs = double.NaN;
            }
        }
    }

    /// <summary>
    /// Advances one word layer: sets its current-word index and sweeps the words around it.
    ///
    /// The soft edge is ONE front moving through the line (AMLL's line mask animator): it
    /// crosses each word at that word's own pace, and the words either side of the current
    /// one take their progress from that same front (in characters), so where two words
    /// meet the edge straddles them in one place. Each word used to run its own clock past
    /// both ends instead, which split the edge in two whenever neighbours differ in pace —
    /// after a held word the next word was fully lit while the held word's last letter was
    /// still dim. Words next to a rest (or a line edge) are flagged
    /// <see cref="WordTiming.SweepLeadIn"/> / <see cref="WordTiming.SweepTrailOut"/>, so the
    /// renderer brings the edge in from dark and finishes it by the word's end there.
    /// </summary>
    private void DriveWordLayer(
        LyricLine line, WordLayer layer, IReadOnlyList<WordTiming> words, TimeSpan adjusted,
        TimeSpan? layerEnd, int lineIndex)
    {
        // Past the layer's end → last word remains highlighted until the line changes.
        int target;
        if (adjusted < words[0].Start)
        {
            target = -1;
        }
        else
        {
            target = words.Count - 1;
            for (int i = 0; i < words.Count; i++)
            {
                var w = words[i];
                var end = w.End ?? (i + 1 < words.Count ? words[i + 1].Start : TimeSpan.MaxValue);
                if (adjusted < end)
                {
                    target = i;
                    break;
                }
            }
        }

        // First: the index setter (LyricLine.ApplyWordIndex) snaps the words away from
        // the current one to the inert sentinels, so this frame's values go in after it.
        SetWordIndex(line, layer, target);
        DriveEmphasis(words, adjusted);
        DriveFloat(words, adjusted, layerEnd, lineIndex);

        if (target < 0)
        {
            // Before the layer's first word: all dark. (The first word is SweepLeadIn, so
            // its edge enters from dark at its start — no pre-roll ahead of the voice.)
            SetProgress(words[0], KaraokeSweep.InertFuture);
            return;
        }

        var cur = words[target];
        var curP = OwnProgress(words, target, adjusted, layerEnd, lineIndex);
        SetProgress(cur, curP);

        // Neighbours read the same front, in characters. Across a rest they don't:
        // the edge finished (or will start) inside each word's own window.
        var curLen = SweepLength(cur);
        if (target > 0)
        {
            var prev = words[target - 1];
            SetProgress(prev, cur.SweepLeadIn
                ? KaraokeSweep.InertPast
                : (curP * curLen + SweepLength(prev)) / SweepLength(prev));
        }
        if (target + 1 < words.Count)
        {
            var next = words[target + 1];
            SetProgress(next, cur.SweepTrailOut
                ? KaraokeSweep.InertFuture
                : (curP * curLen - curLen) / SweepLength(next));
        }

        static void SetProgress(WordTiming w, double p)
        {
            if (w.Progress != p) w.Progress = p;
        }
    }

    /// <summary>Visible characters the front crosses in a word (at least one).</summary>
    private static int SweepLength(WordTiming w) => Math.Max(1, w.SweepText.Trim().Length);

    /// <summary>
    /// The front's position inside word <paramref name="i"/>, 0..1: the elapsed fraction of
    /// its window, held at 0 in a rest before it and at 1 once it ends. Words joined from
    /// several timed spans follow their syllables' own clocks.
    /// </summary>
    private double OwnProgress(IReadOnlyList<WordTiming> words, int i, TimeSpan adjusted, TimeSpan? layerEnd, int lineIndex)
    {
        var w = words[i];
        var end = WordEnd(words, i, layerEnd, lineIndex);
        var t = adjusted.TotalSeconds;
        var s = w.Start.TotalSeconds;
        var e = end.TotalSeconds;
        if (t <= s) return 0;
        if (t >= e) return 1;
        return w.Syllables is { Count: > 1 } syllables
            ? KaraokeSweep.SyllableBandProgress(syllables, s, e, t)
            : KaraokeSweep.WordProgress(s, e, t);
    }

    /// <summary>Where word <paramref name="i"/> of a layer ends: its own end, else the next
    /// word's start. The last word of a start-tag-only layer has no end anywhere — it is
    /// bounded by the next line's start (capped) so it sweeps instead of snapping to lit.</summary>
    private TimeSpan WordEnd(IReadOnlyList<WordTiming> words, int i, TimeSpan? layerEnd, int lineIndex)
    {
        var w = words[i];
        return w.End ?? (i + 1 < words.Count
            ? words[i + 1].Start
            : layerEnd ?? KaraokeSweep.ResolveOpenLastWordEnd(w.Start, NextSyncedLineStart(lineIndex)));
    }

    /// <summary>Start of the first synced line after line <paramref name="index"/>; null when none.</summary>
    private TimeSpan? NextSyncedLineStart(int index)
    {
        if (index < 0) return null;
        for (int i = index + 1; i < _lines.Count; i++)
        {
            var ts = _lines[i].Timestamp;
            if (ts.HasValue) return ts;
        }
        return null;
    }
}
