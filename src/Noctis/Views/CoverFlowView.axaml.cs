using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class CoverFlowView : UserControl
{
    /// <summary>Below this width the now-playing column no longer fits beside the pile
    /// and drops underneath it, centred.</summary>
    internal const double SideBySideMinWidth = 1040;

    /// <summary>Opacity of the blurred cover behind the carousel (the dark wash sits on top).</summary>
    private const double BackgroundArtOpacity = 0.55;

    private bool? _isStacked;

    // ── Carousel ───────────────────────────────────────────────────────────────
    // Slot order −7..+7; each card owns one TransformGroup [Rotate3D, Scale, Translate]
    // whose values SetPose writes. Built once; a skip only changes the numbers.
    private readonly CoverFlowCard[] _slots = new CoverFlowCard[2 * CoverFlowCarouselGeometry.SideSlots + 1];
    private readonly (Rotate3DTransform Rotate, ScaleTransform Scale, TranslateTransform Move)[] _transforms =
        new (Rotate3DTransform, ScaleTransform, TranslateTransform)[2 * CoverFlowCarouselGeometry.SideSlots + 1];

    // Cascade: slots −3..+3 (index = slot + 3); each card's RotateTransform is written by
    // SetCascadePose along with its Canvas position, size, wash and opacity.
    private readonly Border[] _cascadeCards = new Border[2 * CoverFlowCascadeGeometry.SideSlots + 1];
    private readonly Border[] _cascadeWashes = new Border[2 * CoverFlowCascadeGeometry.SideSlots + 1];
    private readonly RotateTransform[] _cascadeRotates = new RotateTransform[2 * CoverFlowCascadeGeometry.SideSlots + 1];

    private CoverFlowViewModel? _vm;
    private int _slideStep;
    private double _slideSeconds = CoverFlowCarouselGeometry.SlideDuration.TotalSeconds;
    private long _slideStartTicks;
    private bool _frameQueued;
    private bool _backgroundOnA;

    /// <summary>True while the cards are sliding after a skip (wheel input is gated on it
    /// so one flick is one skip, not a burst).</summary>
    internal bool IsSliding { get; private set; }

    public CoverFlowView()
    {
        InitializeComponent();
        BuildCarousel();
        BuildCascade();
        DataContextChanged += OnDataContextChanged;
        ActualThemeVariantChanged += OnThemeVariantChanged;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ApplyThemeBlur();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Land the slide: no frame will come while we are off-screen.
        if (IsSliding) FinishSlide();
    }

    private void BuildCarousel()
    {
        CoverFlowCard[] ordered =
        {
            SlotPrev7, SlotPrev6, SlotPrev5, SlotPrev4, SlotPrev3, SlotPrev2, SlotPrev1,
            CenterCard,
            SlotNext1, SlotNext2, SlotNext3, SlotNext4, SlotNext5, SlotNext6, SlotNext7,
        };
        Array.Copy(ordered, _slots, ordered.Length);

        for (var i = 0; i < _slots.Length; i++)
        {
            var rotate = new Rotate3DTransform { Depth = CoverFlowCarouselGeometry.Depth };
            var scale = new ScaleTransform();
            var move = new TranslateTransform();
            _transforms[i] = (rotate, scale, move);
            var group = new TransformGroup();
            group.Children.Add(rotate);
            group.Children.Add(scale);
            group.Children.Add(move);
            _slots[i].RenderTransform = group;
            _slots[i].TitleClicked += OnCardTitleClicked;
            SetPose(i, SlotOf(i));
        }

        CarouselHost.PointerWheelChanged += OnCarouselWheel;
        CarouselHost.PointerPressed += (_, _) => Focus();
    }

    private static int SlotOf(int index) => index - CoverFlowCarouselGeometry.SideSlots;

    private void BuildCascade()
    {
        _cascadeCards[0] = CascadeSlotM3; _cascadeWashes[0] = CascadeWashM3;
        _cascadeCards[1] = CascadeSlotM2; _cascadeWashes[1] = CascadeWashM2;
        _cascadeCards[2] = CascadeSlotM1; _cascadeWashes[2] = CascadeWashM1;
        _cascadeCards[3] = CascadeCenterCover; _cascadeWashes[3] = CascadeWash0;
        _cascadeCards[4] = CascadeSlotP1; _cascadeWashes[4] = CascadeWashP1;
        _cascadeCards[5] = CascadeSlotP2; _cascadeWashes[5] = CascadeWashP2;
        _cascadeCards[6] = CascadeSlotP3; _cascadeWashes[6] = CascadeWashP3;
        for (var i = 0; i < _cascadeCards.Length; i++)
        {
            _cascadeRotates[i] = new RotateTransform();
            _cascadeCards[i].RenderTransformOrigin = RelativePoint.Center;
            _cascadeCards[i].RenderTransform = _cascadeRotates[i];
            SetCascadePose(i, CascadeSlotOf(i));
        }
    }

    private static int CascadeSlotOf(int index) => index - CoverFlowCascadeGeometry.SideSlots;

    private void SetCascadePose(int index, double slot)
    {
        var pose = CoverFlowCascadeGeometry.At(slot);
        var card = _cascadeCards[index];
        Canvas.SetLeft(card, pose.Left);
        Canvas.SetTop(card, pose.Top);
        card.Width = pose.Size;
        card.Height = pose.Size;
        card.Opacity = pose.Opacity;
        card.ZIndex = CoverFlowCascadeGeometry.ZIndexAt(slot);
        _cascadeRotates[index].Angle = pose.Angle;
        _cascadeWashes[index].Opacity = pose.Dim;
    }

    /// <summary>Current signed slot of the Cascade card in slot <paramref name="slot"/>,
    /// read back off its canvas position (tests; fractional mid-slide).</summary>
    internal double CascadePositionOf(int slot)
    {
        var card = _cascadeCards[slot + CoverFlowCascadeGeometry.SideSlots];
        var left = Canvas.GetLeft(card);
        var top = Canvas.GetTop(card);
        // Nearest point on the piecewise path by sampling: the path is not monotone in
        // either axis, so search the whole range.
        double best = 0, bestD = double.MaxValue;
        for (var s = -(double)CoverFlowCascadeGeometry.ExitSlot; s <= CoverFlowCascadeGeometry.ExitSlot; s += 0.005)
        {
            var p = CoverFlowCascadeGeometry.At(s);
            var d = (p.Left - left) * (p.Left - left) + (p.Top - top) * (p.Top - top);
            if (d < bestD) { bestD = d; best = s; }
        }
        return best;
    }

    /// <summary>Current signed position of the card in slot <paramref name="slot"/>
    /// (−7..+7), read back off its transform — mid-slide it is fractional.</summary>
    internal double PositionOf(int slot)
    {
        var i = slot + CoverFlowCarouselGeometry.SideSlots;
        var pose = _transforms[i];
        // X is monotone in |position| on each side, so invert it by search on the keyframes.
        var x = pose.Move.X;
        var sign = x < 0 ? -1 : 1;
        var ax = Math.Abs(x);
        double lo = 0, hi = CoverFlowCarouselGeometry.ExitSlot;
        for (var k = 0; k < 40; k++)
        {
            var mid = (lo + hi) / 2;
            if (Math.Abs(CoverFlowCarouselGeometry.At(mid).X) < ax) lo = mid; else hi = mid;
        }
        return sign * (lo + hi) / 2;
    }

    private void SetPose(int index, double position)
    {
        var pose = CoverFlowCarouselGeometry.At(position);
        var (rotate, scale, move) = _transforms[index];
        rotate.AngleY = pose.AngleY;
        scale.ScaleX = pose.Scale;
        scale.ScaleY = pose.Scale;
        move.X = pose.X;
        var card = _slots[index];
        card.Opacity = pose.Opacity;
        card.ZIndex = CoverFlowCarouselGeometry.ZIndexAt(position);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
        {
            _vm.CarouselShifted -= OnCarouselShifted;
            _vm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        _vm = DataContext as CoverFlowViewModel;
        if (_vm != null)
        {
            _vm.CarouselShifted += OnCarouselShifted;
            _vm.PropertyChanged += OnViewModelPropertyChanged;
            CrossfadeBackground(_vm.CenterArtworkPath);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(CoverFlowViewModel.CenterArtworkPath))
            CrossfadeBackground(_vm?.CenterArtworkPath);
    }

    // ── Slide ──────────────────────────────────────────────────────────────────

    /// <summary>The row moved <paramref name="step"/> slots: every card's content already
    /// swapped by binding, so start each card at the pose of the slot it CAME from and
    /// ease it into its own — the carousel row and the Cascade path both, from one loop
    /// (only one is visible). Step 0 (a track from outside the row) just snaps.</summary>
    internal void OnCarouselShifted(object? sender, int step)
    {
        if (step == 0 || TopLevel.GetTopLevel(this) is null)
        {
            FinishSlide();
            return;
        }

        _slideStep = step;
        _slideSeconds = CoverFlowCarouselGeometry.SlideDurationFor(step).TotalSeconds;
        _slideStartTicks = Stopwatch.GetTimestamp();
        IsSliding = true;
        ApplySlideFrame(0);
        QueueFrame();
    }

    private void QueueFrame()
    {
        if (_frameQueued) return;
        if (TopLevel.GetTopLevel(this) is not { } top) { FinishSlide(); return; }
        _frameQueued = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        _frameQueued = false;
        if (!IsSliding) return;
        var elapsed = (Stopwatch.GetTimestamp() - _slideStartTicks) / (double)Stopwatch.Frequency;
        var t = elapsed / _slideSeconds;
        if (t >= 1)
        {
            FinishSlide();
            return;
        }
        ApplySlideFrame(CoverFlowCarouselGeometry.Ease(t));
        QueueFrame();
    }

    /// <summary>Advance the slide to eased progress <paramref name="eased"/> (0 = every card
    /// still at the slot it came from, 1 = home). Internal so tests can step it.</summary>
    internal void ApplySlideFrame(double eased)
    {
        var remaining = 1 - eased;
        for (var i = 0; i < _slots.Length; i++)
            SetPose(i, SlotOf(i) + _slideStep * remaining);
        for (var i = 0; i < _cascadeCards.Length; i++)
            SetCascadePose(i, CascadeSlotOf(i) + _slideStep * remaining);
    }

    private void FinishSlide()
    {
        IsSliding = false;
        _slideStep = 0;
        for (var i = 0; i < _slots.Length; i++)
            SetPose(i, SlotOf(i));
        for (var i = 0; i < _cascadeCards.Length; i++)
            SetCascadePose(i, CascadeSlotOf(i));
    }

    // ── Input ──────────────────────────────────────────────────────────────────

    private void OnCarouselWheel(object? sender, PointerWheelEventArgs e)
    {
        var delta = Math.Abs(e.Delta.X) > Math.Abs(e.Delta.Y) ? e.Delta.X : e.Delta.Y;
        if (delta == 0 || _vm == null) return;
        e.Handled = true;
        if (IsSliding) return;
        // Wheel down / trackpad swipe left (negative) advances; up / right goes back.
        _vm.JumpToCommand.Execute(delta < 0 ? 1 : -1);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || _vm is not { ShowCarousel: true } || e.KeyModifiers != KeyModifiers.None) return;
        switch (e.Key)
        {
            case Key.Right:
                _vm.JumpToCommand.Execute(1);
                e.Handled = true;
                break;
            case Key.Left:
                _vm.JumpToCommand.Execute(-1);
                e.Handled = true;
                break;
        }
    }

    /// <summary>The caption's title link (09-17: the artwork itself no longer navigates).
    /// Side card → play that track (the row slides it to the centre); centre card → the
    /// album page.</summary>
    private void OnCardTitleClicked(object? sender, EventArgs e)
    {
        if (_vm == null || sender is not CoverFlowCard card) return;
        var slot = SlotOf(Array.IndexOf(_slots, card));
        if (slot == 0)
            _vm.GoToAlbumCommand.Execute(null);
        else
            _vm.JumpToCommand.Execute(slot);
    }

    // ── Background ─────────────────────────────────────────────────────────────

    /// <summary>Point the hidden copy at the new cover and swap opacities; the Opacity
    /// transitions on both images do the crossfade.</summary>
    private void CrossfadeBackground(string? path)
    {
        var (show, hide) = _backgroundOnA ? (BackgroundArtB, BackgroundArtA) : (BackgroundArtA, BackgroundArtB);
        if (string.Equals(show.SourcePath, path, StringComparison.Ordinal) && show.Opacity > 0) return;
        if (string.Equals(hide.SourcePath, path, StringComparison.Ordinal) && hide.Opacity > 0) return;

        show.SourcePath = path;
        show.Opacity = string.IsNullOrEmpty(path) ? 0 : BackgroundArtOpacity;
        hide.Opacity = 0;
        _backgroundOnA = !_backgroundOnA;
    }

    // ── Cascade layout (unchanged) ─────────────────────────────────────────────

    /// <summary>The stage's parent Grid insets the page by these (Margin="0,24,0,12"); the
    /// pile cancels them with negative margins so its cards are cut by the real page edge.</summary>
    private const double PageInsetTop = 24, PageInsetBottom = 12;

    /// <summary>Widest share of the page the pile square may take beside the text column.</summary>
    private const double PileMaxWidthShare = 0.62;

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        ApplyStageLayout(e.NewSize.Width, e.NewSize.Height);
    }

    internal void ApplyStageLayout(double width) => ApplyStageLayout(width, double.NaN);

    /// <summary>Wide: pile left, text column right. Narrow: text under the pile.</summary>
    internal void ApplyStageLayout(double width, double height)
    {
        var stacked = width < SideBySideMinWidth;
        ApplyPileSize(width, height, stacked);

        if (stacked == _isStacked) return;
        _isStacked = stacked;

        if (stacked)
        {
            Grid.SetRow(InfoStack, 1);
            Grid.SetColumn(InfoStack, 0);
            Grid.SetRowSpan(InfoStack, 1);
            Grid.SetColumnSpan(InfoStack, 2);
            Grid.SetRowSpan(PileViewbox, 1);
            Grid.SetColumnSpan(PileViewbox, 2);
            PileViewbox.HorizontalAlignment = HorizontalAlignment.Center;
            PileViewbox.Margin = new Thickness(0, -PageInsetTop, 0, 0);
            InfoStack.HorizontalAlignment = HorizontalAlignment.Center;
            InfoStack.Margin = new Thickness(24, 12, 24, 8);
        }
        else
        {
            Grid.SetRow(InfoStack, 0);
            Grid.SetColumn(InfoStack, 1);
            Grid.SetRowSpan(InfoStack, 2);
            Grid.SetColumnSpan(InfoStack, 1);
            Grid.SetRowSpan(PileViewbox, 2);
            Grid.SetColumnSpan(PileViewbox, 1);
            PileViewbox.HorizontalAlignment = HorizontalAlignment.Left;
            PileViewbox.Margin = new Thickness(0, -PageInsetTop, 0, -PageInsetBottom);
            InfoStack.HorizontalAlignment = HorizontalAlignment.Left;
            InfoStack.Margin = new Thickness(48, 0, 40, 0);
        }

        var align = stacked ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        TitleMarquee.HorizontalAlignment = align;
        ArtistLink.HorizontalAlignment = align;
        AlbumLink.HorizontalAlignment = align;
        UpNextStack.HorizontalAlignment = align;
    }

    /// <summary>
    /// The 1000×1000 design canvas must map onto a SQUARE so its right edge is the playing
    /// cover's right edge and its top/bottom are the page's. Beside the text the square is
    /// the full page height (insets cancelled), capped to a share of the width on very
    /// wide-and-short windows; stacked, it is whatever width allows.
    /// </summary>
    private void ApplyPileSize(double width, double height, bool stacked)
    {
        if (double.IsNaN(height) || height <= 0 || width <= 0)
        {
            PileViewbox.Width = double.NaN;
            PileViewbox.Height = double.NaN;
            return;
        }

        var side = stacked
            ? Math.Max(200, Math.Min(width - 32, height * 0.6))
            : Math.Max(200, Math.Min(height + PageInsetTop + PageInsetBottom, width * PileMaxWidthShare));
        PileViewbox.Width = side;
        PileViewbox.Height = side;
    }

    private void OnThemeVariantChanged(object? sender, EventArgs e)
    {
        ApplyThemeBlur();
    }

    private void ApplyThemeBlur()
    {
        var isLight = ActualThemeVariant == ThemeVariant.Light;

        foreach (var art in new[] { BackgroundArtA, BackgroundArtB })
            if (art.Effect is BlurEffect blur)
                blur.Radius = isLight ? 32 : 48;

        BackgroundOverlay.Opacity = isLight ? 0.30 : 0.45;
    }
}
