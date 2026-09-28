using System.Collections.Generic;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Media.Imaging;
using Noctis.Helpers;

namespace Noctis.Controls;

/// <summary>
/// Plays a looping animated cover by showing frames decoded (via LibVLC video callbacks)
/// into a <see cref="WriteableBitmap"/> in a plain <c>Image</c> element — so it composes
/// inside transparent windows, clips to its parent's rounded border, and never spawns a
/// native output window.
///
/// The decoding itself lives in <see cref="AnimatedCoverFeed"/>: every control showing the
/// same file shares one decoder, and a control only holds a lease on it while it could be
/// seen — attached, effectively visible, and in a shown, non-minimized window. Hidden mini
/// player forms, inactive Cover Flow modes and the main window's page behind an open mini
/// player therefore decode nothing.
/// </summary>
public partial class AnimatedCoverImage : UserControl
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverImage, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<AnimatedCoverImage, bool>(nameof(IsActive), defaultValue: false);

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private readonly Image _image;
    private AnimatedCoverFeed? _feed;
    private readonly HostWindowWatch _hostWindow;
    private bool _attached;
    private bool _wasEffectivelyVisible;

    // Attached instances (UI thread only). IsVisible=False anywhere up the tree does not
    // detach a control, and Avalonia keeps its IsEffectivelyVisibleChanged event internal,
    // so every IsVisible change in the app (a window hidden, a form, costume or page
    // collapsed) re-checks these few controls' effective visibility.
    private static readonly List<AnimatedCoverImage> s_attached = new();

    static AnimatedCoverImage()
    {
        IsVisibleProperty.Changed.AddClassHandler<Visual>((_, _) =>
        {
            for (var i = s_attached.Count - 1; i >= 0; i--)
                s_attached[i].OnVisibilityMayHaveChanged();
        });
    }

    public AnimatedCoverImage()
    {
        InitializeComponent();
        _image = this.FindControl<Image>("FrameImage")!;
        // Minimized/restored re-checks the lease. A hidden window (the main window while
        // the mini player is open) reaches us through the IsVisible class handler above.
        // Created before the handlers below so it has found the window by the time they run.
        _hostWindow = new HostWindowWatch(this, () => UpdateLease(linger: true));
        // Release on detach, re-lease on re-attach: a TabControl detaches the hosting
        // tab's content when you switch tabs and re-attaches it on return, and
        // Source/IsActive don't change across that, so nothing else restarts us. The
        // feed's frame cache makes the return trip seamless.
        AttachedToVisualTree += (_, _) =>
        {
            _attached = true;
            s_attached.Add(this);
            _wasEffectivelyVisible = IsEffectivelyVisible;
            UpdateLease(linger: true);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _attached = false;
            s_attached.Remove(this);
            UpdateLease(linger: true);
        };
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private void OnVisibilityMayHaveChanged()
    {
        var visible = IsEffectivelyVisible;
        if (visible == _wasEffectivelyVisible) return;
        _wasEffectivelyVisible = visible;
        UpdateLease(linger: true);
    }

    /// <summary>True while this control holds a lease on a decoder (tests, diagnostics).</summary>
    internal bool IsDecoding => _feed != null;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // A new source or the animation switched off: the old file is no longer wanted,
        // so its decoder (if this was the last viewer) stops at once and unlocks the file.
        if (change.Property == SourceProperty || change.Property == IsActiveProperty)
            UpdateLease(linger: false);
    }

    private void UpdateLease(bool linger)
    {
        var source = Source;
        var wanted = AnimatedCoverPolicy.ShouldDecode(
            IsActive,
            hasSource: !string.IsNullOrEmpty(source),
            isAttached: _attached,
            isEffectivelyVisible: IsEffectivelyVisible,
            windowShown: _hostWindow.Window?.IsVisible ?? true,
            windowMinimized: _hostWindow.IsMinimized);

        if (wanted && _feed != null && _feed.Source == source)
            return; // already showing it

        if (_feed != null)
        {
            var feed = _feed;
            _feed = null;
            // Let go of the shared frame: once the feed ends it belongs to the frame
            // cache, which may dispose it.
            _image.Source = null;
            feed.Release(this, linger);
        }

        if (!wanted || !File.Exists(source))
            return;

        _feed = AnimatedCoverFeed.Acquire(source!, this);
    }

    /// <summary>Called by the feed: show <paramref name="frame"/> (null = nothing yet, the
    /// static cover underneath shows through). Must not change leases.</summary>
    internal void ShowFrame(WriteableBitmap? frame)
    {
        if (!ReferenceEquals(_image.Source, frame))
            _image.Source = frame;
        _image.InvalidateVisual();
    }

    /// <summary>Called by the feed after it copied a new frame into the shared bitmap.</summary>
    internal void InvalidateFrame() => _image.InvalidateVisual();
}
