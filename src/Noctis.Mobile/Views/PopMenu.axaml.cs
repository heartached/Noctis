using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Every ⋯ menu on the phone (<see cref="ShellViewModel.PopMenu"/>: a song's, album's, playlist's
/// or artist's actions, or the Library tab's): the glass card grows out of <see cref="Anchor"/>,
/// the ⋯ button tapped (or the row or tile held), as Apple's context menus and the desktop's
/// right-click menu do: a short spring from 0.85 round the button, the frost, the rows and a light
/// dim behind fading in together; closing heads back into the button, quicker (the numbers and
/// the old ones beside them: the constants below). It opens down from the button when it fits
/// above the mini player and tab bar, else up from it, else wherever it has the most room, its
/// rows then scrolling.
/// Like the desktop's glass menus it fades the GlassPanel's own <see cref="GlassPanel.Fade"/> and
/// the body's Opacity, never an ancestor's Opacity, which the GPU backend does not apply to the
/// frost. Only RenderTransform and those (and the dim's Opacity) move per frame.
/// </summary>
public partial class PopMenu : UserControl
{
    // The phone's pop (tester 11-04: the desktop numbers read awkward on a phone). Measured old
    // -> new: open 280 ms easeOutQuint (0.22,1,0.36,1) from 0.9, fade 160 ms, rows staggered in
    // over up to 400 ms -> 340 ms spring (PopSpringEase, zeta 0.75: 90 % in ~85 ms, a 2.8 %
    // overshoot, settled by the end) from 0.85, the frost, the content and the dim in over
    // 160 ms on the desktop dialogs' long ease-out (0.16,1,0.3,1), rows fading with the card and
    // never moving on their own. Close 150 ms accelerate (0.4,0,1,1) to 0.94 -> 200 ms back to
    // 0.85 at the anchor: the shrink on a quick ease-out (0.2,0,0,1), so it visibly heads home,
    // the fade on the accelerate, so it is gone as it lands.
    internal const double PopScale = 0.85;
    internal const double PopOpenMs = 340;
    internal const double PopFadeMs = 160;
    internal const double PopCloseMs = 200;
    internal const double PopCloseScale = 0.85;
    /// <summary>The bottom sheets' pose (Add to Playlist, Rename, the album note): the desktop
    /// dialogs' (PillDialogHost) rise and grow, not the menu's deep scale, which pinched a
    /// full-width card in at its sides.</summary>
    internal static readonly PopPose SheetPose = new(0.94, 24);
    internal static readonly PopPose MenuPose = new(PopScale, 0);
    internal static readonly Avalonia.Animation.Easings.Easing PopEase = new PopSpringEase();
    internal static readonly Avalonia.Animation.Easings.Easing PopFadeEase = new CubicBezierEase(0.16, 1, 0.3, 1);
    internal static readonly Avalonia.Animation.Easings.Easing PopCloseEase = new CubicBezierEase(0.2, 0, 0, 1);
    internal static readonly Avalonia.Animation.Easings.Easing PopCloseFadeEase = new CubicBezierEase(0.4, 0, 1, 1);

    /// <summary>The card's width for an item's menu, and for the Library's three rows.</summary>
    internal const double ItemWidth = 250, LibraryWidth = 250;
    /// <summary>The card keeps this far from the screen's sides and the bottom chrome, and
    /// <see cref="TopGap"/> below the status bar (a header ⋯ sits just under it, and the card
    /// covers its button).</summary>
    internal const double EdgeGap = 12, TopGap = 4;
    // The tab bar's own bottom margin plus a gap above it (ShellView's ChromeBottomMargin + 8).
    private const double ChromeGap = 16;
    /// <summary>How far left of Now Playing's artist text the links bubble starts (Apple's: the
    /// glyph column sits out to the left of the text, the labels inside it).</summary>
    internal const double LinksLead = 12;
    /// <summary>The quality card's width, and the gap it keeps from the badge it grows out of.</summary>
    internal const double CardWidth = 260, CardGap = 10;

    private ShellViewModel? _shell;
    private TopLevel? _top;
    // The control tapped or held just now: a menu opened by its command grows out of it.
    private Control? _pending;
    // Bumped by every open and close: a deferred step of an older run sees it changed and stops.
    private int _run;
    // Now Playing's artist links: the bubble rises out of the text instead of covering it.
    private bool _rises;
    // Now Playing's quality card: centred on the badge, above it (below when there is no room).
    private bool _centred;

    public PopMenu()
    {
        InitializeComponent();
    }

    /// <summary>What the open card grew out of: the ⋯ button, or the held row or tile; null when
    /// the menu was opened without a tap (it then opens centred).</summary>
    public Control? Anchor { get; private set; }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _top = TopLevel.GetTopLevel(this);
        _top?.AddHandler(Button.ClickEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        _top?.AddHandler(LongPress.FiredEvent, OnTapped, RoutingStrategies.Bubble, handledEventsToo: true);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _top?.RemoveHandler(Button.ClickEvent, OnTapped);
        _top?.RemoveHandler(LongPress.FiredEvent, OnTapped);
        _top = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// A button's Click (and a long press) is raised before its command runs, so the menu that
    /// command opens finds what was tapped here. It is kept for this dispatcher turn only: a menu
    /// opened later by anything else must not grow out of an old tap.
    /// </summary>
    private void OnTapped(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Control control || control == this || this.IsVisualAncestorOf(control)) return;
        _pending = control;
        Dispatcher.UIThread.Post(() =>
        {
            if (_pending == control) _pending = null;
        }, DispatcherPriority.Background);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_shell != null) _shell.PropertyChanged -= OnShellChanged;
        _shell = DataContext as ShellViewModel;
        if (_shell != null) _shell.PropertyChanged += OnShellChanged;
        Sync();
    }

    private void OnShellChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShellViewModel.PopMenu)) Sync();
    }

    private void Sync()
    {
        if (_shell?.PopMenu is { } menu)
        {
            if (!IsVisible || !IsHitTestVisible || !ReferenceEquals(MenuContent.Content, menu)) Open(menu);
        }
        else Close();
    }

    private void OnCatcherPressed(object? sender, PointerPressedEventArgs e)
    {
        _shell?.CloseSheet();
        e.Handled = true;
    }

    private void Open(object menu)
    {
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        try { OpenCore(menu); }
        finally { Services.FrameStallMonitor.AddTime("menu open", System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds); }
    }

    private void OpenCore(object menu)
    {
        var run = ++_run;
        Anchor = _pending is { } pending && pending.IsAttachedToVisualTree() && pending.IsEffectivelyVisible ? pending : null;
        _pending = null;
        Scope.RequestedThemeVariant = ThemeOf(Anchor);
        Card.Width = menu switch
        {
            LibraryMenuViewModel or TrackLinksMenuViewModel or OptionsMenuViewModel { HasItems: true } => LibraryWidth,
            QualityCardViewModel => CardWidth,
            _ => ItemWidth,
        };
        _rises = menu is TrackLinksMenuViewModel;
        _centred = menu is QualityCardViewModel;
        MenuContent.Content = menu;
        Body.Offset = default;
        IsVisible = true;
        IsHitTestVisible = true;
        // Built and placed now, not on the next layout pass: a control enables its transitions
        // when it joins the visual tree, and the card only joins it the first time the menu is
        // laid out, so the first open after launch snapped to its end (headless, 10-13).
        UpdateLayout();
        Place();
        UpdateLayout();

        // Start values with no transitions (they snap), the targets once that pose has been
        // drawn. The first frame of a freshly built menu is the slow one: a run started inside it
        // (a dispatcher post, as before) had covered most of its course before anything was seen,
        // so the card all but appeared at full size and then crept the last few pixels.
        PopFrom(Card, Glass, Body, Origin(), MenuPose, Dim);
        AfterFirstFrame(this, () =>
        {
            if (run == _run) PopIn(Card, Glass, Body, Dim);
        });
    }

    /// <summary>
    /// The card wears the look of what it opened from: on a page painted in its cover's colour
    /// (album, artist) the light glass over a light tint and the dark glass over a dark one, as
    /// the page's own text flips; else its theme (Now Playing's dark scope, or the app's).
    /// </summary>
    private ThemeVariant? ThemeOf(Control? anchor)
    {
        if (anchor == null)
            return _shell is { IsNowPlayingOpen: true, IsQueueOpen: false } ? ThemeVariant.Dark : null;
        var tinted = anchor.GetSelfAndVisualAncestors().OfType<Control>().Select(c => c.DataContext).OfType<ITintedPage>().FirstOrDefault();
        if (tinted?.Tint is { HasTint: true } tint) return tint.IsLight ? ThemeVariant.Light : ThemeVariant.Dark;
        return anchor.ActualThemeVariant;
    }

    /// <summary>The open motion reversed: inert at once (a fading card must never take a tap),
    /// then shrink toward the button while fading, and hide once it is gone.</summary>
    private void Close()
    {
        if (!IsVisible) return;
        var run = ++_run;
        IsHitTestVisible = false;
        PopOut(Card, Glass, Body, MenuPose, Dim);

        DispatcherTimer.RunOnce(() =>
        {
            if (run != _run) return;
            IsVisible = false;
            // The menu keeps its content: the next menu of the same kind (every song's ⋯ is
            // one) recycles the built view and only rebinds it. Cleared, every open built the
            // card's rows again: ~200 ms of the tap's frame on the emulator (11-06).
            Anchor = null;
            PopDone(Card, Glass, Body, Dim);
        }, TimeSpan.FromMilliseconds(PopCloseMs));
    }

    /// <summary>
    /// The pop's start, snapped (no transitions): <paramref name="card"/> at
    /// <paramref name="pose"/> round <paramref name="origin"/>, its glass, <paramref name="body"/>
    /// and <paramref name="scrim"/> faded out. Shared with the bottom sheets (ContextSheet,
    /// PromptSheet, AlbumNoteSheet), so every glass pop-up opens and closes alike.
    /// </summary>
    internal static void PopFrom(Control card, GlassPanel glass, Control body, RelativePoint origin, PopPose pose, Control? scrim = null)
    {
        PopDone(card, glass, body, scrim);
        glass.Fade = 0;
        body.Opacity = 0;
        if (scrim != null) scrim.Opacity = 0;
        card.RenderTransform = pose.Transform();
        card.RenderTransformOrigin = origin;
    }

    /// <summary>The pop: the card springs to full size (<see cref="PopOpenMs"/>) while the glass,
    /// the body and the dim behind fade in together (<see cref="PopFadeMs"/>). Run once the
    /// <see cref="PopFrom"/> pose has been drawn (<see cref="AfterFirstFrame"/>). Only
    /// RenderTransform, Opacity and the glass's Fade move: no layout per frame.</summary>
    internal static void PopIn(Control card, GlassPanel glass, Control body, Control? scrim = null)
    {
        var fade = TimeSpan.FromMilliseconds(PopFadeMs);
        glass.Transitions = new Transitions { new DoubleTransition { Property = GlassPanel.FadeProperty, Duration = fade, Easing = PopFadeEase } };
        body.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = fade, Easing = PopFadeEase } };
        if (scrim != null)
            scrim.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = fade, Easing = PopFadeEase } };
        card.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(PopOpenMs), Easing = PopEase },
        };
        glass.Fade = 1;
        body.Opacity = 1;
        if (scrim != null) scrim.Opacity = 1;
        card.RenderTransform = PopPose.Rest.Transform();
    }

    /// <summary>The pop reversed, quicker (<see cref="PopCloseMs"/>): back to
    /// <paramref name="pose"/> toward where it grew from on a quick ease-out while the glass, the
    /// body and the dim fade on an accelerate; hide the card once it is done.</summary>
    internal static void PopOut(Control card, GlassPanel glass, Control body, PopPose pose, Control? scrim = null)
    {
        var duration = TimeSpan.FromMilliseconds(PopCloseMs);
        glass.Transitions = new Transitions { new DoubleTransition { Property = GlassPanel.FadeProperty, Duration = duration, Easing = PopCloseFadeEase } };
        body.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = PopCloseFadeEase } };
        if (scrim != null)
            scrim.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = duration, Easing = PopCloseFadeEase } };
        card.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = duration, Easing = PopCloseEase },
        };
        glass.Fade = 0;
        body.Opacity = 0;
        if (scrim != null) scrim.Opacity = 0;
        card.RenderTransform = pose.Transform();
    }

    /// <summary>Drops the pop's transitions once it has finished.</summary>
    internal static void PopDone(Control card, GlassPanel glass, Control body, Control? scrim = null)
    {
        card.Transitions = null;
        glass.Transitions = null;
        body.Transitions = null;
        if (scrim != null) scrim.Transitions = null;
    }

    /// <summary>
    /// Runs <paramref name="action"/> two frames on (the one that lays out and draws the start
    /// pose, then the next: Motion's rule), each request marking <paramref name="control"/> for
    /// redraw so a still screen still ticks. Off screen, after the dispatcher's layout work; and
    /// should no frame come (the app just went to the background), after half a second anyway.
    /// </summary>
    internal static void AfterFirstFrame(Control control, Action action)
    {
        var done = false;
        void Once()
        {
            if (done) return;
            done = true;
            action();
        }
        if (TopLevel.GetTopLevel(control) is not { } top)
        {
            Dispatcher.UIThread.Post(Once, DispatcherPriority.Background);
            return;
        }
        top.RequestAnimationFrame(_ =>
        {
            top.RequestAnimationFrame(_ => Once());
            control.InvalidateVisual();
        });
        control.InvalidateVisual();
        DispatcherTimer.RunOnce(Once, TimeSpan.FromMilliseconds(500));
    }

    /// <summary>
    /// Apple's placement: the card covers the button, opening down from its top edge when it
    /// fits above the bottom chrome, else up from its bottom edge (a ⋯ low on the screen, Now
    /// Playing's), aligned to the button's right edge on the right half of the screen and its
    /// left edge on the left. Neither fits (a short window): it takes the side with more room
    /// and its rows scroll. It never runs under the status bar, the mini player or the tab bar.
    /// Now Playing's artist links are Apple's bubble instead: just above the artist, its lower
    /// left corner by the text, over the title; below the artist when there is no room above.
    /// Now Playing's quality card is centred on its badge, a gap above it (over the cover), below
    /// it when there is no room above.
    /// </summary>
    private void Place()
    {
        var host = this.GetVisualParent();
        var size = host?.Bounds.Size ?? default;
        if (size.Width <= 0 || size.Height <= 0) size = Bounds.Size;
        var top = (_shell?.SafeArea.Top ?? 0) + TopGap;
        var bottom = Math.Max(top + 120, BottomLimit(size.Height));
        Card.MaxHeight = bottom - top;
        // DesiredSize counts the margin: the last open's placement must not make it look taller.
        Card.Margin = default;
        Card.Measure(new Size(Card.Width, double.PositiveInfinity));
        var width = Card.Width;
        var height = Math.Min(Card.DesiredSize.Height, bottom - top);

        double x, y;
        if (Anchor is { } anchor && host != null && anchor.TranslatePoint(default, host) is { } at)
        {
            var a = new Rect(at, anchor.Bounds.Size);
            x = a.Center.X >= size.Width / 2 ? a.Right - width : a.Left;
            if (_centred)
            {
                x = a.Center.X - width / 2;
                y = a.Top - CardGap - height >= top ? a.Top - CardGap - height : Math.Min(a.Bottom + CardGap, bottom - height);
            }
            else if (_rises)
            {
                x = a.Left - LinksLead;
                y = a.Top - height >= top ? a.Top - height : a.Bottom;
            }
            else if (a.Top + height <= bottom) y = Math.Max(top, a.Top);
            else if (Math.Min(a.Bottom, bottom) - height >= top) y = Math.Min(a.Bottom, bottom) - height;
            else y = bottom - a.Top >= a.Bottom - top ? Math.Max(top, a.Top) : top;
        }
        else
        {
            x = (size.Width - width) / 2;
            y = top + (bottom - top - height) / 2;
        }
        x = Math.Clamp(x, EdgeGap, Math.Max(EdgeGap, size.Width - EdgeGap - width));
        y = Math.Clamp(y, top, Math.Max(top, bottom - height));
        // On the layout's pixel grid, as layout rounding would put it: Origin() works from this
        // margin, so a half-pixel placement (a card centred on an odd-width badge) would grow
        // from half a pixel beside the anchor's centre.
        if (Card.UseLayoutRounding && LayoutHelper.GetLayoutScale(this) is var scale and > 0)
        {
            x = LayoutHelper.RoundLayoutValue(x, scale);
            y = LayoutHelper.RoundLayoutValue(y, scale);
        }
        Card.MaxHeight = bottom - y;
        Card.Margin = new Thickness(x, y, 0, 0);
    }

    /// <summary>How far down the card may reach: above the tab bar and mini player on a page or
    /// the Queue (whose tab bar stays up), above the gesture bar over Now Playing and Lyrics.</summary>
    private double BottomLimit(double height)
    {
        if (_shell is not { } shell) return height - EdgeGap;
        var safe = shell.SafeArea.Bottom;
        if ((shell.IsNowPlayingOpen || shell.IsLyricsOpen) && !shell.IsQueueOpen) return height - safe - EdgeGap;
        return height - safe - ShellView.ExpandedChromeHeight(shell.IsMiniBarVisible && !shell.IsQueueOpen) - ChromeGap;
    }

    /// <summary>The anchor's centre inside the card: the point it grows from and shrinks to.</summary>
    private RelativePoint Origin()
    {
        var card = new Rect(Card.Margin.Left, Card.Margin.Top, Card.Width, Card.Bounds.Height > 0 ? Card.Bounds.Height : Card.DesiredSize.Height);
        if (Anchor is not { } anchor || this.GetVisualParent() is not { } host || anchor.TranslatePoint(default, host) is not { } at || card.Width <= 0)
            return RelativePoint.Center;
        var x = at.X + anchor.Bounds.Width / 2 - card.X;
        var y = at.Y + anchor.Bounds.Height / 2 - card.Y;
        return new RelativePoint(Math.Clamp(x, 0, card.Width), Math.Clamp(y, 0, card.Height), RelativeUnit.Absolute);
    }
}

/// <summary>A pop-up's resting-apart pose: how small it is and how far below where it lands.</summary>
internal readonly record struct PopPose(double Scale, double Rise)
{
    public static readonly PopPose Rest = new(1, 0);

    /// <summary>The same operation list at every pose, so the transition interpolates them.</summary>
    public ITransform Transform() => TransformOperations.Parse(
        $"translateY({Px(Rise)}px) scale({Px(Scale)})");

    private static string Px(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
}
