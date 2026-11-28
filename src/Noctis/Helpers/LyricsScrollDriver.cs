using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Helpers;

/// <summary>
/// The synced-lyrics scroll engine shared by the Lyrics page and the mini player's lyrics
/// panel (owner 11-27: "lyrics flowing should be the same as the lyrics page"). Moved out
/// of LyricsView unchanged:
/// <list type="bullet">
/// <item>a line advance glides on a tempo spring (<see cref="LineMotion.ForHold"/> of the
/// line's hold) with the lines below the active one setting off a stagger later each
/// (<see cref="LineGlide"/> cascade, capped per-step lag), handing velocities on;</item>
/// <item>line changes arriving on top of each other (a scrub) or right after a committed
/// seek retarget an exponential chase instead;</item>
/// <item>any offset this driver did not write (wheel, scrollbar drag, touch pan, keys)
/// pauses auto-follow (<see cref="LyricsViewModel.IsAutoFollowPaused"/>), which resumes
/// after <see cref="AutoFollowResumeDelay"/> when Settings ▸ "Return to the current line
/// automatically" is on, or from the Follow pill / a seek.</item>
/// </list>
/// The host supplies the scroller, the line list, where the list starts in extent space and
/// the anchor ratio; everything else is identical for both surfaces.
/// </summary>
internal sealed class LyricsScrollDriver
{
    // ── Shared tuning (single source for every lyrics surface that uses this driver) ──

    /// <summary>Updates closer together than this are a drag: chase, not glide.</summary>
    public const double ScrubWindowMs = 250;
    /// <summary>Chase time to ~99% of a stationary target.</summary>
    public const double ScrubSettleMs = 380;
    /// <summary>A line change this soon after a committed seek is that seek resolving: chase.
    /// Covers the commit debounce (60ms, posted) plus a 100ms sync tick, with margin.</summary>
    public const double SeekFollowWindowMs = 400;
    /// <summary>Each line below the active one starts its glide this much later.</summary>
    public const double CascadeDelayPerLineMs = LineMotion.CascadeDelayPerLineMs;
    /// <summary>Stagger depth cap (only the DELAY is capped; every line takes part).</summary>
    public const int CascadeMaxLines = 8;
    /// <summary>A line's lag may exceed the line above's by at most this much (no crossing).</summary>
    public const double MaxCascadeLagStepPx = 16;
    /// <summary>Scrolled away by hand: back to the sung line after this (when enabled).</summary>
    public static readonly TimeSpan AutoFollowResumeDelay = TimeSpan.FromSeconds(5);

    /// <summary>Anchor of the active line in extent space with the page's padding
    /// (<see cref="TopPadRatio"/> + the panel's 0.22).</summary>
    public const double PageAnchorRatio = 0.32;
    /// <summary>Run-out padding above the first line, as a share of the viewport.</summary>
    public const double TopPadRatio = 0.10;
    /// <summary>Run-out padding below the last line, as a share of the viewport.</summary>
    public const double BottomPadRatio = 0.78;

    /// <summary>How a line change moves the list.</summary>
    public enum Route { Glide, Chase }

    /// <summary>The route for a line change <paramref name="sinceLastMs"/> after the previous
    /// one and <paramref name="sinceSeekMs"/> after the last committed seek.</summary>
    public static Route ChooseRoute(double sinceLastMs, double sinceSeekMs) =>
        sinceLastMs < ScrubWindowMs || sinceSeekMs < SeekFollowWindowMs ? Route.Chase : Route.Glide;

    /// <summary>The glide spring for line <paramref name="index"/>: follows its hold.</summary>
    public static LineSpring SpringFor(IReadOnlyList<LyricLine> lines, int index) =>
        LineMotion.ForHold(Noctis.Services.LyricsTimeline.HoldMs(lines, index));

    // ── Host ──

    private readonly Visual _host;
    private readonly Func<ScrollViewer?> _scroller;
    private readonly Func<ItemsControl?> _items;
    private readonly Func<double> _itemsTopInExtent;
    private readonly Func<double> _anchorRatio;
    private readonly Func<LyricsViewModel?> _vm;
    private readonly Action<string> _trace;

    /// <param name="host">Its TopLevel supplies the frame clock.</param>
    /// <param name="scroller">The lyrics ScrollViewer.</param>
    /// <param name="items">The ItemsControl of lines (an unvirtualized panel).</param>
    /// <param name="itemsTopInExtent">Where the line panel starts in the scroller's extent
    /// (the run-out padding above the first line).</param>
    /// <param name="anchorRatio">Active line anchor, as a share of the viewport, in extent space.</param>
    /// <param name="vm">The lyrics view model (hold times, auto-follow state).</param>
    /// <param name="trace">Dev-log trace sink.</param>
    public LyricsScrollDriver(Visual host, Func<ScrollViewer?> scroller, Func<ItemsControl?> items,
        Func<double> itemsTopInExtent, Func<double> anchorRatio, Func<LyricsViewModel?> vm, Action<string>? trace = null)
    {
        _host = host;
        _scroller = scroller;
        _items = items;
        _itemsTopInExtent = itemsTopInExtent;
        _anchorRatio = anchorRatio;
        _vm = vm;
        _trace = trace ?? (_ => { });
    }

    // ── State ──

    /// <summary>Last line scrolled to; -1 forces the next request through.</summary>
    public int LastScrolledIndex { get; set; } = -1;

    /// <summary>True while this driver is writing the offset (glide, chase, or a host's
    /// own programmatic restore that sets it).</summary>
    public bool IsProgrammaticScroll { get; set; }

    /// <summary>A glide or chase is in flight.</summary>
    public bool IsAnimating => _glide != null || _chaseRunning;

    /// <summary>The spring of the last glide started (tests/diagnostics).</summary>
    public LineSpring? LastSpring { get; private set; }

    /// <summary>The route of the last line change (tests/diagnostics).</summary>
    public Route? LastRoute { get; private set; }

    /// <summary>The target offset of the last line change (tests/diagnostics).</summary>
    public double LastTarget { get; private set; } = double.NaN;

    private int _generation;
    private HashSet<Control>? _cascadeLines;
    private LineGlide? _glide;
    private DispatcherTimer? _resumeTimer;
    private long _lastSeekCommitTicks;
    private long _lastScrollRequestTicks;
    private double _chaseTargetY;
    private double _chaseY;
    private bool _chaseRunning;
    private long _chaseLastFrameTicks;
    private double _lastAppliedScrollY = double.NaN;
    private bool _stopped;

    /// <summary>Whether a scrolled-away view will return to the sung line by itself.</summary>
    public bool AutoFollowResumePending => _resumeTimer != null;

    /// <summary>A committed seek from any surface: the next line change chases.</summary>
    public void NoteSeekCommitted() => _lastSeekCommitTicks = Stopwatch.GetTimestamp();

    /// <summary>Ends every loop and timer for good (host closed).</summary>
    public void Stop()
    {
        _stopped = true;
        Cancel();
        CancelAutoFollowResumeTimer();
    }

    public void Cancel()
    {
        if (IsProgrammaticScroll || _chaseRunning)
            _trace($"cancel {(_chaseRunning ? "chase" : "glide")} in flight at offset {_scroller()?.Offset.Y:0}");
        _generation++;
        IsProgrammaticScroll = false;
        _chaseRunning = false;
        _glide = null;
        ClearCascadeTransforms();
    }

    /// <summary>Every offset this driver (or its host) writes goes through here, so a write
    /// that does not match is known to be the user's.</summary>
    public void ApplyOffset(ScrollViewer scrollViewer, double y)
    {
        _lastAppliedScrollY = y;
        scrollViewer.Offset = new Vector(0, y);
    }

    /// <summary>Feed the scroller's Offset changes. Returns true when the change came from
    /// the user (auto-follow is then paused).</summary>
    public bool OnOffsetChanged(Vector offset)
    {
        if (IsProgrammaticScroll) return false;
        // Nothing has been scrolled by us yet — this is the first layout pass, not a user
        // gesture. Adopt it as the baseline.
        if (double.IsNaN(_lastAppliedScrollY))
        {
            _lastAppliedScrollY = offset.Y;
            return false;
        }
        // 1px tolerance: layout can round the offset we asked for.
        if (Math.Abs(offset.Y - _lastAppliedScrollY) <= 1.0) return false;
        _trace($"offset moved externally {_lastAppliedScrollY:0}→{offset.Y:0} — pausing auto-follow");
        PauseAutoFollow();
        return true;
    }

    /// <summary>A wheel turn over the lyrics (not consumed as Ctrl+wheel offset).</summary>
    public void OnWheel()
    {
        if (!IsProgrammaticScroll)
            PauseAutoFollow();
    }

    /// <summary>Ctrl/⌘+wheel over the lyrics nudges the lyrics timing offset (the view
    /// model's step and persistence). Returns true when it was consumed.</summary>
    public static bool TryNudgeOffset(LyricsViewModel? vm, ScrollViewer? scroller, PointerWheelEventArgs e)
    {
        if (vm == null || scroller == null || !SmoothScrollBehavior.HasCommandModifier(e)) return false;
        if (e.Source is not Visual source || !(source == scroller || scroller.IsVisualAncestorOf(source))) return false;
        return vm.NudgeLyricsOffset(e.Delta.Y);
    }

    public void PauseAutoFollow()
    {
        if (_vm() is not { } vm) return;
        vm.IsAutoFollowPaused = true;
        // Auto-resume, unless Settings › Lyrics Page › "Return to the current line
        // automatically" is off: then only the Follow pill (or a seek) resumes.
        CancelAutoFollowResumeTimer();
        if (!vm.AutoReturnToCurrentLine || _stopped) return;
        _resumeTimer = new DispatcherTimer { Interval = AutoFollowResumeDelay };
        _resumeTimer.Tick += (_, _) =>
        {
            CancelAutoFollowResumeTimer();
            if (_vm() is { AutoReturnToCurrentLine: true } v)
                v.IsAutoFollowPaused = false;
        };
        _resumeTimer.Start();
    }

    public void CancelAutoFollowResumeTimer()
    {
        _resumeTimer?.Stop();
        _resumeTimer = null;
    }

    // ── Geometry ──

    private Panel? LinesPanel()
    {
        var items = _items();
        var presenter = items?.GetVisualDescendants().OfType<ItemsPresenter>().FirstOrDefault();
        return presenter?.GetVisualChildren().FirstOrDefault() as Panel;
    }

    /// <summary>Anchor offset for a line, or null if the item containers are not laid out yet.</summary>
    public double? TryComputeAnchorOffset(int index)
    {
        var items = _items();
        var scroller = _scroller();
        if (items == null || scroller == null) return null;
        if (index < 0 || index >= items.ItemCount) return null;
        if (LinesPanel() is not { } panel || index >= panel.Children.Count) return null;

        var targetChild = panel.Children[index];
        var childBounds = targetChild.TransformToVisual(panel);
        if (childBounds == null) return null;

        // Panel coordinates exclude the run-out padding above the list, but the anchor
        // math runs in the scroll viewer's offset/extent space, which includes it.
        var childTop = childBounds.Value.Transform(new Point(0, 0)).Y + _itemsTopInExtent();
        return LyricsScrollAnchor.ComputeAnchorOffset(
            childTop, targetChild.Bounds.Height,
            scroller.Viewport.Height, scroller.Extent.Height, _anchorRatio());
    }

    // ── Line changes ──

    /// <summary>Moves the list to line <paramref name="index"/>: chase for a scrub or a seek
    /// resolving, tempo-spring glide with cascade otherwise; nothing while auto-follow is paused.</summary>
    public void ScrollToLine(int index)
    {
        if (_stopped || index == LastScrolledIndex) return;
        LastScrolledIndex = index;

        if (_vm() is { IsAutoFollowPaused: true })
        {
            _trace($"scroll→{index} skipped: auto-follow paused");
            return;
        }

        var now = Stopwatch.GetTimestamp();
        var sinceLastMs = _lastScrollRequestTicks == 0
            ? double.MaxValue
            : (now - _lastScrollRequestTicks) * 1000.0 / Stopwatch.Frequency;
        _lastScrollRequestTicks = now;
        var sinceSeekMs = _lastSeekCommitTicks == 0
            ? double.MaxValue
            : (now - _lastSeekCommitTicks) * 1000.0 / Stopwatch.Frequency;

        var scroller = _scroller();
        if (ChooseRoute(sinceLastMs, sinceSeekMs) == Route.Chase && scroller != null)
        {
            // No settle delay: a scrub only changes WHICH line is active, so the item
            // layout the offset is measured against has not moved.
            if (TryComputeAnchorOffset(index) is { } scrubTarget)
            {
                _trace($"scroll→{index} chase to {scrubTarget:0} (sinceLast {sinceLastMs:0}ms, sinceSeek {sinceSeekMs:0}ms)");
                LastRoute = Route.Chase;
                LastTarget = scrubTarget;
                _generation++;   // supersede the eased glide, keep the chase
                _glide = null;
                ClearCascadeTransforms();
                ChaseTo(scroller, scrubTarget);
                return;
            }
        }

        // Stop the frame loop of a glide in flight but keep its state: the new glide
        // starts from exactly what is on screen — offset, cascade translates and their
        // velocities (LineGlide). Every early return below cancels properly instead.
        // Started NOW, on the same frame the line's scale/opacity transitions start.
        _generation++;
        var started = false;
        try
        {
            var items = _items();
            if (items == null || scroller == null || index >= items.ItemCount) return;
            if (LinesPanel() is not { } panel || index >= panel.Children.Count) return;
            if (TryComputeAnchorOffset(index) is not { } targetOffset) return;

            var currentOffset = scroller.Offset.Y;
            var diff = targetOffset - currentOffset;
            LastTarget = targetOffset;

            if (Math.Abs(diff) < 2 && _glide == null)
            {
                _trace($"scroll→{index} snap {currentOffset:0}→{targetOffset:0}");
                LastRoute = Route.Glide;
                ApplyOffset(scroller, targetOffset);
                return;
            }

            // The spring follows the tempo: how long this line stays the target (its start
            // to the next line's start). Fast rap lines land quickly, slow lines glide.
            var lines = _vm()?.LyricLines;
            var spring = lines != null ? SpringFor(lines, index) : LineMotion.ForHold(double.NaN);
            LastSpring = spring;
            LastRoute = Route.Glide;
            _trace($"scroll→{index} glide {currentOffset:0}→{targetOffset:0} ({diff:+0;-0}px) " +
                   $"spring ζ{spring.Zeta:0.00} ωn{spring.NaturalFrequency:0.0}{(_glide != null ? " carried" : "")}");
            AnimateScroll(scroller, currentOffset, targetOffset, spring, panel, index);
            started = true;
        }
        catch { }
        finally
        {
            if (!started) Cancel();
        }
    }

    /// <summary>Jumps (no animation) to line <paramref name="index"/> once layout has run.</summary>
    public void JumpToLineWhenReady(int index)
    {
        if (index < 0 || _stopped) return;
        _trace($"jump→{index}");
        LastScrolledIndex = index;
        Cancel();
        Dispatcher.UIThread.Post(() => JumpNow(index), DispatcherPriority.Loaded);
    }

    /// <summary>Jumps (no animation) to line <paramref name="index"/> now. False when the line
    /// is not laid out yet.</summary>
    public bool JumpNow(int index)
    {
        try
        {
            if (_stopped || _scroller() is not { } scroller || TryComputeAnchorOffset(index) is not { } target)
                return false;
            LastTarget = target;
            ApplyOffset(scroller, target);
            return true;
        }
        catch { return false; }
    }

    // ── Scrub chase ──
    // Dragging the timeline delivers a new active line every frame or two; an eased glide
    // restarting at zero velocity could not follow. A new target only moves the goalpost,
    // so the existing velocity carries straight over (SmoothScrollBehavior's wheel chase).

    private void ChaseTo(ScrollViewer scrollViewer, double targetY)
    {
        _chaseTargetY = targetY;
        if (_chaseRunning) return;
        // Pick the chase up from wherever the offset actually is, so a glide that was
        // in flight hands over without a jump.
        _chaseY = scrollViewer.Offset.Y;
        _trace($"chase start {_chaseY:0}→{targetY:0}");
        _chaseLastFrameTicks = Stopwatch.GetTimestamp();
        _chaseRunning = true;
        IsProgrammaticScroll = true;
        RequestChaseFrame();
    }

    private void RequestChaseFrame()
    {
        if (TopLevel.GetTopLevel(_host) is not { } topLevel)
        {
            // No frame clock (detached mid-drag) — land on the target rather than strand
            // the offset part-way.
            if (_scroller() is { } sv) ApplyOffset(sv, _chaseTargetY);
            _chaseRunning = false;
            IsProgrammaticScroll = false;
            return;
        }
        topLevel.RequestAnimationFrame(ChaseFrame);
    }

    private void ChaseFrame(TimeSpan _)
    {
        if (!_chaseRunning || _stopped) return;
        if (_scroller() is not { } scrollViewer)
        {
            _chaseRunning = false;
            IsProgrammaticScroll = false;
            return;
        }

        var now = Stopwatch.GetTimestamp();
        // Clamped so a stalled UI thread cannot produce one huge jump.
        var dt = Math.Min((now - _chaseLastFrameTicks) / (double)Stopwatch.Frequency, 0.1);
        _chaseLastFrameTicks = now;

        // Extent grows as lines re-wrap, so re-clamp every frame (an unreachable target
        // clamps to what is reachable, so the chase always lands).
        var maxY = Math.Max(0, scrollViewer.Extent.Height - scrollViewer.Viewport.Height);
        var target = Math.Clamp(_chaseTargetY, 0, maxY);
        _chaseY += (target - _chaseY) * (1 - Math.Exp(-dt / (ScrubSettleMs / 4600.0)));

        if (Math.Abs(target - _chaseY) < 0.5)
        {
            _chaseY = target;
            ApplyOffset(scrollViewer, target);
            _chaseRunning = false;
            IsProgrammaticScroll = false;
            return;
        }
        ApplyOffset(scrollViewer, _chaseY);
        RequestChaseFrame();
    }

    // ── Tempo-spring glide with cascade ──

    private void ClearCascadeTransforms()
    {
        if (_cascadeLines == null) return;
        foreach (var control in _cascadeLines)
            if (control.RenderTransform is TranslateTransform tt)
                tt.Y = 0;
        _cascadeLines = null;
    }

    /// <summary>
    /// Frame-clock scroll animation: the offset and every line follow one <see cref="LineGlide"/>
    /// on <paramref name="spring"/>; lines below the active one set off a stagger later each (a
    /// transient translate that relaxes to zero), so the stack settles top-down. A glide still in
    /// flight hands over positions and velocities.
    /// </summary>
    private void AnimateScroll(ScrollViewer scrollViewer, double from, double to, LineSpring spring,
        Panel? linesPanel, int activeIndex)
    {
        _generation++;
        IsProgrammaticScroll = true;

        var delta = to - from;
        // The stagger is a SETTLING effect for line-to-line advance; over a seek-sized jump
        // it is unreadable and drags many blurred lines through the viewport. Past a viewport
        // and a half, glide cleanly with no cascade.
        var seekSized = Math.Abs(delta) > scrollViewer.Viewport.Height * 1.5;
        var cascade = linesPanel != null && activeIndex >= 0 && Math.Abs(delta) > 8 && !seekSized;

        var lines = linesPanel?.Children;
        var lineCount = lines?.Count ?? 0;
        var glide = LineGlide.Begin(_glide, NowMs(), from, to, spring, lineCount, activeIndex,
            cascade ? CascadeDelayPerLineMs : 0, CascadeMaxLines,
            i => lines![i].RenderTransform is TranslateTransform tt ? tt.Y : 0);
        _glide = glide;
        var touched = _cascadeLines ??= new HashSet<Control>();
        var generation = _generation;
        var startMs = glide.StartMs;

        var lastFrameMs = double.NaN;
        var frames = 0;
        var maxGapMs = 0.0;
        var maxGapAtMs = 0.0;
        var droppedFrames = 0;

        void Frame(TimeSpan _)
        {
            if (generation != _generation || _stopped) return;
            var now = NowMs();
            var elapsed = now - startMs;
            frames++;
            if (!double.IsNaN(lastFrameMs))
            {
                var gap = elapsed - lastFrameMs;
                if (gap > maxGapMs) { maxGapMs = gap; maxGapAtMs = elapsed; }
                if (gap > 30) droppedFrames++;
            }
            lastFrameMs = elapsed;

            var scrollY = glide.Offset(now);
            ApplyOffset(scrollViewer, scrollY);

            // Each line is displaced by the gap between the offset and its own course. Chained
            // clamp below the active line so lines never cross; only lines near the viewport
            // get the transform written (each write re-renders that line's blur).
            var viewportHeight = scrollViewer.Viewport.Height;
            var bandTop = scrollY - viewportHeight;
            var bandBottom = scrollY + viewportHeight * 2;
            if (lines != null)
            {
                var prevLag = 0.0;
                var n = Math.Min(lineCount, lines.Count);
                for (var i = 0; i < n; i++)
                {
                    var lag = glide.LineLag(i, now);
                    if (i > activeIndex)
                    {
                        lag = LineMotion.CascadeStep(lag, prevLag, MaxCascadeLagStepPx);
                        prevLag = lag;
                    }
                    var control = lines[i];
                    var top = control.Bounds.Top + lag;
                    var target = top + control.Bounds.Height >= bandTop && top <= bandBottom ? lag : 0.0;
                    if (control.RenderTransform is TranslateTransform tt)
                    {
                        // ReSharper disable once CompareOfFloatsByEqualityOperator
                        if (tt.Y != target)
                        {
                            tt.Y = target;
                            touched.Add(control);
                        }
                    }
                    else if (Math.Abs(target) > 0.01)
                    {
                        control.RenderTransform = new TranslateTransform(0, target);
                        touched.Add(control);
                    }
                }
            }

            if (glide.IsDone(now))
            {
                ApplyOffset(scrollViewer, to);
                ClearCascadeTransforms();
                _glide = null;
                IsProgrammaticScroll = false;
                _trace($"glide end at {elapsed:0}ms (planned {glide.EndMs - startMs:0}): {frames} frames, " +
                       $"{droppedFrames} gap(s) >30ms, worst {maxGapMs:0}ms at {maxGapAtMs:0}ms, cascade {cascade}, offset {to:0}");
                return;
            }
            RequestScrollFrame(Frame, scrollViewer, to);
        }

        RequestScrollFrame(Frame, scrollViewer, to);
    }

    private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

    /// <summary>Schedules the next frame; with no TopLevel (left the tree mid-glide) snaps to
    /// the target so the offset never strands mid-glide.</summary>
    private void RequestScrollFrame(Action<TimeSpan> frame, ScrollViewer scrollViewer, double to)
    {
        if (TopLevel.GetTopLevel(_host) is { } topLevel)
        {
            topLevel.RequestAnimationFrame(frame);
        }
        else
        {
            ApplyOffset(scrollViewer, to);
            Cancel();
        }
    }
}
