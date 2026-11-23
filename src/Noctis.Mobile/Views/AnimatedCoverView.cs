using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Mobile.Services;

namespace Noctis.Mobile.Views;

/// <summary>
/// The native side of an animated cover: the phone's video view, made anew each time Avalonia
/// hosts it (it disposes the last one when the host leaves the tree), and its muted looping player.
/// </summary>
public interface IAnimatedCoverSurface : IAnimatedCoverPlayer
{
    /// <summary>A new native view to host over the cover (AndroidViewControlHandle on the phone).</summary>
    IPlatformHandle CreateView(IPlatformHandle parent);

    /// <summary>The hosted view is going: let the player and the view's surface go first.</summary>
    void DestroyView();
}

/// <summary>
/// Now Playing's animated cover (Discord beta 11-23): a native video view hosted over the still
/// cover (NativeControlHost; Avalonia.Android 12.1.6 adds it to the AvaloniaView above the drawing
/// and places it by margins, AndroidNativeControlHostImpl.AndroidNativeControlAttachment.ShowInBounds).
/// <see cref="AnimatedCoverPresenter"/> decides when it shows: <see cref="Source"/> is the loop
/// (null: none) and <see cref="IsSeen"/> whether the cover is on screen and still. Hidden, the
/// still cover under it is all there is; shown, the video stays see-through until its first frame,
/// then fades in (the native side), so a slow or failed start is the still cover, never a blank.
/// Without <see cref="SurfaceFactory"/> (tests, desktop previews) it never shows.
/// </summary>
public sealed class AnimatedCoverView : NativeControlHost
{
    public static readonly StyledProperty<string?> SourceProperty =
        AvaloniaProperty.Register<AnimatedCoverView, string?>(nameof(Source));

    public static readonly StyledProperty<bool> IsSeenProperty =
        AvaloniaProperty.Register<AnimatedCoverView, bool>(nameof(IsSeen));

    /// <summary>Makes the phone's video surface; set by the Android head at start.</summary>
    public static Func<IAnimatedCoverSurface>? SurfaceFactory { get; set; }

    private IAnimatedCoverSurface? _surface;
    private AnimatedCoverPresenter? _presenter;

    public AnimatedCoverView()
    {
        IsVisible = false;
        IsHitTestVisible = false;
    }

    public string? Source
    {
        get => GetValue(SourceProperty);
        set => SetValue(SourceProperty, value);
    }

    public bool IsSeen
    {
        get => GetValue(IsSeenProperty);
        set => SetValue(IsSeenProperty, value);
    }

    /// <summary>The video is up over the cover (tests read it through the presenter's state).</summary>
    internal bool IsShown => _presenter?.IsShown == true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SourceProperty || change.Property == IsSeenProperty) _presenter?.Update(Source, IsSeen);
    }

    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        if (SurfaceFactory is not { } factory) return base.CreateNativeControlCore(parent);
        try
        {
            if (_surface is null)
            {
                _surface = factory();
                _presenter = new AnimatedCoverPresenter(_surface, static (delay, action) => DispatcherTimer.RunOnce(action, delay));
                _presenter.IsShownChanged += (_, _) => IsVisible = _presenter.IsShown;
            }
            var handle = _surface.CreateView(parent);
            _presenter!.Update(Source, IsSeen);
            return handle;
        }
        catch (Exception ex)
        {
            Noctis.Services.DebugLog.Write("AnimatedCover", $"no video view: {ex.GetType().Name}");
            _surface = null;
            _presenter = null;
            return base.CreateNativeControlCore(parent);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        _presenter?.Detach();
        _surface?.DestroyView();
        base.DestroyNativeControlCore(control);
    }
}
