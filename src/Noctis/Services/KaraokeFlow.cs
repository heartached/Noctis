using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// The lyrics page's motion for the Share Lyrics karaoke card (live preview and Save Video):
/// the card's lines run through the same <see cref="LyricsTimeline"/> the page uses, so the
/// sweep (one soft front per line, lead-in/trail-out at rests), the sung-word float and the
/// held-note clock are the page's, frame for frame. On top, each line focuses as the page's
/// list would bring it to the anchor — <see cref="LineMotion.ScrollLeadMs"/> ahead of a
/// word-timed line once the line before has finished — with the page's opacity transition
/// (0.5 s cubic in-out). The card draws no line motion (owner 10-25): no scale, no float.
///
/// Stateful like the page (the timeline walks a cursor and plays word settles out), so feed
/// it times in playback order; a jump back re-resolves as a seek does on the page.
/// </summary>
public sealed class KaraokeFlow
{
    /// <summary>Opacity of a line that is not the focus (sung or still to come).</summary>
    public const double InactiveOpacity = 0.5;

    /// <summary>Opacity of unsung words on the focused line (the page's word base 0.45).</summary>
    public const double UnsungOpacity = 0.45;

    /// <summary>Line opacity transition (the page's DoubleTransition, 0.5 s cubic in-out).</summary>
    public const double FocusFadeSeconds = 0.5;

    private readonly LyricLine?[] _lines;      // per card line; null = unsynced (always lit)
    private readonly int[] _timelineIndex;     // card line → index in _synced, or -1
    private readonly List<LyricLine> _synced = new();
    private readonly LyricsTimeline _timeline;
    private readonly double[] _focusFrom, _focusTo, _focusStart;
    private double _lastT = double.NaN;

    private static readonly TimeSpan ScrollLead = TimeSpan.FromMilliseconds(LineMotion.ScrollLeadMs);

    /// <param name="karaoke">Card lines, in song order (parallel to the card's lyric lines).</param>
    public KaraokeFlow(IReadOnlyList<KaraokeLine> karaoke)
    {
        _lines = new LyricLine?[karaoke.Count];
        _timelineIndex = new int[karaoke.Count];
        _focusFrom = new double[karaoke.Count];
        _focusTo = new double[karaoke.Count];
        _focusStart = new double[karaoke.Count];
        for (int i = 0; i < karaoke.Count; i++)
        {
            var k = karaoke[i];
            _timelineIndex[i] = -1;
            if (k.StartSeconds is not { } start) continue;
            var line = new LyricLine { Timestamp = TimeSpan.FromSeconds(start) };
            if (k.Words is { Count: > 0 } words)
            {
                // The end first: Words' setter derives the held-note and sweep-edge flags from it.
                line.EndTimestamp = TimeSpan.FromSeconds(words[^1].EndSeconds);
                line.Words = words.Select((w, j) => new WordTiming
                {
                    Text = j + 1 < words.Count ? w.Token + " " : w.Token,
                    Start = TimeSpan.FromSeconds(w.StartSeconds),
                    End = TimeSpan.FromSeconds(Math.Max(w.StartSeconds, w.EndSeconds)),
                }).ToList();
                line.Text = string.Join(' ', words.Select(w => w.Token));
            }
            _lines[i] = line;
            _timelineIndex[i] = _synced.Count;
            _synced.Add(line);
        }
        // The card's times already carry the page's 80 ms word lookahead (the caller adds
        // it), so the timeline runs without one; plain lines lead by half the line glide,
        // as on the page.
        _timeline = new LyricsTimeline(_synced, TimeSpan.Zero, TimeSpan.FromMilliseconds(LineMotion.HalfTravelMs));
    }

    /// <summary>Card line <paramref name="line"/>'s engine line (its <see cref="LyricLine.Words"/>
    /// carry the frame's Progress/Lift/EmphasisMs), or null when it is unsynced.</summary>
    public LyricLine? Line(int line) => line >= 0 && line < _lines.Length ? _lines[line] : null;

    /// <summary>Moves every line and word to track time <paramref name="tSeconds"/>.</summary>
    public void Advance(double tSeconds)
    {
        // The first frame, or a jump back (a seek in the preview): lines take their focus at
        // once instead of fading in from wherever the last frame left them.
        var snap = double.IsNaN(_lastT) || tSeconds < _lastT - 0.75;
        _lastT = tSeconds;
        var pos = TimeSpan.FromSeconds(Math.Max(0, tSeconds));
        _timeline.Update(pos);
        var scroll = _timeline.Lead(pos, ScrollLead);

        // Focus as the page's opacity ramp: the lit lines and the line the list is on, or —
        // while the list leads into the next line — that line alone.
        var lit = _timeline.ActiveIndices;
        var leading = scroll >= 0 && scroll == _timeline.ActiveIndex + 1;
        // Before the first line the page sits on its intro dots and leads out of them; the
        // card has none, so its first line takes the focus on the same lead.
        if (_timeline.ActiveIndex < 0 && _synced.Count > 0 && _synced[0].Timestamp - pos <= ScrollLead)
        {
            scroll = 0;
            leading = true;
        }
        for (int i = 0; i < _lines.Length; i++)
        {
            var ti = _timelineIndex[i];
            double target = _lines[i] == null ? 1
                : leading ? (ti == scroll ? 1 : 0)
                : Contains(lit, ti) || ti == scroll && scroll >= 0 ? 1 : 0;

            if (snap)
            {
                _focusFrom[i] = _focusTo[i] = target;
                _focusStart[i] = tSeconds;
            }
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            else if (_focusTo[i] != target)
            {
                _focusFrom[i] = FocusAt(i, tSeconds);
                _focusTo[i] = target;
                _focusStart[i] = tSeconds;
            }
        }

        static bool Contains(IReadOnlyList<int> list, int v)
        {
            for (int i = 0; i < list.Count; i++) if (list[i] == v) return true;
            return false;
        }
    }

    /// <summary>How focused card line <paramref name="line"/> is at <paramref name="tSeconds"/>
    /// (0 = inactive, 1 = the anchor line), on the page's 0.5 s cubic in-out.</summary>
    public double FocusAt(int line, double tSeconds)
    {
        var x = Math.Clamp((tSeconds - _focusStart[line]) / FocusFadeSeconds, 0, 1);
        return _focusFrom[line] + (_focusTo[line] - _focusFrom[line]) * EaseCubicInOut(x);
    }

    /// <summary>The page's line transition curve (cubic in-out) at progress <paramref name="x"/>,
    /// clamped to 0..1.</summary>
    public static double EaseCubicInOut(double x)
    {
        x = Math.Clamp(x, 0, 1);
        return x < 0.5 ? 4 * x * x * x : 1 - Math.Pow(-2 * x + 2, 3) / 2;
    }

    /// <summary>Line opacity at <paramref name="tSeconds"/> (after <see cref="Advance"/>).</summary>
    public double LineOpacity(int line, double tSeconds) =>
        InactiveOpacity + (1 - InactiveOpacity) * FocusAt(line, tSeconds);
}
