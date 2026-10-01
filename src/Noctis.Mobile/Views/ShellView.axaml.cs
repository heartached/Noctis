using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
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
}
