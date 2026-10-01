using System.ComponentModel;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class ShellView : UserControl
{
    private IInsetsManager? _insets;

    public ShellView()
    {
        InitializeComponent();
        // The TopLevel pads its main view by the safe area by default; the shell pads each
        // layer itself (SafeArea) so the overlays' backgrounds run under the system bars,
        // and both together doubled the gap (~49 dp extra on top, 24 dp at the bottom).
        TopLevel.SetAutoSafeAreaPadding(this, false);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        TabContent.AddHandler(ScrollViewer.ScrollChangedEvent, OnContentScrollChanged);
        ChromeHost.SizeChanged += (_, _) => ApplyChromeLayout();
        DataContextChanged += OnDataContextChanged;
    }

    /// <summary>
    /// Avalonia.Android turns the Back key into an Escape KeyDown on the focused control and
    /// raises the activity's BackRequested only when nothing handled it. A slider keeps focus
    /// after a touch and marks Escape handled, so Back did nothing on Now Playing or Settings
    /// once a slider had been dragged (device run B12). Taking Escape here, in the tunnel
    /// phase, runs the shell's Back before any focused control sees the key; at the Library
    /// root it stays unhandled so the activity still falls through to finish.
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape || e.KeyModifiers != KeyModifiers.None) return;
        if (DataContext is ShellViewModel vm && vm.TryHandleBack()) e.Handled = true;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _insets = TopLevel.GetTopLevel(this)?.InsetsManager;
        if (_insets == null) return;   // headless tests: no system bars
        // Draw under the status and navigation bars (Android 15 enforces this for SDK 35+
        // anyway) and pad by the reported insets, so the layout is the same on every API level.
        _insets.DisplayEdgeToEdgePreference = true;
        _insets.SafeAreaChanged += OnSafeAreaChanged;
        ApplySafeArea(_insets.SafeAreaPadding);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (_insets != null) _insets.SafeAreaChanged -= OnSafeAreaChanged;
        _insets = null;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>
    /// Avalonia.Android 12.1.2 raises SafeAreaChanged once at startup, before the TopLevel
    /// knows its scaling, with the insets in physical pixels (0,128,0,63 on a 420 dpi phone
    /// instead of 0,48.8,0,24), and raises nothing when the scaling lands. The manager's
    /// SafeAreaPadding reads right once the view is sized, so re-read it on every resize
    /// (first layout, rotation); without this the tab content sat ~130 dp too low.
    /// </summary>
    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        if (_insets != null) ApplySafeArea(_insets.SafeAreaPadding);
    }

    /// <summary>Re-reads the manager rather than trusting <see cref="SafeAreaChangedArgs.SafeAreaPadding"/>,
    /// which can carry the physical-pixel insets described above; one source for both paths.</summary>
    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e)
    {
        if (_insets != null) ApplySafeArea(_insets.SafeAreaPadding);
    }

    /// <summary>Internal for tests, which have no insets manager.</summary>
    internal void ApplySafeArea(Thickness padding)
    {
        if (DataContext is ShellViewModel vm) vm.SafeArea = padding;
    }

    // ---- Glass tab bar -------------------------------------------------------------------
    // Expanded: the mini player capsule over a full-width tab capsule. Folded (scrolled down):
    // one row of a round current-tab button, the mini player and a round Search button. Sizes
    // are set here and the transitions attached once below animate every change between them.

    private const double TabHeight = 62, MiniHeight = 58, Bubble = 58, Gap = 10;
    private const double ChromeSideMargin = 14, ChromeBottomMargin = 8;
    private static readonly TimeSpan Morph = TimeSpan.FromMilliseconds(380);
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(200);

    private ShellViewModel? _vm;
    private bool _transitionsOn;

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null) _vm.PropertyChanged -= OnShellPropertyChanged;
        _vm = DataContext as ShellViewModel;
        if (_vm != null) _vm.PropertyChanged += OnShellPropertyChanged;
        ApplyChromeLayout();
    }

    private void OnShellPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShellViewModel.IsTabBarCollapsed) or nameof(ShellViewModel.IsMiniBarVisible)
            or nameof(ShellViewModel.SelectedTab) or nameof(ShellViewModel.SafeArea))
            ApplyChromeLayout();
    }

    /// <summary>A page's vertical scroll, from any list in the tab content (horizontal shelves
    /// report no vertical change and are skipped by the view model).</summary>
    private void OnContentScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_vm == null || e.Source is not ScrollViewer viewer || e.OffsetDelta.Y == 0) return;
        _vm.ReportContentScroll(viewer.Offset.Y, e.OffsetDelta.Y);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        ApplyChromeLayout();
    }

    private void AttachTransitions()
    {
        _transitionsOn = true;
        // An iOS-style settle: fast out of the gate, long soft landing.
        var ease = new CubicBezierEase(0.32, 0.72, 0, 1);
        TabCapsule.Transitions = new Transitions
        {
            new DoubleTransition { Property = WidthProperty, Duration = Morph, Easing = ease },
            new DoubleTransition { Property = HeightProperty, Duration = Morph, Easing = ease },
        };
        TabRow.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Quick, Easing = ease } };
        CollapsedTabButton.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Morph, Easing = ease } };
        SearchBubble.Transitions = new Transitions
        {
            new DoubleTransition { Property = GlassPanel.FadeProperty, Duration = Morph, Easing = ease },
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = Morph, Easing = ease },
        };
        SearchBubbleContent.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = Morph, Easing = ease } };
        MiniBar.Transitions = new Transitions { new ThicknessTransition { Property = MarginProperty, Duration = Morph, Easing = ease } };
        SelectionPill.Transitions = new Transitions
        {
            new TransformOperationsTransition { Property = RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(320), Easing = ease },
        };
    }

    /// <summary>How tall the expanded chrome stands (mini player + tabs, or tabs alone).</summary>
    internal static double ExpandedChromeHeight(bool miniVisible) => miniVisible ? TabHeight + Gap + MiniHeight : TabHeight;

    /// <summary>Lays out the bar for the view model's current state. Internal for tests.</summary>
    internal void ApplyChromeLayout()
    {
        if (_vm is not { } vm) return;
        var folded = vm.IsTabBarCollapsed;
        var mini = vm.IsMiniBarVisible;
        var width = ChromeHost.Bounds.Width;

        var expandedHeight = ExpandedChromeHeight(mini);
        ChromeHost.Height = folded ? Bubble : expandedHeight;
        // Lists end this far below their last row so it can scroll clear of the bar; the
        // expanded height, so the end of a list never hides when the bar unfolds over it.
        Resources["ShellBottomInset"] = new Thickness(0, 0, 0, expandedHeight + ChromeBottomMargin + vm.SafeArea.Bottom + 16);
        BottomScrim.Height = expandedHeight + ChromeBottomMargin + vm.SafeArea.Bottom + 36;

        if (width <= 0) return;

        TabCapsule.Width = folded ? Bubble : width;
        TabCapsule.Height = folded ? Bubble : TabHeight;
        TabRow.Width = width;
        TabRow.Opacity = folded ? 0 : 1;
        TabRow.IsHitTestVisible = !folded;
        CollapsedTabButton.Width = Bubble;
        CollapsedTabButton.Height = Bubble;
        CollapsedTabButton.Opacity = folded ? 1 : 0;
        CollapsedTabButton.IsHitTestVisible = folded;

        SearchBubble.Width = Bubble;
        SearchBubble.Height = Bubble;
        SearchBubble.Fade = folded ? 1 : 0;
        SearchBubble.RenderTransform = TransformOperations.Parse(folded ? "scale(1)" : "scale(0.6)");
        SearchBubbleContent.Opacity = folded ? 1 : 0;
        SearchBubble.IsHitTestVisible = folded;

        MiniBar.Height = MiniHeight;
        MiniBar.Margin = folded ? new Thickness(Bubble + Gap, 0, Bubble + Gap, 0) : new Thickness(0, 0, 0, TabHeight + Gap);
        MiniNextButton.IsVisible = !folded;

        var slot = width / 4;
        var index = vm.SelectedTab switch
        {
            MobileTab.Home => 0,
            MobileTab.Playlists => 1,
            MobileTab.Library => 2,
            _ => 3,
        };
        SelectionPill.Width = slot - 8;
        SelectionPill.Height = TabHeight - 10;
        SelectionPill.RenderTransform = TransformOperations.Parse($"translateX({(index * slot + 4).ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");

        // Once the bar has been laid out at its real width, so it appears in place rather
        // than animating in from zero.
        if (!_transitionsOn && IsLoaded) AttachTransitions();
    }
}
