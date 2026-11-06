using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Services.LyricsPage;

/// <summary>
/// The lyrics page's list motion on the Lyrics Page video's virtual clock: the same
/// <see cref="LyricsTimeline"/> picks the line the list glides to (<see cref="LineMotion.ScrollLeadMs"/>
/// ahead of a word-timed line once the line before has been sung), the glide runs on the
/// page's spring for the line's tempo (<see cref="LineMotion.ForHold"/>), and lines below the
/// target cascade behind it (<see cref="LineMotion.CascadeDelayPerLineMs"/> per line, capped at
/// 8 lines, clamped to 16 px steps — LyricsView.AnimateScroll). Each line's opacity, blur and
/// karaoke look follow the page's focus ramp, and the active dots line breathes.
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

    /// <summary>Line opacity by distance to the nearest lit line (LyricsViewModel); 0 from 10 on.</summary>
    public static readonly double[] OpacityByDistance = { 1.0, 0.55, 0.32, 0.18, 0.12, 0.08, 0.06, 0.04, 0.03, 0.02 };

    /// <summary>Line blur radius (page px at a 46 px font) by distance; the last entry from 4 on.</summary>
    public static readonly double[] BlurByDistance = { 0, 4, 6, 8, 10 };

    /// <summary>The dots' breathing: down to this scale and back (LyricsView.axaml's
    /// 2.1 s alternate SineEaseInOut animation).</summary>
    private const double DotsMinScale = 0.88;
    private const double DotsBreathSeconds = 2.1;

    private static readonly TimeSpan ScrollLead = TimeSpan.FromMilliseconds(LineMotion.ScrollLeadMs);

    private readonly IReadOnlyList<LyricLine> _lines;
    private readonly IReadOnlyList<float> _tops;
    private readonly IReadOnlyList<float> _heights;
    private readonly float _anchorY;
    private readonly float _viewportHeight;
    private readonly float _scale;
    private readonly float _listTop;
    private readonly float _pixelScale;
    private readonly double[] _lags;

    private readonly Fade _opacity, _blur, _sweep;

    private LineGlide? _glide;
    private double _restOffset;
    private double _lastT = double.NaN;
    private int _dotsIndex = -1;
    private double _dotsStart;

    /// <param name="listTop">Where the list's viewport starts on the canvas (lines draw at
    /// listTop + top − offset).</param>
    /// <param name="pixelScale">Device pixels per canvas px. When set, each target offset is
    /// rounded so its line rests on a whole device pixel: the glide itself lands there, with no
    /// sub-pixel tick after it. 0 leaves the offsets unrounded.</param>
    public LyricsPageMotion(IReadOnlyList<LyricLine> lines, IReadOnlyList<float> lineTops,
        IReadOnlyList<float> lineHeights, float anchorY, float viewportHeight, float scale,
        float listTop = 0, float pixelScale = 0)
    {
        _listTop = listTop;
        _pixelScale = pixelScale;
        _lines = lines;
        _tops = lineTops;
        _heights = lineHeights;
        _anchorY = anchorY;
        _viewportHeight = viewportHeight;
        _scale = scale;
        _lags = new double[lines.Count];
        _opacity = new Fade(lines.Count);
        _blur = new Fade(lines.Count);
        _sweep = new Fade(lines.Count);
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

        Refocus(tSeconds, snap);
        TrackDots(tSeconds, snap);
    }

    /// <summary>Line <paramref name="line"/>'s opacity at <paramref name="tSeconds"/> (after <see cref="Advance"/>).</summary>
    public double Opacity(int line, double tSeconds) => _opacity.At(line, tSeconds);

    /// <summary>Line <paramref name="line"/>'s blur radius at <paramref name="tSeconds"/>, in canvas px
    /// (the page's px × scale).</summary>
    public double BlurRadius(int line, double tSeconds) => _blur.At(line, tSeconds) * _scale;

    /// <summary>How far line <paramref name="line"/>'s words show the karaoke look (dim unsung text,
    /// the sweep lit on top): 0 = the inactive look, 1 = the karaoke look.</summary>
    public double SweepFocus(int line, double tSeconds) => _sweep.At(line, tSeconds);

    /// <summary>The breathing scale of line <paramref name="line"/>'s dots: 1 unless it is the active
    /// dots line, which breathes 1 → 0.88 → 1 every 4.2 s from the moment it became active.</summary>
    public double DotsScale(int line, double tSeconds)
    {
        if (line < 0 || line != _dotsIndex) return 1;
        var u = Math.Max(0, tSeconds - _dotsStart) / DotsBreathSeconds;
        var phase = u % 2;
        var x = phase <= 1 ? phase : 2 - phase;     // triangle wave 0 → 1 → 0
        var s = (1 - Math.Cos(Math.PI * x)) / 2;    // sine in-out along it
        return 1 - (1 - DotsMinScale) * s;
    }

    /// <summary>
    /// Retargets every line's opacity, blur and sweep focus as the page's focus ramp does
    /// (LyricsViewModel): distance to the nearest lit line, or — while the list leads into the
    /// next line — to that line alone. Each eases over the page's 0.5 s cubic in-out. The page
    /// swaps its BlurEffect radius instantly; here the blur eases with the opacity on purpose,
    /// so a line change never pops in the video (spec anti-goal: no stutter at a line change).
    /// </summary>
    private void Refocus(double tSeconds, bool snap)
    {
        var primary = Timeline.ActiveIndex;
        IReadOnlyList<int> lit = Timeline.ActiveIndices;
        if (ScrollIndex == primary + 1)
        {
            primary = ScrollIndex;
            lit = Array.Empty<int>();
        }
        for (int i = 0; i < _lines.Count; i++)
        {
            var dist = LyricsTimeline.DistanceToNearestActive(i, primary, lit);
            _opacity.Retarget(i, dist < OpacityByDistance.Length ? OpacityByDistance[dist] : 0, tSeconds, snap);
            _blur.Retarget(i, BlurByDistance[Math.Min(dist, BlurByDistance.Length - 1)], tSeconds, snap);
            _sweep.Retarget(i, _lines[i].IsSweepFocused ? 1 : 0, tSeconds, snap);
        }
    }

    /// <summary>Notes when a dots line becomes the active line, the breathing's phase zero. After a
    /// snap that is when the dots would have activated (their start − the line lead), so a seek
    /// lands on the same phase as playing through.</summary>
    private void TrackDots(double tSeconds, bool snap)
    {
        var dots = Timeline.ActiveLine is { IsIntroPlaceholder: true } ? Timeline.ActiveIndex : -1;
        if (!snap && dots == _dotsIndex) return;
        _dotsIndex = dots;
        if (dots < 0) return;
        _dotsStart = snap && _lines[dots].Timestamp is { } ts
            ? ts.TotalSeconds - LineMotion.HalfTravelMs / 1000.0
            : tSeconds;
    }

    /// <summary>The scroll offset that puts line <paramref name="index"/>'s centre on the anchor.</summary>
    public double TargetOffset(int index)
    {
        if (_tops.Count == 0) return -_anchorY;
        index = Math.Clamp(index, 0, _tops.Count - 1);
        double offset = _tops[index] + _heights[index] / 2.0 - _anchorY;
        if (_pixelScale <= 0) return offset;
        double y = _listTop + _tops[index] - offset;
        return _listTop + _tops[index] - Math.Round(y * _pixelScale) / _pixelScale;
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

    /// <summary>Per-line values easing toward their targets on the page's 0.5 s cubic in-out
    /// (<see cref="KaraokeFlow.FocusFadeSeconds"/>, as <see cref="KaraokeFlow.FocusAt"/>).</summary>
    private sealed class Fade(int count)
    {
        private readonly double[] _from = new double[count], _to = new double[count], _start = new double[count];

        public void Retarget(int i, double target, double tSeconds, bool snap)
        {
            if (snap)
            {
                _from[i] = _to[i] = target;
                _start[i] = tSeconds;
            }
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            else if (_to[i] != target)
            {
                _from[i] = At(i, tSeconds);
                _to[i] = target;
                _start[i] = tSeconds;
            }
        }

        public double At(int i, double tSeconds)
        {
            if (i < 0 || i >= _to.Length) return 0;
            var x = (tSeconds - _start[i]) / KaraokeFlow.FocusFadeSeconds;
            return x >= 1 ? _to[i] : _from[i] + (_to[i] - _from[i]) * KaraokeFlow.EaseCubicInOut(x);
        }
    }
}
