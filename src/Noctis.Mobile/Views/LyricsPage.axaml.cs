using System.Collections.Specialized;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

public partial class LyricsPage : UserControl
{
    /// <summary>Fraction of the viewport the active line is anchored at: the desktop page's
    /// ratio (LyricsView.PageAnchorRatio). The desktop is the reference, not the spec's
    /// golden ratio.</summary>
    internal const double AnchorRatio = 0.32;

    private LyricsPageViewModel? _lyrics;
    private bool _frameLoopRunning;
    private bool _gliding;
    private double _glideFrom;
    private double _glideTo;
    private double _glideStartMs;

    public LyricsPage()
    {
        InitializeComponent();
        LyricsScroll.SizeChanged += (_, _) => UpdateEdgePadding();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_lyrics != null)
        {
            _lyrics.PropertyChanged -= OnLyricsChanged;
            _lyrics.Lines.CollectionChanged -= OnLinesChanged;
        }
        _lyrics = (DataContext as ShellViewModel)?.Lyrics;
        if (_lyrics != null)
        {
            _lyrics.PropertyChanged += OnLyricsChanged;
            _lyrics.Lines.CollectionChanged += OnLinesChanged;
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != IsVisibleProperty || !IsVisible) return;
        EnsureFrameLoop();
        // After layout, so the containers exist and the extent is measured.
        Dispatcher.UIThread.Post(() => ScrollToLine(_lyrics?.ActiveLineIndex ?? -1, animate: false), DispatcherPriority.Loaded);
    }

    private void OnLyricsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LyricsPageViewModel.WantsFrames))
            EnsureFrameLoop();
        else if (e.PropertyName == nameof(LyricsPageViewModel.ActiveLineIndex) && IsVisible)
            ScrollToLine(_lyrics!.ActiveLineIndex, animate: true);
    }

    /// <summary>A new track's lyrics start at the top.</summary>
    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Reset) return;
        _gliding = false;
        LyricsScroll.Offset = default;
    }

    /// <summary>Top and bottom room so the first and last lines can reach the anchor
    /// (the desktop pads 10% above and most of the viewport below).</summary>
    private void UpdateEdgePadding()
    {
        var height = LyricsScroll.Viewport.Height;
        LyricsList.Margin = new Thickness(24, height * 0.10, 24, height * 0.60);
    }

    private bool KeepsFrames => IsVisible && (_gliding || _lyrics?.WantsFrames == true);

    private void EnsureFrameLoop()
    {
        if (_frameLoopRunning || !KeepsFrames) return;
        if (TopLevel.GetTopLevel(this) is not { } top) return;
        _frameLoopRunning = true;
        top.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan _)
    {
        var now = LyricsPageViewModel.NowMs();
        if (_gliding) StepGlide(now);
        if (_lyrics?.WantsFrames == true && IsVisible) _lyrics.OnFrame(now);

        if (KeepsFrames && TopLevel.GetTopLevel(this) is { } top)
            top.RequestAnimationFrame(OnFrame);
        else
            _frameLoopRunning = false;
    }

    /// <summary>
    /// Puts line <paramref name="index"/> on the anchor, gliding with the desktop's line
    /// motion or jumping. The list is virtualised, so an off-screen line is realised first.
    /// Internal for tests.
    /// </summary>
    internal void ScrollToLine(int index, bool animate)
    {
        if (index < 0 || _lyrics == null || index >= _lyrics.Lines.Count) return;
        if (LyricsList.ContainerFromIndex(index) is not Control container)
        {
            LyricsList.ScrollIntoView(index);
            LyricsList.UpdateLayout();
            if (LyricsList.ContainerFromIndex(index) is not Control realised) return;
            container = realised;
        }

        if (container.TranslatePoint(new Point(0, 0), LyricsList) is not { } origin) return;
        // Coordinates relative to the list exclude its margin; the scroll extent includes it.
        var target = LyricsScrollAnchor.ComputeAnchorOffset(
            origin.Y + LyricsList.Margin.Top, container.Bounds.Height,
            LyricsScroll.Viewport.Height, LyricsScroll.Extent.Height, AnchorRatio);

        if (!animate || Math.Abs(target - LyricsScroll.Offset.Y) < 2)
        {
            _gliding = false;
            LyricsScroll.Offset = new Vector(0, target);
            return;
        }

        _glideFrom = LyricsScroll.Offset.Y;
        _glideTo = target;
        _glideStartMs = LyricsPageViewModel.NowMs();
        _gliding = true;
        EnsureFrameLoop();
    }

    private void StepGlide(double nowMs)
    {
        var progress = Math.Clamp((nowMs - _glideStartMs) / LineMotion.DurationMs, 0, 1);
        LyricsScroll.Offset = new Vector(0, _glideFrom + (_glideTo - _glideFrom) * LineMotion.Ease(progress));
        if (progress >= 1) _gliding = false;
    }
}
