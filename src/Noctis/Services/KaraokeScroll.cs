using Noctis.Helpers;

namespace Noctis.Services;

/// <summary>
/// The scroll of the Share Lyrics karaoke card while it follows playback (flow mode): the
/// whole song is one list and the line the lyrics page would be on glides to the card's
/// anchor, on the page's glide — a <see cref="LineGlide"/> on the spring
/// <see cref="LineMotion.ForHold"/> picks for that line's tempo, velocity carried when a
/// new line comes in mid-glide. The target line comes from <see cref="KaraokeFlow.ScrollLine"/>
/// (the page's <c>LyricsTimeline.Lead</c> with <see cref="LineMotion.ScrollLeadMs"/>).
///
/// Runs on track time, like <see cref="KaraokeFlow"/>, so the preview and the exported clip
/// move alike; feed it times in playback order. The first frame and a seek (a jump back,
/// or forward by more than a frame gap can explain) land on the target at once, as the
/// page re-anchors a seek instead of gliding through every line in between.
/// </summary>
public sealed class KaraokeScroll
{
    /// <summary>A jump back by more than this is a seek (<see cref="KaraokeFlow"/>'s value).</summary>
    public const double SeekBackSeconds = 0.75;

    /// <summary>A jump forward by more than this is a seek: the preview ticks every ~30 ms
    /// and the clip every 1/60 s, so only a seek opens a gap this wide.</summary>
    public const double SeekForwardSeconds = 1.5;

    private readonly double[] _targets;
    private readonly Func<int, double> _holdMs;
    private LineGlide? _glide;
    private int _line = -1;
    private double _offset;
    private double _lastT = double.NaN;

    /// <param name="targets">Per card line, the scroll offset that puts it on the anchor.</param>
    /// <param name="holdMs">Per card line, how long it stays the target (the spring's tempo;
    /// NaN = the base spring).</param>
    public KaraokeScroll(IReadOnlyList<double> targets, Func<int, double> holdMs)
    {
        _targets = targets.ToArray();
        _holdMs = holdMs;
    }

    /// <summary>The line the scroll is on (or heading to); -1 before the first frame.</summary>
    public int Line => _line;

    /// <summary>True while a glide is in flight (as of the last <see cref="Offset"/>).</summary>
    public bool IsGliding => _glide != null;

    /// <summary>The settled offset of card line <paramref name="line"/>.</summary>
    public double Target(int line) => line >= 0 && line < _targets.Length ? _targets[line] : 0;

    /// <summary>The scroll offset at track time <paramref name="tSeconds"/> with the list
    /// heading for card line <paramref name="line"/> (out of range: stays on its line).</summary>
    public double Offset(int line, double tSeconds)
    {
        var snap = double.IsNaN(_lastT)
                   || tSeconds < _lastT - SeekBackSeconds
                   || tSeconds > _lastT + SeekForwardSeconds;
        _lastT = tSeconds;
        var nowMs = tSeconds * 1000;
        if (line < 0 || line >= _targets.Length) line = _line;

        if (snap)
        {
            _glide = null;
            _line = line;
            _offset = Target(line);
            return _offset;
        }

        if (line != _line)
        {
            var from = _glide is { } g && !g.IsDone(nowMs) ? g.Offset(nowMs) : _offset;
            _offset = Target(line);
            _glide = LineGlide.Begin(_glide, nowMs, from, _offset, LineMotion.ForHold(_holdMs(line)),
                0, 0, 0, 0, _ => 0);
            _line = line;
        }

        if (_glide == null) return _offset;
        if (_glide.IsDone(nowMs))
        {
            _glide = null;
            return _offset;
        }
        return _glide.Offset(nowMs);
    }
}
