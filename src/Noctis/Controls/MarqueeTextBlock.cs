using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Noctis.Controls;

/// <summary>
/// A self-contained text block that scrolls horizontally when the text overflows
/// its viewport. Used in context menu / flyout headers for long track titles and
/// artist names. Each instance manages its own animation lifecycle via
/// AttachedToVisualTree / DetachedFromVisualTree.
/// </summary>
public class MarqueeTextBlock : UserControl
{
    // ── Global enable switches (set by SettingsViewModel) ──
    public static bool GlobalCoverFlowScrollEnabled { get; set; } = true;
    public static bool GlobalCoverFlowArtistScrollEnabled { get; set; } = true;
    public static bool GlobalCoverFlowAlbumScrollEnabled { get; set; } = true;
    public static bool GlobalLyricsTitleScrollEnabled { get; set; } = true;
    public static bool GlobalLyricsArtistScrollEnabled { get; set; } = true;
    public static bool GlobalMiniPlayerTitleScrollEnabled { get; set; } = true;
    public static bool GlobalMiniPlayerAlbumScrollEnabled { get; set; } = true;

    // The switches above are plain statics with no change notification, so flipping
    // a toggle ON did nothing for already-attached instances until the text or
    // layout next changed. Raised after the settings code rewrites the statics;
    // each attached instance re-evaluates. Instances subscribe in OnAttached and
    // unsubscribe in OnDetached so recycled controls are not kept alive.
    private static event EventHandler? GlobalSettingsChanged;

    public static void NotifyGlobalSettingsChanged() =>
        GlobalSettingsChanged?.Invoke(null, EventArgs.Empty);

    private const double OverflowThreshold = 1.0;
    private const double ScrollSpeed = 26.0;
    /// <summary>How long the text rests at its start position between laps. The marquee
    /// does a full loop (out the left edge, back in from the right), lands at the start,
    /// holds for this long, then goes around again.</summary>
    private static readonly TimeSpan RestPause = TimeSpan.FromSeconds(7);

    // ── Styled properties ──

    public static readonly StyledProperty<string?> TextProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, string?>(nameof(Text));

    public new static readonly StyledProperty<double> FontSizeProperty =
        TextBlock.FontSizeProperty.AddOwner<MarqueeTextBlock>();

    public new static readonly StyledProperty<FontWeight> FontWeightProperty =
        TextBlock.FontWeightProperty.AddOwner<MarqueeTextBlock>();

    public new static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextBlock.ForegroundProperty.AddOwner<MarqueeTextBlock>();

    public static readonly StyledProperty<double> MaxDisplayWidthProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, double>(nameof(MaxDisplayWidth), 240);

    public static readonly StyledProperty<bool> IsForArtistProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsForArtist));

    public static readonly StyledProperty<bool> IsForAlbumProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsForAlbum));

    public static readonly StyledProperty<bool> IsCoverFlowProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsCoverFlow));

    public static readonly StyledProperty<bool> IsLyricsPageProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsLyricsPage));

    public static readonly StyledProperty<bool> IsMiniPlayerProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, bool>(nameof(IsMiniPlayer));

    public static readonly StyledProperty<Control?> InlineContentProperty =
        AvaloniaProperty.Register<MarqueeTextBlock, Control?>(nameof(InlineContent));

    public string? Text
    {
        get => GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public new double FontSize
    {
        get => GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public new FontWeight FontWeight
    {
        get => GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    public new IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public double MaxDisplayWidth
    {
        get => GetValue(MaxDisplayWidthProperty);
        set => SetValue(MaxDisplayWidthProperty, value);
    }

    /// <summary>
    /// When true, uses the artist scroll setting; when false, uses the title scroll setting.
    /// </summary>
    public bool IsForArtist
    {
        get => GetValue(IsForArtistProperty);
        set => SetValue(IsForArtistProperty, value);
    }

    /// <summary>
    /// When true, uses the album scroll setting instead of title/artist settings.
    /// </summary>
    public bool IsForAlbum
    {
        get => GetValue(IsForAlbumProperty);
        set => SetValue(IsForAlbumProperty, value);
    }

    /// <summary>
    /// When true, uses the CoverFlow scroll setting instead of menu settings.
    /// </summary>
    public bool IsCoverFlow
    {
        get => GetValue(IsCoverFlowProperty);
        set => SetValue(IsCoverFlowProperty, value);
    }

    /// <summary>
    /// When true, uses the lyrics page scroll settings instead of menu settings.
    /// </summary>
    public bool IsLyricsPage
    {
        get => GetValue(IsLyricsPageProperty);
        set => SetValue(IsLyricsPageProperty, value);
    }

    /// <summary>
    /// When true, uses the mini player scroll settings instead of menu settings.
    /// Combine with <see cref="IsForAlbum"/> to pick the album vs. title setting.
    /// </summary>
    public bool IsMiniPlayer
    {
        get => GetValue(IsMiniPlayerProperty);
        set => SetValue(IsMiniPlayerProperty, value);
    }

    /// <summary>
    /// Optional inline content (e.g. explicit badge) that scrolls together with the text.
    /// </summary>
    public Control? InlineContent
    {
        get => GetValue(InlineContentProperty);
        set => SetValue(InlineContentProperty, value);
    }

    // ── Internal controls ──

    private readonly Border _viewport;
    private readonly TextBlock _textBlock;
    private readonly StackPanel _contentPanel;
    private readonly TranslateTransform _transform;

    // ── Animation state ──
    // Frame-clock driven (TopLevel.RequestAnimationFrame), NOT a DispatcherTimer: a 16 ms
    // timer defaults to Background priority (starved by any layout/render work) and beats
    // against the ~16.7 ms vsync, so some frames got two steps and some none — visible
    // stutter. Same migration the lyrics scroll and SmoothScrollBehavior already made.

    private bool _isRunning;
    private bool _isFrameQueued;
    private long _lastFrameTimestamp;
    private int _lapGeneration;
    private double _overflow;
    private double _textWidth;
    private double _viewportWidth;
    private double _offset;

    public MarqueeTextBlock()
    {
        _transform = new TranslateTransform();
        _textBlock = new TextBlock
        {
            MaxLines = 1,
            TextTrimming = TextTrimming.None
        };

        _contentPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            RenderTransform = _transform
        };
        _contentPanel.Children.Add(_textBlock);

        _viewport = new Border
        {
            ClipToBounds = true,
            Child = _contentPanel,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        Content = _viewport;

        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;

        // Overflow is computed from the viewport width, but nothing recomputed it when
        // that width changed — only Text/FontSize/FontWeight/MaxDisplayWidth/InlineContent
        // did. Resizing the window (the lyrics page and mini player size the marquee from
        // the layout) left _overflow stale, so the text either scrolled past the wrong end
        // or stopped scrolling despite now overflowing.
        _viewport.GetObservable(BoundsProperty).Subscribe(new AnonymousObserver(_ => ResetAndRecalc()));
    }

    /// <summary>Minimal IObserver so the control can react to its own bounds changes.</summary>
    private sealed class AnonymousObserver : IObserver<Rect>
    {
        private readonly Action<Rect> _onNext;
        public AnonymousObserver(Action<Rect> onNext) => _onNext = onNext;
        public void OnCompleted() { }
        public void OnError(Exception error) { }
        public void OnNext(Rect value) => _onNext(value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == TextProperty)
        {
            _textBlock.Text = Text;
            ResetAndRecalc();
        }
        else if (change.Property == FontSizeProperty)
        {
            _textBlock.FontSize = FontSize;
            ResetAndRecalc();
        }
        else if (change.Property == FontWeightProperty)
        {
            _textBlock.FontWeight = FontWeight;
            ResetAndRecalc();
        }
        else if (change.Property == ForegroundProperty)
        {
            _textBlock.Foreground = Foreground;
        }
        else if (change.Property == MaxDisplayWidthProperty)
        {
            _viewport.MaxWidth = MaxDisplayWidth;
            ResetAndRecalc();
        }
        else if (change.Property == InlineContentProperty)
        {
            if (change.OldValue is Control old)
                _contentPanel.Children.Remove(old);
            if (change.NewValue is Control newCtrl)
                _contentPanel.Children.Add(newCtrl);
            ResetAndRecalc();
        }
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _viewport.MaxWidth = MaxDisplayWidth;
        _textBlock.Text = Text;
        _textBlock.FontSize = FontSize;
        _textBlock.FontWeight = FontWeight;
        _textBlock.Foreground = Foreground;

        GlobalSettingsChanged += OnGlobalSettingsChanged;

        // Schedule measurement after layout
        Dispatcher.UIThread.Post(RecalcAndStart, DispatcherPriority.Render);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        GlobalSettingsChanged -= OnGlobalSettingsChanged;
        StopScrolling();
    }

    private void OnGlobalSettingsChanged(object? sender, EventArgs e) => ResetAndRecalc();

    private bool IsScrollEnabled => IsLyricsPage
        ? (IsForArtist ? GlobalLyricsArtistScrollEnabled : GlobalLyricsTitleScrollEnabled)
        : IsMiniPlayer
        ? (IsForAlbum ? GlobalMiniPlayerAlbumScrollEnabled : GlobalMiniPlayerTitleScrollEnabled)
        : IsCoverFlow
            ? (IsForAlbum ? GlobalCoverFlowAlbumScrollEnabled
                : IsForArtist ? GlobalCoverFlowArtistScrollEnabled
                : GlobalCoverFlowScrollEnabled)
            : true;

    private void ResetAndRecalc()
    {
        StopScrolling();
        _offset = 0;
        _transform.X = 0;

        if (VisualRoot != null)
            Dispatcher.UIThread.Post(RecalcAndStart, DispatcherPriority.Render);
    }

    private void RecalcAndStart()
    {
        if (VisualRoot == null) return;

        var viewportWidth = _viewport.Bounds.Width;
        if (viewportWidth <= 0)
            viewportWidth = MaxDisplayWidth;

        var textWidth = MeasureTextWidth();
        if (textWidth <= 0) return;

        _textWidth = textWidth;
        _viewportWidth = viewportWidth;
        _overflow = Math.Max(0, textWidth - viewportWidth);

        if (_overflow <= OverflowThreshold || !IsScrollEnabled)
        {
            // Static: apply trimming when overflow but scroll disabled
            _textBlock.TextTrimming = _overflow > OverflowThreshold
                ? TextTrimming.CharacterEllipsis
                : TextTrimming.None;
            var staticWidth = viewportWidth;
            if (InlineContent is { IsVisible: true, Bounds.Width: > 0 } ic2)
                staticWidth = Math.Max(0, staticWidth - _contentPanel.Spacing - ic2.Bounds.Width);
            _textBlock.Width = _overflow > OverflowThreshold ? staticWidth : double.NaN;
            return;
        }

        // Scrolling mode: no trimming, natural width
        _textBlock.TextTrimming = TextTrimming.None;
        _textBlock.Width = double.NaN;

        _offset = 0;
        _transform.X = 0;
        ScheduleNextLap();
    }

    /// <summary>Rest at the start position, then start the next lap. A one-shot timer
    /// rather than idling on the frame clock: re-requesting animation frames through a
    /// 7-second hold would force continuous renders doing nothing.</summary>
    private void ScheduleNextLap()
    {
        var generation = ++_lapGeneration;
        DispatcherTimer.RunOnce(() =>
        {
            if (generation == _lapGeneration && VisualRoot != null)
                StartScrolling();
        }, RestPause);
    }

    private void StartScrolling()
    {
        if (_isRunning || VisualRoot == null) return;
        _isRunning = true;
        _lastFrameTimestamp = Stopwatch.GetTimestamp();
        QueueNextFrame();
    }

    private void StopScrolling()
    {
        _isRunning = false;
        _lapGeneration++; // cancels any pending between-laps resume
    }

    private void QueueNextFrame()
    {
        if (!_isRunning || _isFrameQueued) return;
        if (TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            StopScrolling();
            return;
        }
        _isFrameQueued = true;
        topLevel.RequestAnimationFrame(OnFrame);
    }

    private void OnFrame(TimeSpan frameTime)
    {
        _isFrameQueued = false;
        if (!_isRunning) return;

        if (!IsScrollEnabled || _overflow <= OverflowThreshold || VisualRoot == null)
        {
            StopScrolling();
            ResetAndRecalc();
            return;
        }

        var now = Stopwatch.GetTimestamp();
        // Real elapsed time, clamped so a stalled UI thread can't produce one giant jump.
        var elapsedSeconds = Math.Min((now - _lastFrameTimestamp) / (double)Stopwatch.Frequency, 0.1);
        _lastFrameTimestamp = now;
        if (elapsedSeconds <= 0)
        {
            QueueNextFrame();
            return;
        }

        // Full-loop marquee, no bounce: the text always travels left. Once its tail has
        // cleared the viewport's left edge, wrap to just past the right edge so the head
        // slides back in; landing on the start position ends the lap and rests there.
        var next = _offset - ScrollSpeed * elapsedSeconds;

        if (next <= -_textWidth)
        {
            next += _textWidth + _viewportWidth;
        }
        else if (_offset > 0 && next <= 0)
        {
            // Only a wrapped (incoming-from-the-right) pass can cross zero downward —
            // the outbound pass STARTS at zero, so this never fires on the way out.
            _offset = 0;
            _transform.X = 0;
            StopScrolling();
            ScheduleNextLap();
            return;
        }

        _offset = next;
        _transform.X = next;
        QueueNextFrame();
    }

    private double MeasureTextWidth()
    {
        var text = _textBlock.Text;
        if (string.IsNullOrWhiteSpace(text)) return 0;

        var formatted = new FormattedText(
            text,
            CultureInfo.CurrentCulture,
            _textBlock.FlowDirection,
            new Typeface(
                _textBlock.FontFamily,
                _textBlock.FontStyle,
                _textBlock.FontWeight,
                _textBlock.FontStretch),
            _textBlock.FontSize,
            Brushes.Transparent);

        var width = formatted.WidthIncludingTrailingWhitespace;

        // Include inline content width + spacing when present and visible
        if (InlineContent is { IsVisible: true, Bounds.Width: > 0 } ic)
            width += _contentPanel.Spacing + ic.Bounds.Width;

        return width;
    }
}
