using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// The lyrics page's list motion on the Lyrics Page video's virtual clock: the same
/// <see cref="LyricsTimeline"/> picks the line the list glides to (<see cref="LineMotion.ScrollLeadMs"/>
/// ahead of a word-timed line once the line before has been sung), the glide runs on the
/// page's spring for the line's tempo (<see cref="LineMotion.ForHold"/>), and lines below the
/// target cascade behind it (<see cref="LineMotion.CascadeDelayPerLineMs"/> per line, capped at
/// 8 lines, clamped to 16 px steps — LyricsView.AnimateScroll).
///
/// Stateful like the page: feed it times in playback order. The first call, or a jump back
/// of more than 0.75 s, snaps to the line instead of gliding, so a given frame time always
/// resolves to the same picture after a seek. Times are track seconds that already carry the
/// 80 ms word lookahead (<see cref="LyricsPageFrames.TrackTime"/>). Allocates per glide only.
/// </summary>
public sealed class LyricsPageMotion
{
    private const int CascadeMaxLines = 8;
    private const double MaxCascadeLagStepPx = 16;

    private static readonly TimeSpan ScrollLead = TimeSpan.FromMilliseconds(LineMotion.ScrollLeadMs);

    private readonly IReadOnlyList<LyricLine> _lines;
    private readonly IReadOnlyList<float> _tops;
    private readonly IReadOnlyList<float> _heights;
    private readonly float _anchorY;
    private readonly float _viewportHeight;
    private readonly float _scale;
    private readonly double[] _lags;

    private LineGlide? _glide;
    private double _restOffset;
    private double _lastT = double.NaN;

    public LyricsPageMotion(IReadOnlyList<LyricLine> lines, IReadOnlyList<float> lineTops,
        IReadOnlyList<float> lineHeights, float anchorY, float viewportHeight, float scale)
    {
        _lines = lines;
        _tops = lineTops;
        _heights = lineHeights;
        _anchorY = anchorY;
        _viewportHeight = viewportHeight;
        _scale = scale;
        _lags = new double[lines.Count];
        // The video's times already carry the 80 ms word lookahead, so the timeline adds none;
        // plain lines lead by half the line glide, as on the page (see KaraokeFlow).
        Timeline = new LyricsTimeline(lines, TimeSpan.Zero, TimeSpan.FromMilliseconds(LineMotion.HalfTravelMs));
    }

    /// <summary>The engine timeline the lines run on (active line, lit set, word clocks).</summary>
    public LyricsTimeline Timeline { get; }

    /// <summary>The line the list is on or gliding to.</summary>
    public int ScrollIndex { get; private set; }

    /// <summary>Moves the timeline and the list to track time <paramref name="tSeconds"/>.</summary>
    public void Advance(double tSeconds)
    {
        var snap = double.IsNaN(_lastT) || tSeconds < _lastT - 0.75;
        _lastT = tSeconds;
        var pos = TimeSpan.FromSeconds(Math.Max(0, tSeconds));
        Timeline.Update(pos);
        var scroll = Timeline.Lead(pos, ScrollLead);
        if (scroll < 0) scroll = FirstLineFrom(pos);

        if (snap)
        {
            _glide = null;
            _restOffset = TargetOffset(scroll);
            ScrollIndex = scroll;
        }
        else if (scroll != ScrollIndex)
        {
            var from = ScrollOffset(tSeconds);
            var to = TargetOffset(scroll);
            // What is on screen now (the old glide's chained lags) is where the new glide starts.
            LineLags(tSeconds);
            var seekSized = Math.Abs(to - from) > 1.5 * _viewportHeight;
            _glide = LineGlide.Begin(_glide, tSeconds * 1000, from, to,
                LineMotion.ForHold(LyricsTimeline.HoldMs(_lines, scroll)), _lines.Count, scroll,
                seekSized ? 0 : LineMotion.CascadeDelayPerLineMs, CascadeMaxLines, i => _lags[i]);
            ScrollIndex = scroll;
        }
    }

    /// <summary>The scroll offset that puts line <paramref name="index"/>'s centre on the anchor.</summary>
    public double TargetOffset(int index)
    {
        if (_tops.Count == 0) return -_anchorY;
        index = Math.Clamp(index, 0, _tops.Count - 1);
        return _tops[index] + _heights[index] / 2.0 - _anchorY;
    }

    /// <summary>The list's scroll offset at <paramref name="tSeconds"/> (after <see cref="Advance"/>):
    /// a line is drawn at its top − this + its <see cref="LineLags"/> entry.</summary>
    public double ScrollOffset(double tSeconds) => _glide?.Offset(tSeconds * 1000) ?? _restOffset;

    /// <summary>
    /// Each line's cascade translate at <paramref name="tSeconds"/> (positive = still behind the
    /// list, 0 once landed). Lines below the scroll target are chained so a line never trails the
    /// one above by more than the page's clamp (LineMotion.CascadeStep, 16 px at a 46 px font).
    /// The same array every call: copy it to keep a snapshot.
    /// </summary>
    public IReadOnlyList<double> LineLags(double tSeconds)
    {
        var ms = tSeconds * 1000;
        var prevLag = 0.0;
        for (int i = 0; i < _lags.Length; i++)
        {
            var lag = _glide?.LineLag(i, ms) ?? 0;
            if (i > ScrollIndex)
            {
                lag = LineMotion.CascadeStep(lag, prevLag, MaxCascadeLagStepPx * _scale);
                prevLag = lag;
            }
            _lags[i] = lag;
        }
        return _lags;
    }

    /// <summary>Before the first line: the first line still to come, else 0.</summary>
    private int FirstLineFrom(TimeSpan pos)
    {
        for (int i = 0; i < _lines.Count; i++)
            if (_lines[i].Timestamp is { } ts && ts >= pos) return i;
        return 0;
    }
}
