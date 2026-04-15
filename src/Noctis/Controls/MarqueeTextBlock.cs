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
    public static bool GlobalMenuTitleScrollEnabled { get; set; } = true;
    public static bool GlobalMenuArtistScrollEnabled { get; set; } = true;
    public static bool GlobalCoverFlowScrollEnabled { get; set; } = true;
    public static bool GlobalCoverFlowArtistScrollEnabled { get; set; } = true;
    public static bool GlobalCoverFlowAlbumScrollEnabled { get; set; } = true;
    public static bool GlobalLyricsTitleScrollEnabled { get; set; } = true;
    public static bool GlobalLyricsArtistScrollEnabled { get; set; } = true;

    private const double OverflowThreshold = 1.0;
    private const double ScrollSpeed = 26.0;
    private static readonly TimeSpan EdgePause = TimeSpan.FromMilliseconds(850);

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

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = new();
    private double _overflow;
    private double _offset;
    private int _direction = -1;
    private double _pauseRemainingMs;

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

        _timer.Tick += OnTick;
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += OnDetached;
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

        // Schedule measurement after layout
        Dispatcher.UIThread.Post(RecalcAndStart, DispatcherPriority.Render);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        StopTimer();
    }

    private bool IsScrollEnabled => IsLyricsPage
        ? (IsForArtist ? GlobalLyricsArtistScrollEnabled : GlobalLyricsTitleScrollEnabled)
        : IsCoverFlow
            ? (IsForAlbum ? GlobalCoverFlowAlbumScrollEnabled
                : IsForArtist ? GlobalCoverFlowArtistScrollEnabled
                : GlobalCoverFlowScrollEnabled)
            : IsForArtist
                ? GlobalMenuArtistScrollEnabled
                : GlobalMenuTitleScrollEnabled;

    private void ResetAndRecalc()
    {
        StopTimer();
        _offset = 0;
        _direction = -1;
        _pauseRemainingMs = EdgePause.TotalMilliseconds;
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
        _direction = -1;
        _pauseRemainingMs = EdgePause.TotalMilliseconds;
        _transform.X = 0;
        StartTimer();
    }

    private void StartTimer()
    {
        if (_timer.IsEnabled || VisualRoot == null) return;
        _clock.Restart();
        _timer.Start();
    }

    private void StopTimer()
    {
        if (!_timer.IsEnabled) return;
        _timer.Stop();
        _clock.Reset();
    }

    private void OnTick(object? sender, EventArgs e)
    {
        if (!IsScrollEnabled || _overflow <= OverflowThreshold || VisualRoot == null)
        {
            StopTimer();
            ResetAndRecalc();
            return;
        }

        var elapsedMs = _clock.Elapsed.TotalMilliseconds;
        _clock.Restart();
        if (elapsedMs <= 0) return;

        if (_pauseRemainingMs > 0)
        {
            _pauseRemainingMs = Math.Max(0, _pauseRemainingMs - elapsedMs);
            return;
        }

        var next = _offset + (_direction * ScrollSpeed * elapsedMs / 1000.0);
        if (_direction < 0 && next <= -_overflow)
        {
            next = -_overflow;
            _direction = 1;
            _pauseRemainingMs = EdgePause.TotalMilliseconds;
        }
        else if (_direction > 0 && next >= 0)
        {
            next = 0;
            _direction = -1;
            _pauseRemainingMs = EdgePause.TotalMilliseconds;
        }

        _offset = next;
        _transform.X = next;
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
