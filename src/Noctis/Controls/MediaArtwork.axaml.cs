using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Transformation;
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

    /// <summary>How far (canvas units) the record slides out of the sleeve on play. The
    /// XAML tucks it at Canvas.Left 42; 26 more puts the label past the sleeve's edge.</summary>
    private const double RecordSlideDistance = 26;

    private static readonly TransformOperations RecordTucked = TransformOperations.Parse("translateX(0px)");
    private static readonly TransformOperations RecordOut = TransformOperations.Parse($"translateX({RecordSlideDistance}px)");

    private readonly SpinClock _clock = new();
    private readonly RotateTransform _discRotate = new();
    private readonly RotateTransform _vinylRotate = new();
    private readonly RotateTransform _reelLeftRotate = new();
    private readonly RotateTransform _reelRightRotate = new();

    private bool _frameQueued;
    private long _lastFrameTimestamp;

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
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == MediumProperty || change.Property == AnimatedActiveProperty)
        {
            ApplyMedium();
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

        QueueFrame();
    }

    /// <summary>Record out of the sleeve while playing, tucked while paused. The
    /// transition on VinylSlide eases the move.</summary>
    private void ApplyRecordSlide()
    {
        VinylSlide.RenderTransform = IsSpinning ? RecordOut : RecordTucked;
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
