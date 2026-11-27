using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// The large now-playing cover, dressed as a plain square, a compact disc, a vinyl sleeve
/// with the record pulled out, or a cassette (Appearance → Now Playing Artwork).
///
/// The XAML holds all four costumes; this class shows exactly one, keeps only that one's
/// <see cref="AnimatedCoverImage"/> decoding, and turns the disc/record/reels from the
/// frame clock while <see cref="IsSpinning"/> is on. Rotation runs through a
/// <see cref="SpinClock"/> rather than a style animation so pausing coasts to a stop and
/// holds the angle instead of snapping back to 0°. The loop only runs while there is
/// something to move: never for the plain cover, and it ends once a paused disc settles.
///
/// One more cue comes from state rather than the clock: the vinyl record slides out of
/// its sleeve while playing and tucks back when paused.
///
/// Disc scratching (Settings → Player, off by default): with <see cref="ScratchEnabled"/>
/// on, pressing the CD or the record and dragging past <see cref="DiscScratch.DragThreshold"/>
/// grabs it. The disc then turns with the pointer and <see cref="ScratchTarget"/> moves the
/// song along (clockwise = forward); letting go hands the disc back to the clock. A press
/// that never travels that far is left a plain click. Off, the handlers return at once.
/// </summary>
public partial class MediaArtwork : UserControl
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<MediaArtwork, string?>(nameof(SourcePath));

    /// <summary>Already-decoded bitmap painted underneath the high-res load so a track
    /// change never flashes the placeholder (the lyrics page passes Player.AlbumArt).</summary>
    public static readonly StyledProperty<IImage?> FallbackSourceProperty =
        AvaloniaProperty.Register<MediaArtwork, IImage?>(nameof(FallbackSource));

    public static readonly StyledProperty<string?> AnimatedSourceProperty =
        AvaloniaProperty.Register<MediaArtwork, string?>(nameof(AnimatedSource));

    /// <summary>The user's "Animated Artwork" toggle. Applied to the visible costume only.</summary>
    public static readonly StyledProperty<bool> AnimatedActiveProperty =
        AvaloniaProperty.Register<MediaArtwork, bool>(nameof(AnimatedActive));

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<MediaArtwork, int>(nameof(DecodeWidth), 1280);

    public static readonly StyledProperty<ArtworkMedium> MediumProperty =
        AvaloniaProperty.Register<MediaArtwork, ArtworkMedium>(nameof(Medium), ArtworkMedium.Cover);

    public static readonly StyledProperty<bool> IsSpinningProperty =
        AvaloniaProperty.Register<MediaArtwork, bool>(nameof(IsSpinning));

    /// <summary>Corner radius of the plain-cover costume (the discs and cassette own their shapes).</summary>
    public static readonly StyledProperty<CornerRadius> CoverCornerRadiusProperty =
        AvaloniaProperty.Register<MediaArtwork, CornerRadius>(nameof(CoverCornerRadius), new CornerRadius(12));

    /// <summary>Settings → Player → Disc Scratching.</summary>
    public static readonly StyledProperty<bool> ScratchEnabledProperty =
        AvaloniaProperty.Register<MediaArtwork, bool>(nameof(ScratchEnabled));

    /// <summary>What a scratch drives (the player). Null = the disc cannot be scratched.</summary>
    public static readonly StyledProperty<IDiscScratchTarget?> ScratchTargetProperty =
        AvaloniaProperty.Register<MediaArtwork, IDiscScratchTarget?>(nameof(ScratchTarget));

    public bool ScratchEnabled { get => GetValue(ScratchEnabledProperty); set => SetValue(ScratchEnabledProperty, value); }
    public IDiscScratchTarget? ScratchTarget { get => GetValue(ScratchTargetProperty); set => SetValue(ScratchTargetProperty, value); }

    public string? SourcePath { get => GetValue(SourcePathProperty); set => SetValue(SourcePathProperty, value); }
    public IImage? FallbackSource { get => GetValue(FallbackSourceProperty); set => SetValue(FallbackSourceProperty, value); }
    public string? AnimatedSource { get => GetValue(AnimatedSourceProperty); set => SetValue(AnimatedSourceProperty, value); }
    public bool AnimatedActive { get => GetValue(AnimatedActiveProperty); set => SetValue(AnimatedActiveProperty, value); }
    public int DecodeWidth { get => GetValue(DecodeWidthProperty); set => SetValue(DecodeWidthProperty, value); }
    public ArtworkMedium Medium { get => GetValue(MediumProperty); set => SetValue(MediumProperty, value); }
    public bool IsSpinning { get => GetValue(IsSpinningProperty); set => SetValue(IsSpinningProperty, value); }
    public CornerRadius CoverCornerRadius { get => GetValue(CoverCornerRadiusProperty); set => SetValue(CoverCornerRadiusProperty, value); }

    /// <summary>Cassette reels are small and turn faster than a platter; a bare 1:1 with
    /// the disc speed read as sluggish.</summary>
    private const double ReelSpeedRatio = 1.8;

    /// <summary>How far (× sleeve edge) the record slides out of the sleeve on play: tucked,
    /// its label hides behind the sleeve; out, it sits where the carousel's record does.</summary>
    private const double RecordSlideFraction = 0.2;

    private static readonly TransformOperations RecordTucked = TransformOperations.Parse("translateX(0px)");
    // Recomputed in pixels by LayoutCases; this stands in until the first layout.
    private TransformOperations _recordOut = TransformOperations.Parse("translateX(40px)");
    private double _recordSlide = 40;

    private readonly SpinClock _clock = new();
    private readonly RotateTransform _discRotate = new();
    private readonly RotateTransform _vinylRotate = new();
    private readonly RotateTransform _reelLeftRotate = new();
    private readonly RotateTransform _reelRightRotate = new();

    private bool _frameQueued;
    private long _lastFrameTimestamp;

    // Disc scratching state. Armed = pressed on the disc, not yet past the threshold.
    private IPointer? _scratchPointer;
    private Point _scratchPressPoint;      // in this control, for the threshold
    private Visual? _scratchFrame;         // the disc's non-rotating parent
    private Point _scratchCentre;          // disc centre in _scratchFrame
    private double _scratchRadius;
    private double _scratchLastAngle;
    private double _scratchDegrees;        // turn since the grab, as the target applied it
    private double _scratchBaseTotal;      // clock total at the grab
    private IDiscScratchTarget? _scratchActiveTarget; // non-null while the disc is held

    public MediaArtwork()
    {
        InitializeComponent();

        // Transforms are created here rather than named in XAML so the field types are
        // certain and the same instance is reused across every frame.
        DiscSpin.RenderTransform = _discRotate;
        VinylSpin.RenderTransform = _vinylRotate;
        ReelLeftSpin.RenderTransform = _reelLeftRotate;
        ReelRightSpin.RenderTransform = _reelRightRotate;

        ApplyMedium();
        ApplyRecordSlide();

        AddHandler(PointerPressedEvent, OnScratchPressed, RoutingStrategies.Bubble);
        AddHandler(PointerMovedEvent, OnScratchMoved, RoutingStrategies.Bubble);
        AddHandler(PointerReleasedEvent, OnScratchReleased, RoutingStrategies.Bubble);
        AddHandler(PointerCaptureLostEvent, OnScratchCaptureLost, RoutingStrategies.Bubble);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MediumProperty || change.Property == AnimatedActiveProperty)
        {
            if (change.Property == MediumProperty) FinishScratch();
            ApplyMedium();
        }
        else if (change.Property == ScratchEnabledProperty || change.Property == ScratchTargetProperty)
        {
            FinishScratch();
            ApplyScratchCursor();
        }
        else if (change.Property == IsSpinningProperty)
        {
            _clock.IsRunning = IsSpinning;
            ApplyRecordSlide();
            QueueFrame();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _clock.IsRunning = IsSpinning;
        QueueFrame();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        // A frame already requested still fires; OnFrame sees the missing VisualRoot and
        // does not re-queue, so the loop ends on its own.
        FinishScratch();
        base.OnDetachedFromVisualTree(e);
    }

    private bool Spins => Medium != ArtworkMedium.Cover;

    private void ApplyMedium()
    {
        var medium = Medium;
        CoverLayout.IsVisible = medium == ArtworkMedium.Cover;
        DiscLayout.IsVisible = medium == ArtworkMedium.CompactDisc;
        VinylLayout.IsVisible = medium == ArtworkMedium.Vinyl;
        CassetteLayout.IsVisible = medium == ArtworkMedium.Cassette;

        // One animated decoder at a time: only the costume on screen may run.
        var animated = AnimatedActive;
        CoverAnimated.IsActive = animated && medium == ArtworkMedium.Cover;
        DiscAnimated.IsActive = animated && medium == ArtworkMedium.CompactDisc;
        SleeveAnimated.IsActive = animated && medium == ArtworkMedium.Vinyl;
        CassetteAnimated.IsActive = animated && medium == ArtworkMedium.Cassette;

        ApplyScratchCursor();
        QueueFrame();
    }

    /// <summary>Record out of the sleeve while playing, tucked while paused. The
    /// transition on VinylSlide eases the move.</summary>
    private void ApplyRecordSlide()
    {
        VinylSlide.RenderTransform = IsSpinning ? _recordOut : RecordTucked;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        LayoutCases(finalSize);
        return base.ArrangeOverride(finalSize);
    }

    /// <summary>Places the vinyl and cassette parts in pixels for this size, centred, with the
    /// carousel's proportions (<see cref="MediaCaseArt"/>). Setting the same values again on
    /// the follow-up layout pass changes nothing, so this settles in one pass.</summary>
    private void LayoutCases(Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        // Vinyl: sleeve s × s, the record (tucked) to its right.
        var s = Math.Min(size.Width / MediaCaseArt.VinylWidth, size.Height);
        var x0 = (size.Width - MediaCaseArt.VinylWidth * s) / 2;
        var y0 = (size.Height - s) / 2;
        Place(VinylSleeve, x0, y0, s, s);
        VinylSleeve.CornerRadius = new CornerRadius(s * 0.004);
        var r = MediaCaseArt.VinylDiscRadius * s;
        var slide = Math.Round(s * RecordSlideFraction);
        var centre = MediaCaseArt.VinylDiscCentre(s);
        Place(VinylSlide, x0 + centre.X - slide - r, y0 + centre.Y - r, r * 2, r * 2);
        var label = r * MediaCaseArt.VinylLabelRadius * 2;
        VinylLabel.Width = VinylLabel.Height = label;
        VinylLabelFinish.Width = VinylLabelFinish.Height = label;
        if (Math.Abs(slide - _recordSlide) > 0.5)
        {
            _recordSlide = slide;
            _recordOut = TransformOperations.Parse($"translateX({slide.ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
            ApplyRecordSlide();
        }

        // Cassette: the shell centred; label, reels and layers in shell space.
        s = Math.Min(size.Width / MediaCaseArt.CassetteWidth, size.Height / MediaCaseArt.CassetteHeight);
        var w = MediaCaseArt.CassetteWidth * s;
        var h = MediaCaseArt.CassetteHeight * s;
        Place(CassetteShell, (size.Width - w) / 2, (size.Height - h) / 2, w, h);
        foreach (var layer in new Control[] { CassetteBody, CassetteShellLayer, CassetteWindowLayer, CassetteGlassLayer })
            Place(layer, 0, 0, w, h);
        CassetteBody.CornerRadius = new CornerRadius(s * 0.035);
        var labelRect = MediaCaseArt.CassetteLabelRect(s);
        Place(CassetteLabel, labelRect.X, labelRect.Y, labelRect.Width, labelRect.Height);
        CassetteLabel.CornerRadius = new CornerRadius(s * 0.018);
        var (left, right) = MediaCaseArt.CassetteReelCentres(s);
        var hub = MediaCaseArt.CassetteHubRadius(s);
        Place(ReelLeftSpin, left.X - hub, left.Y - hub, hub * 2, hub * 2);
        Place(ReelRightSpin, right.X - hub, right.Y - hub, hub * 2, hub * 2);
    }

    private static void Place(Control control, double x, double y, double width, double height)
    {
        Canvas.SetLeft(control, x);
        Canvas.SetTop(control, y);
        control.Width = width;
        control.Height = height;
    }

    private void QueueFrame()
    {
        if (_frameQueued || !Spins || _clock.IsSettled) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel) return;

        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        _frameQueued = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameQueued = false;
        if (VisualRoot == null || !Spins) return;

        var now = Stopwatch.GetTimestamp();
        // Clamp so a frame after a long stall (window hidden, machine asleep) doesn't
        // whip the disc through several turns at once.
        var elapsedSeconds = Math.Min((now - _lastFrameTimestamp) / (double)Stopwatch.Frequency, 0.1);
        _lastFrameTimestamp = now;

        _clock.Advance(elapsedSeconds);
        ApplyAngle(_clock.TotalDegrees);

        if (_clock.IsSettled) return;
        if (TopLevel.GetTopLevel(this) is { } topLevel)
        {
            _frameQueued = true;
            topLevel.RequestAnimationFrame(OnFrame);
        }
    }

    /// <summary>Reel rotation for an unwrapped disc rotation. Geared off the total, not
    /// the wrapped angle, so the reels never snap when the disc passes 360°.</summary>
    internal static double ReelAngle(double totalDegrees) => SpinClock.Wrap(totalDegrees * ReelSpeedRatio);

    // ── Disc scratching ──

    /// <summary>The turning part a scratch grabs: the CD or the record. The plain cover and
    /// the cassette have no platter to grab.</summary>
    internal Control? ScratchDisc => Medium switch
    {
        ArtworkMedium.CompactDisc => DiscSpin,
        ArtworkMedium.Vinyl => VinylSpin,
        _ => null,
    };

    /// <summary>True while the disc is held (past the threshold).</summary>
    internal bool IsScratching => _scratchActiveTarget != null;

    /// <summary>True between a press on the disc and its release, scratching or not.</summary>
    internal bool IsScratchArmed => _scratchPointer != null;

    /// <summary>Total disc rotation (unwrapped degrees), for tests.</summary>
    internal double DiscTotalDegrees => _clock.TotalDegrees;

    private void ApplyScratchCursor()
    {
        var on = ScratchEnabled && ScratchTarget != null;
        DiscSpin.Cursor = on && Medium == ArtworkMedium.CompactDisc ? new Cursor(StandardCursorType.Hand) : null;
        VinylSpin.Cursor = on && Medium == ArtworkMedium.Vinyl ? new Cursor(StandardCursorType.Hand) : null;
    }

    private void OnScratchPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!ScratchEnabled || ScratchTarget == null) return;
        // A double-click keeps whatever it did before (the mini player closes on it).
        if (e.ClickCount > 1 || _scratchPointer != null) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (ScratchDisc is not { } disc || disc.GetVisualParent() is not Visual frame) return;
        if (e.Source is not Visual source || !(ReferenceEquals(source, disc) || disc.IsVisualAncestorOf(source))) return;

        var size = frame.Bounds.Size;
        var centre = new Point(size.Width / 2, size.Height / 2);
        var radius = Math.Min(size.Width, size.Height) / 2;
        var p = e.GetPosition(frame);
        var offset = p - centre;
        if (radius <= 0 || Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) > radius) return;

        _scratchPointer = e.Pointer;
        _scratchPressPoint = e.GetPosition(this);
        _scratchFrame = frame;
        _scratchCentre = centre;
        _scratchRadius = radius;
        _scratchLastAngle = DiscScratch.AngleOf(p, centre);
        _scratchDegrees = 0;
        // Claimed so a press on the disc does not also start a window drag (mini player).
        e.Handled = true;
    }

    private void OnScratchMoved(object? sender, PointerEventArgs e)
    {
        if (_scratchPointer == null || !ReferenceEquals(e.Pointer, _scratchPointer) || _scratchFrame is not { } frame) return;

        if (_scratchActiveTarget == null)
        {
            if (!DiscScratch.PastThreshold(_scratchPressPoint, e.GetPosition(this))) return;
            if (ScratchTarget is not { } target || !target.BeginScratch())
            {
                ResetScratchPointer();
                return;
            }
            _scratchActiveTarget = target;
            _scratchBaseTotal = _clock.TotalDegrees;
            _clock.Hold(_scratchBaseTotal);
            e.Pointer.Capture(this);
        }

        var p = e.GetPosition(frame);
        var offset = p - _scratchCentre;
        // Right over the spindle the angle swings wildly for a pixel of travel: skip it.
        if (Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y) < _scratchRadius * DiscScratch.DeadZoneFraction)
        {
            e.Handled = true;
            return;
        }

        var angle = DiscScratch.AngleOf(p, _scratchCentre);
        var wanted = _scratchDegrees + DiscScratch.TurnBetween(_scratchLastAngle, angle);
        _scratchLastAngle = angle;
        // The player clamps at the track's start and end; the disc stops where the song does.
        _scratchDegrees = _scratchActiveTarget.ScratchTo(wanted);
        _clock.Hold(_scratchBaseTotal + _scratchDegrees);
        ApplyAngle(_clock.TotalDegrees);
        e.Handled = true;
    }

    private void OnScratchReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_scratchPointer == null || !ReferenceEquals(e.Pointer, _scratchPointer)) return;
        var wasScratching = IsScratching;
        FinishScratch();
        // A release without a scratch is a plain click: leave it to whoever handles clicks.
        if (wasScratching) e.Handled = true;
    }

    private void OnScratchCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (_scratchPointer == null || !ReferenceEquals(e.Pointer, _scratchPointer)) return;
        FinishScratch();
    }

    /// <summary>Ends any press or scratch: the player gets its single seek, the disc goes back
    /// to the clock. Safe to call any time.</summary>
    private void FinishScratch()
    {
        var target = _scratchActiveTarget;
        var pointer = _scratchPointer;
        // Cleared first: releasing the capture raises CaptureLost, which then finds nothing to do.
        _scratchActiveTarget = null;
        ResetScratchPointer();
        if (target == null) return;

        _clock.Release();
        target.EndScratch();
        if (pointer?.Captured is Visual captured && (ReferenceEquals(captured, this) || this.IsVisualAncestorOf(captured)))
            pointer.Capture(null);
        QueueFrame();
    }

    private void ResetScratchPointer()
    {
        _scratchPointer = null;
        _scratchFrame = null;
    }

    private void ApplyAngle(double totalDegrees)
    {
        var angle = SpinClock.Wrap(totalDegrees);
        _discRotate.Angle = angle;
        _vinylRotate.Angle = angle;
        var reel = ReelAngle(totalDegrees);
        _reelLeftRotate.Angle = reel;
        _reelRightRotate.Angle = reel;
    }
}
