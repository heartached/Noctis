using Noctis.Models;

namespace Noctis.Services;

/// <summary>Result of one <see cref="LyricsTimeline.Update"/>.</summary>
/// <param name="LineChanged">True when the active line is a different line than before the call.</param>
/// <param name="ActiveIndex">Index of the active line in the timeline's list, or -1.</param>
/// <param name="ActiveLine">The active line, or null before the first synced line.</param>
public readonly record struct LyricsTimelineStep(bool LineChanged, int ActiveIndex, LyricLine? ActiveLine);

/// <summary>
/// The synced-lyrics engine, UI-free: walks a monotonic line cursor per position
/// sample (O(1) amortised, rewinds on seek), flips <see cref="LyricLine.IsActive"/>
/// on the outgoing/incoming lines, and drives the word layers of the active line
/// (<see cref="LyricLine.CurrentWordIndex"/>, <see cref="LyricLine.BackgroundWordIndex"/>,
/// <see cref="LyricLine.TransliterationWordIndex"/>, <see cref="WordTiming.Progress"/>).
/// Lifted verbatim from the desktop LyricsViewModel so the Android lyrics page and the
/// desktop page share one clock.
/// The caller owns the line list and feeds the audible position (engine position
/// minus output latency, smoothed by <see cref="LyricsPlaybackClock"/>).
/// </summary>
public sealed class LyricsTimeline
{
    private readonly IReadOnlyList<LyricLine> _lines;
    private readonly TimeSpan _wordLookahead;
    private readonly TimeSpan _lineLookahead;

    private int _cursor;
    private TimeSpan _lastPosition = TimeSpan.MinValue;

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

    /// <summary>The active line, or null.</summary>
    public LyricLine? ActiveLine { get; private set; }

    /// <summary>Index of <see cref="ActiveLine"/> in the list, or -1.</summary>
    public int ActiveIndex { get; private set; } = -1;

    /// <summary>Forget the cursor and the active line (track change, lyrics reload).
    /// Deactivates the line it was holding.</summary>
    public void Reset()
    {
        if (ActiveLine is { } l) l.IsActive = false;
        ActiveLine = null;
        ActiveIndex = -1;
        Rewind();
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
        if (_lines.Count == 0) return new(false, ActiveIndex, ActiveLine);

        // Seek-backwards detection: rewind the cursor so we don't miss earlier lines.
        // 750ms threshold tolerates small non-monotonic jitter from the player position poll.
        if (_lastPosition != TimeSpan.MinValue &&
            position + TimeSpan.FromMilliseconds(750) < _lastPosition)
        {
            _cursor = 0;
        }
        _lastPosition = position;

        TimeSpan AdjustedFor(LyricLine l) =>
            position + (l.HasWords || l.HasBackgroundWords || l.HasTransliterationWords ? _wordLookahead : _lineLookahead);

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
        var rewound = false;
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
            UpdateActiveWord(position);
            return new(false, ActiveIndex, ActiveLine);
        }

        var changed = false;
        if (bestMatch != ActiveLine)
        {
            // Deactivate previous line — leave it fully swept (index past the end) so the
            // bright overlay keeps covering the words while the base layer fades back to
            // full opacity. Snapping to -1 here blanked the overlay instantly, which read
            // as the finished line dimming for a beat. Re-entry recomputes the real index.
            if (ActiveLine != null)
            {
                ActiveLine.IsActive = false;
                if (ActiveLine.HasWords)
                    ActiveLine.CurrentWordIndex = ActiveLine.Words!.Count;
                if (ActiveLine.HasBackgroundWords)
                    ActiveLine.BackgroundWordIndex = ActiveLine.BackgroundWords!.Count;
                if (ActiveLine.HasTransliterationWords)
                    ActiveLine.TransliterationWordIndex = ActiveLine.TransliterationWords!.Count;
            }

            // Activate new line
            if (bestMatch != null)
                bestMatch.IsActive = true;

            ActiveLine = bestMatch;
            ActiveIndex = bestIndex;
            changed = true;
        }
        else if (bestMatch != null && !bestMatch.IsActive)
        {
            // Safety: ensure the active line stays active even if something reset it
            bestMatch.IsActive = true;
        }

        UpdateActiveWord(position);
        return new(changed, ActiveIndex, ActiveLine);
    }

    /// <summary>
    /// Advances CurrentWordIndex on the active line when word-level timings are present.
    /// No-op when the active line has no words — existing line-level highlight is all that renders.
    /// </summary>
    private void UpdateActiveWord(TimeSpan position)
    {
        var line = ActiveLine;
        if (line == null || (!line.HasWords && !line.HasBackgroundWords && !line.HasTransliterationWords)) return;

        var adjusted = position + _wordLookahead;

        if (line.HasWords)
            DriveWordLayer(line.Words!, adjusted, line.EndTimestamp,
                line.CurrentWordIndex, i => line.CurrentWordIndex = i);

        // Background vocals (adlibs) run as an independent layer with their own clock.
        if (line.HasBackgroundWords)
            DriveWordLayer(line.BackgroundWords!, adjusted, line.BackgroundEndTimestamp,
                line.BackgroundWordIndex, i => line.BackgroundWordIndex = i);

        // Word-timed romanization (TTML transliteration) — its own clock, like the adlibs.
        if (line.HasTransliterationWords)
            DriveWordLayer(line.TransliterationWords!, adjusted, line.TransliterationEndTimestamp,
                line.TransliterationWordIndex, i => line.TransliterationWordIndex = i);
    }

    /// <summary>Advances one word layer's current-word index and sweeps the active word.</summary>
    private void DriveWordLayer(
        IReadOnlyList<WordTiming> words, TimeSpan adjusted, TimeSpan? layerEnd,
        int currentIndex, Action<int> setIndex)
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

        if (currentIndex != target)
            setIndex(target);

        // Drive the AMLL-style sweep on the current word AND its immediate
        // neighbours, on the same lookahead-adjusted clock as the index above.
        // BandProgress keeps moving a little past both ends of each word, so the
        // feathered edge finishes crossing the previous token while it is already
        // entering the next — clamping at [0,1] here is what used to park the band
        // at every token boundary of a slow passage. Before the line starts
        // (target -1) this pre-rolls word 0; past the layer end the last word
        // settles at the inert-past sentinel (fully lit) until the line changes.
        var first = Math.Max(0, target - 1);
        var last = Math.Min(words.Count - 1, target + 1);
        for (int i = first; i <= last; i++)
        {
            var w = words[i];
            // Last word of a start-tag-only layer has no end anywhere — bound it by
            // the next line's start (capped) so it sweeps instead of snapping to lit.
            var end = w.End ?? (i + 1 < words.Count
                ? words[i + 1].Start
                : layerEnd ?? KaraokeSweep.ResolveOpenLastWordEnd(w.Start, NextSyncedLineStart()));
            // Words joined from several timed spans sweep on their syllables' own
            // clocks — a linear ramp would outrun the voice on a held syllable.
            var progress = w.Syllables is { Count: > 1 } syllables
                ? KaraokeSweep.SyllableBandProgress(
                    syllables, w.Start.TotalSeconds, end.TotalSeconds, adjusted.TotalSeconds)
                : KaraokeSweep.BandProgress(
                    w.Start.TotalSeconds, end.TotalSeconds, adjusted.TotalSeconds);
            if (w.Progress != progress)
                w.Progress = progress;
        }
    }

    /// <summary>Start of the first synced line after the active one; null when none.</summary>
    private TimeSpan? NextSyncedLineStart()
    {
        if (ActiveIndex < 0) return null;
        for (int i = ActiveIndex + 1; i < _lines.Count; i++)
        {
            var ts = _lines[i].Timestamp;
            if (ts.HasValue) return ts;
        }
        return null;
    }
}
