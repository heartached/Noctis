using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Noctis.Models;

namespace Noctis.Controls.SpicyLyrics;

/// <summary>
/// Native Avalonia control implementing the SpicyLyrics animation and rendering system.
/// Uses upstream spring physics, natural cubic splines, line-wrapping, and soft sweep.
/// Consumes Noctis's existing LyricLine / WordTiming state without creating a second clock.
/// </summary>
public sealed class SpicyLyricsRenderControl : Control
{
    public static readonly StyledProperty<LyricLine?> LineProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, LyricLine?>(nameof(Line));

    public static readonly StyledProperty<IReadOnlyList<LyricLine>?> LinesProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, IReadOnlyList<LyricLine>?>(nameof(Lines));

    public static readonly StyledProperty<SpicyLyricsPreset> PresetProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, SpicyLyricsPreset>(
            nameof(Preset), SpicyLyricsPreset.Full);

    public static readonly StyledProperty<string> PresetModeProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, string>(
            nameof(PresetMode), "SpicyLyrics Full");

    public static readonly StyledProperty<double> LyricsFontSizeProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, double>(nameof(LyricsFontSize), 34);

    public static readonly StyledProperty<FontFamily> LyricsFontFamilyProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, FontFamily>(
            nameof(LyricsFontFamily), FontFamily.Default);

    public static readonly StyledProperty<IBrush> SungBrushProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, IBrush>(
            nameof(SungBrush), Brushes.White);

    public static readonly StyledProperty<IBrush> UnsungBrushProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, IBrush>(
            nameof(UnsungBrush), new SolidColorBrush(Color.FromArgb(115, 230, 230, 240)));

    public static readonly StyledProperty<IBrush> SecondaryBrushProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, IBrush>(
            nameof(SecondaryBrush), new SolidColorBrush(Color.FromArgb(175, 210, 210, 220)));

    public static readonly StyledProperty<IBrush> GlowBrushProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, IBrush>(
            nameof(GlowBrush), new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)));

    public static readonly StyledProperty<double> RowSpacingProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, double>(nameof(RowSpacing), 10);

    public static readonly StyledProperty<double> LayerSpacingProperty =
        AvaloniaProperty.Register<SpicyLyricsRenderControl, double>(nameof(LayerSpacing), 5);

    public LyricLine? Line
    {
        get => GetValue(LineProperty);
        set => SetValue(LineProperty, value);
    }

    public IReadOnlyList<LyricLine>? Lines
    {
        get => GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public SpicyLyricsPreset Preset
    {
        get => GetValue(PresetProperty);
        set => SetValue(PresetProperty, value);
    }

    public string PresetMode
    {
        get => GetValue(PresetModeProperty);
        set => SetValue(PresetModeProperty, value);
    }

    public double LyricsFontSize
    {
        get => GetValue(LyricsFontSizeProperty);
        set => SetValue(LyricsFontSizeProperty, value);
    }

    public FontFamily LyricsFontFamily
    {
        get => GetValue(LyricsFontFamilyProperty);
        set => SetValue(LyricsFontFamilyProperty, value);
    }

    public IBrush SungBrush
    {
        get => GetValue(SungBrushProperty);
        set => SetValue(SungBrushProperty, value);
    }

    public IBrush UnsungBrush
    {
        get => GetValue(UnsungBrushProperty);
        set => SetValue(UnsungBrushProperty, value);
    }

    public IBrush SecondaryBrush
    {
        get => GetValue(SecondaryBrushProperty);
        set => SetValue(SecondaryBrushProperty, value);
    }

    public IBrush GlowBrush
    {
        get => GetValue(GlowBrushProperty);
        set => SetValue(GlowBrushProperty, value);
    }

    public double RowSpacing
    {
        get => GetValue(RowSpacingProperty);
        set => SetValue(RowSpacingProperty, value);
    }

    public double LayerSpacing
    {
        get => GetValue(LayerSpacingProperty);
        set => SetValue(LayerSpacingProperty, value);
    }

    private readonly SpicyLyricsLayoutEngine _layoutEngine = new();
    private readonly HashSet<INotifyPropertyChanged> _observed = new();
    private SpicyLyricsAnimator _animator;
    private long _lastFrameTicks;
    private bool _frameRequested;

    static SpicyLyricsRenderControl()
    {
        ClipToBoundsProperty.OverrideDefaultValue<SpicyLyricsRenderControl>(false);
        AffectsMeasure<SpicyLyricsRenderControl>(
            LineProperty,
            LinesProperty,
            LyricsFontSizeProperty,
            LyricsFontFamilyProperty,
            RowSpacingProperty,
            LayerSpacingProperty,
            MaxWidthProperty,
            WidthProperty);

        AffectsRender<SpicyLyricsRenderControl>(
            PresetProperty,
            PresetModeProperty,
            LyricsFontSizeProperty,
            LyricsFontFamilyProperty,
            SungBrushProperty,
            UnsungBrushProperty,
            SecondaryBrushProperty,
            GlowBrushProperty,
            RowSpacingProperty,
            LayerSpacingProperty);
    }

    public SpicyLyricsRenderControl()
    {
        _animator = new SpicyLyricsAnimator(Preset);
        _lastFrameTicks = Stopwatch.GetTimestamp();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);

        if (change.Property == PresetModeProperty)
        {
            Preset = SpicyLyricsPreset.FromMode(PresetMode);
            InvalidateMeasure();
            InvalidateVisual();
        }

        if (change.Property == PresetProperty)
        {
            _animator = new SpicyLyricsAnimator(Preset);
            InvalidateMeasure();
            InvalidateVisual();
        }

        if (change.Property == LineProperty)
        {
            Lines = Line is null ? null : new[] { Line };
            InvalidateMeasure();
            InvalidateVisual();
        }

        if (change.Property == LinesProperty)
        {
            RebuildSubscriptions();
            _animator.Reset();
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = availableSize.Width;
        if (!double.IsNaN(MaxWidth) && !double.IsInfinity(MaxWidth) && MaxWidth > 0)
        {
            width = double.IsInfinity(width) || width <= 0 ? MaxWidth : Math.Min(width, MaxWidth);
        }
        else if (double.IsInfinity(width) || width <= 0)
        {
            width = Bounds.Width > 0 ? Bounds.Width : 480;
        }

        if (Bounds.Width > 0 && Bounds.Width < width)
        {
            width = Bounds.Width;
        }

        var lines = Lines;
        if (lines is null || lines.Count == 0)
            return new Size(width, 0);

        var typeface = new Typeface(LyricsFontFamily, FontStyle.Normal, FontWeight.Bold);
        var mainSize = Math.Max(8, LyricsFontSize);
        var totalHeight = 0.0;
        var maxRowWidth = 0.0;

        foreach (var line in lines)
        {
            if (line.Words is { Count: > 0 } words)
            {
                using var rows = new DisposingRowList(_layoutEngine.BuildRows(words, typeface, mainSize, SungBrush, width));
                for (var i = 0; i < rows.List.Count; i++)
                {
                    if (i > 0) totalHeight += LayerSpacing;
                    totalHeight += rows.List[i].Height;
                    maxRowWidth = Math.Max(maxRowWidth, rows.List[i].Width);
                }
            }
            else if (!string.IsNullOrWhiteSpace(line.Text))
            {
                using var lineLayout = new TextLayout(
                    line.Text,
                    typeface,
                    mainSize,
                    null,
                    textAlignment: TextAlignment.Left,
                    textWrapping: TextWrapping.Wrap,
                    maxWidth: Math.Max(100.0, width));
                totalHeight += lineLayout.Height;
                maxRowWidth = Math.Max(maxRowWidth, lineLayout.WidthIncludingTrailingWhitespace);
            }
            else
            {
                totalHeight += mainSize * 1.3;
                maxRowWidth = Math.Max(maxRowWidth, mainSize * 2.0);
            }

            if (line.ShowBackgroundWords && line.BackgroundWords is { Count: > 0 } bg)
            {
                using var bgRows = new DisposingRowList(_layoutEngine.BuildRows(bg, typeface, mainSize * 0.62, SungBrush, width));
                for (var i = 0; i < bgRows.List.Count; i++)
                {
                    totalHeight += LayerSpacing + bgRows.List[i].Height;
                    maxRowWidth = Math.Max(maxRowWidth, bgRows.List[i].Width);
                }
            }
            if (line.ShowTransliterationWords && line.TransliterationWords is { Count: > 0 } roman)
            {
                using var romanRows = new DisposingRowList(_layoutEngine.BuildRows(roman, typeface, mainSize * 0.68, SungBrush, width));
                for (var i = 0; i < romanRows.List.Count; i++)
                {
                    totalHeight += LayerSpacing + romanRows.List[i].Height;
                    maxRowWidth = Math.Max(maxRowWidth, romanRows.List[i].Width);
                }
            }
            else if (line.ShowTransliterationText && !string.IsNullOrWhiteSpace(line.Transliteration))
            {
                using var romanLayout = new TextLayout(
                    line.Transliteration!,
                    typeface,
                    mainSize * 0.68,
                    null,
                    textAlignment: TextAlignment.Left,
                    textWrapping: TextWrapping.Wrap,
                    maxWidth: Math.Max(100.0, width));
                totalHeight += LayerSpacing + romanLayout.Height;
                maxRowWidth = Math.Max(maxRowWidth, romanLayout.WidthIncludingTrailingWhitespace);
            }
            if (line.ShowTranslation && !string.IsNullOrWhiteSpace(line.Translation))
            {
                using var transLayout = new TextLayout(
                    line.Translation!,
                    typeface,
                    mainSize * 0.62,
                    null,
                    textAlignment: TextAlignment.Left,
                    textWrapping: TextWrapping.Wrap,
                    maxWidth: Math.Max(100.0, width));
                totalHeight += LayerSpacing + transLayout.Height;
                maxRowWidth = Math.Max(maxRowWidth, transLayout.WidthIncludingTrailingWhitespace);
            }

            totalHeight += RowSpacing;
        }

        // Generous headroom (+20% of font size) to give full breathing room for letter blossom and swell
        return new Size(Math.Min(width, Math.Max(10.0, maxRowWidth)), Math.Max(mainSize, totalHeight + mainSize * 0.20));
    }

    private sealed class DisposingRowList : IDisposable
    {
        public List<SpicyLyricsLayoutEngine.Row> List { get; }
        public DisposingRowList(List<SpicyLyricsLayoutEngine.Row> list) => List = list;
        public void Dispose()
        {
            foreach (var r in List) r.Dispose();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LyricsRenderer.Changed += OnGlobalRendererChanged;
        Preset = SpicyLyricsPreset.FromMode(LyricsRenderer.Mode);
        RebuildSubscriptions();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LyricsRenderer.Changed -= OnGlobalRendererChanged;
        ClearSubscriptions();
    }

    private void OnGlobalRendererChanged(object? sender, EventArgs e)
    {
        Preset = SpicyLyricsPreset.FromMode(LyricsRenderer.Mode);
        InvalidateVisual();
    }

    private void RebuildSubscriptions()
    {
        ClearSubscriptions();
        if (Lines is null) return;

        foreach (var line in Lines)
        {
            Observe(line);
            ObserveWords(line.Words);
            ObserveWords(line.BackgroundWords);
            ObserveWords(line.TransliterationWords);
        }
    }

    private void ObserveWords(IReadOnlyList<WordTiming>? words)
    {
        if (words is null) return;
        foreach (var word in words)
            Observe(word);
    }

    private void Observe(INotifyPropertyChanged? source)
    {
        if (source is null || !_observed.Add(source)) return;
        source.PropertyChanged += OnModelPropertyChanged;
    }

    private void ClearSubscriptions()
    {
        foreach (var item in _observed)
            item.PropertyChanged -= OnModelPropertyChanged;
        _observed.Clear();
    }

    private void OnModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LyricLine.Words) or nameof(LyricLine.BackgroundWords) or nameof(LyricLine.TransliterationWords))
        {
            RebuildSubscriptions();
            InvalidateMeasure();
        }
        else if (e.PropertyName is nameof(LyricLine.IsActive)
                 or nameof(LyricLine.ShowWords)
                 or nameof(LyricLine.ShowLineText)
                 or nameof(LyricLine.ShowBackgroundWords)
                 or nameof(LyricLine.ShowTransliterationWords)
                 or nameof(LyricLine.ShowTransliterationText)
                 or nameof(LyricLine.ShowTranslation)
                 or nameof(LyricLine.Text)
                 or nameof(LyricLine.Transliteration)
                 or nameof(LyricLine.Translation))
        {
            InvalidateMeasure();
        }
        InvalidateVisual();
    }

    private Action<TimeSpan>? _frameCallback;

    private void RequestFrame()
    {
        if (_frameRequested) return;
        _frameRequested = true;

        _frameCallback ??= _ =>
        {
            _frameRequested = false;
            InvalidateVisual();
        };

        var top = TopLevel.GetTopLevel(this);
        if (top != null)
        {
            top.RequestAnimationFrame(_frameCallback);
        }
        else
        {
            Dispatcher.UIThread.Post(() =>
            {
                _frameRequested = false;
                InvalidateVisual();
            }, DispatcherPriority.Render);
        }
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);

        var lines = Lines;
        if (lines is null || lines.Count == 0)
            return;

        var width = Bounds.Width;
        if (width <= 0)
        {
            width = !double.IsNaN(MaxWidth) && !double.IsInfinity(MaxWidth) && MaxWidth > 0
                ? MaxWidth
                : 460;
        }

        // Calculate delta time
        var now = Stopwatch.GetTimestamp();
        var dt = (double)(now - _lastFrameTicks) / Stopwatch.Frequency;
        _lastFrameTicks = now;
        dt = Math.Clamp(dt, 0.001, 0.05);

        var typeface = new Typeface(LyricsFontFamily, FontStyle.Normal, FontWeight.Bold);
        var mainSize = Math.Max(8, LyricsFontSize);
        var top = 0.0;
        var hasActiveSprings = false;

        foreach (var line in lines)
        {
            var lineOpacity = GetLineOpacity(line);
            if (lineOpacity <= 0.005)
                continue;

            using (context.PushOpacity(lineOpacity))
            {
                var lineScale = GetLineScale(line);
                using (context.PushTransform(Matrix.CreateScale(lineScale, lineScale)))
                {
                    var drawnHeight = DrawLine(context, line, typeface, mainSize, width / lineScale, top, dt, ref hasActiveSprings);
                    top += drawnHeight + RowSpacing;
                }
            }

            if (top > Bounds.Height + mainSize * 4)
                break;
        }

        // Keep hardware-vsync render cadence if springs are still in motion
        if (hasActiveSprings)
        {
            RequestFrame();
        }
    }

    private double GetLineOpacity(LyricLine line)
    {
        if (Preset.IsMinimal)
        {
            if (line.IsActive) return 1.0;
            return line.CurrentWordIndex >= 0 ? Preset.SungOpacity : Preset.NotSungOpacity;
        }
        return Math.Clamp(line.LineOpacity, 0.0, 1.0);
    }

    private double GetLineScale(LyricLine line)
    {
        if (Preset.IsMinimal)
        {
            if (line.IsActive) return 1.0;
            return line.CurrentWordIndex >= 0 ? 0.95 : 0.965;
        }
        return 1.0;
    }

    private double DrawLine(
        DrawingContext context,
        LyricLine line,
        Typeface typeface,
        double fontSize,
        double width,
        double top,
        double dt,
        ref bool hasActiveSprings)
    {
        var y = top;
        var rightAligned = line.IsRightToLeft || line.Voice == LyricVoice.Voice2;
        var centered = line.Voice == LyricVoice.Group;

        // Intro/interlude placeholder dots
        if (line.IsIntroPlaceholder)
        {
            y += DrawInterludeDots(context, line, typeface, fontSize, width, y, centered, rightAligned, ref hasActiveSprings);
            return y - top;
        }

        // Primary lyric words
        if (line.ShowWords && line.Words is { Count: > 0 } words)
        {
            y += DrawWordLayer(context, words, typeface, fontSize, width, y,
                line.IsActive, line.IsSweepFocused, line.IsRightToLeft, rightAligned, centered, dt, ref hasActiveSprings);
        }
        else if (line.ShowLineText && !string.IsNullOrWhiteSpace(line.Text))
        {
            y += DrawStaticText(context, line.Text, typeface, fontSize,
                line.IsActive ? SungBrush : UnsungBrush, width, y, centered, rightAligned);
        }

        // Secondary layer: background vocals
        if (line.ShowBackgroundWords && line.BackgroundWords is { Count: > 0 } bg)
        {
            y += LayerSpacing;
            var bgTypeface = new Typeface(LyricsFontFamily, FontStyle.Normal, FontWeight.SemiBold);
            y += DrawWordLayer(context, bg, bgTypeface, fontSize * 0.62, width, y,
                line.IsActive, line.IsSweepFocused, line.IsRightToLeft, rightAligned, centered, dt, ref hasActiveSprings);
        }

        // Secondary layer: romanization
        if (line.ShowTransliterationWords && line.TransliterationWords is { Count: > 0 } roman)
        {
            y += LayerSpacing;
            y += DrawWordLayer(context, roman, typeface, fontSize * 0.68, width, y,
                line.IsActive, line.IsSweepFocused, line.IsRightToLeft, rightAligned, centered, dt, ref hasActiveSprings);
        }
        else if (line.ShowTransliterationText && !string.IsNullOrWhiteSpace(line.Transliteration))
        {
            y += LayerSpacing;
            y += DrawStaticText(context, line.Transliteration!, typeface, fontSize * 0.68,
                SecondaryBrush, width, y, centered, rightAligned);
        }

        // Secondary layer: translation
        if (line.ShowTranslation && !string.IsNullOrWhiteSpace(line.Translation))
        {
            y += LayerSpacing;
            y += DrawStaticText(context, line.Translation!, typeface, fontSize * 0.62,
                SecondaryBrush, width, y, centered, rightAligned);
        }

        return Math.Max(fontSize, y - top);
    }

    private double DrawWordLayer(
        DrawingContext context,
        IReadOnlyList<WordTiming> words,
        Typeface typeface,
        double fontSize,
        double width,
        double top,
        bool isLineActive,
        bool sweepFocused,
        bool rtl,
        bool rightAligned,
        bool centered,
        double dt,
        ref bool hasActiveSprings)
    {
        var rows = _layoutEngine.BuildRows(words, typeface, fontSize, SungBrush, width);
        var y = top;

        try
        {
            var wordIndex = 0;
            foreach (var row in rows)
            {
                var x = centered
                    ? Math.Max(0, (width - row.Width) / 2)
                    : rightAligned
                        ? Math.Max(0, width - row.Width)
                        : 0;

                foreach (var item in row.Words)
                {
                    var word = item.Timing;
                    var wordWidth = item.Width;
                    var wordHeight = item.Height;

                    // Evaluate word pose via animator
                    var evaluated = _animator.AnimateWord(word, wordIndex, isLineActive, dt);
                    if (isLineActive || Math.Abs(evaluated.Scale - 1.0) > 0.003 || Math.Abs(evaluated.OffsetYEm) > 0.001 || evaluated.Glow > 0.01)
                    {
                        hasActiveSprings = true;
                    }

                    if (evaluated.Letters is { Count: > 1 } letters)
                    {
                        // Held word letter animation: pops letter-by-letter (f o r e v e r), spreads and glows!
                        DrawHeldWordLetters(context, word, letters, typeface, fontSize, x, y, wordWidth, isLineActive, sweepFocused, rtl, ref hasActiveSprings);
                    }
                    else
                    {
                        // Standard word animation: pops, lifts, sweeps, and glows!
                        DrawStandardWord(context, word, evaluated, typeface, fontSize, x, y, wordWidth, wordHeight, isLineActive, sweepFocused, rtl);
                    }

                    x += wordWidth;
                    wordIndex++;
                }

                y += row.Height + LayerSpacing;
            }
        }
        finally
        {
            foreach (var r in rows)
                r.Dispose();
        }

        return Math.Max(fontSize, y - top - LayerSpacing);
    }

    // ── Gradient wipe (faithful port of appleWipe + drawWipeText from LyricsRenderer.kt) ──────────

    /// <summary>
    /// Computes the (lo, hi) stop fractions in the fragment's LOCAL [0..1] space for the
    /// Apple Music feathered wipe brush. Mirrors LyricsRenderer.kt appleWipe + drawWipeText exactly.
    /// </summary>
    /// <param name="gradientPositionPercent">Raw gradient position (-20 = unsung, 100 = sung).</param>
    /// <param name="fullWordWidth">Total width of the whole word (reference space for percent).</param>
    /// <param name="fragmentWidth">Width of this specific fragment (letter or word on its row).</param>
    /// <param name="startXOffset">This fragment's x offset within fullWordWidth.</param>
    /// <param name="fontSize">Font size in pixels (used to compute feather in pixels).</param>
    /// <param name="text">The text of the full word (used for feather calculation).</param>
    /// <param name="rtl">Right-to-left: gradient grows from the right.</param>
    private static (double lo, double hi) AppleWipeStops(
        double gradientPositionPercent,
        double fullWordWidth,
        double fragmentWidth,
        double startXOffset,
        double fontSize,
        string text,
        bool rtl)
    {
        var fw = Math.Max(1.0, fullWordWidth);
        var w = Math.Max(1.0, fragmentWidth);
        var isCjk = SpicyLyricsAppleMusicMotion.IsCjk(text);
        var charCount = new StringInfo(text.Trim()).LengthInTextElements;
        var feather = Math.Min(SpicyLyricsAppleMusicMotion.FeatherEm(charCount, isCjk) * fontSize, fw / 2.0);

        // appleWipe: edge = progress/100 * (width+feather) - feather; position = edge/width*100; band = feather/width*100-20
        var edge = gradientPositionPercent / 100.0 * (fw + feather) - feather;
        var stop1 = edge / fw;                              // loFrac (global)
        var stop2 = (edge + feather) / fw;                  // hiFrac (global)  [band = feather/fw, but +20% correction ≈ +feather]

        // Actually: position = edge/width*100, band = feather/width*100 - 20
        // stop2 (in global 0..1) = (position + 20 + band) / 100 = (edge/fw*100 + feather/fw*100) / 100 = (edge+feather)/fw
        // This matches the Kotlin: stop2Frac = (gradientPositionPercent + 20f + gradientOffsetPercent) / 100f
        // where gradientOffset=0 in Full mode, and 20f corresponds to the fixed 20% wipe band.

        if (rtl)
        {
            var tmpLo = 1.0 - stop2;
            var tmpHi = 1.0 - stop1;
            stop1 = tmpLo;
            stop2 = tmpHi;
        }

        // Map global stops into fragment-local [0..1] space
        var localLo = (stop1 * fw - startXOffset) / w;
        var localHi = (stop2 * fw - startXOffset) / w;

        return (localLo, localHi);
    }

    /// <summary>
    /// Builds a LinearGradientBrush in text-local coordinates (0..fragmentWidth) that
    /// faithfully reproduces the Apple Music wipe: dim on the left, bright on the right,
    /// with a soft feathered transition band.
    /// </summary>
    private static IBrush BuildWipeBrush(
        double lo, double hi,
        double fragmentWidth,
        Color brightColor, Color dimColor,
        bool rtl)
    {
        var w = Math.Max(1.0, fragmentWidth);

        // Clamp to valid stop range (same logic as Kotlin's coerceIn)
        var s0 = Math.Clamp(lo, 0.0001, 0.9997);
        var s1 = Math.Clamp(hi, s0 + 0.0001, 0.9998);

        // colorAt(f): lerp between dim and bright across the band
        Color ColorAt(double f)
        {
            var t = Math.Clamp((f - lo) / Math.Max(1e-6, hi - lo), 0.0, 1.0);
            if (!rtl) t = 1.0 - t; // bright on left side of band for LTR
            var a = (byte)(dimColor.A + t * (brightColor.A - dimColor.A));
            var r = (byte)(dimColor.R + t * (brightColor.R - dimColor.R));
            var g = (byte)(dimColor.G + t * (brightColor.G - dimColor.G));
            var b = (byte)(dimColor.B + t * (brightColor.B - dimColor.B));
            return Color.FromArgb(a, r, g, b);
        }

        // For LTR: left (0) = leftColor (dim before band, bright after)
        //          right (1) = rightColor
        // The reference uses: left=bright, right=dim for LTR (bright grows from left)
        var leftColor = rtl ? dimColor : brightColor;
        var rightColor = rtl ? brightColor : dimColor;

        var brush = new LinearGradientBrush
        {
            // Text-local absolute coordinates: 0 to fragmentWidth pixels
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(w, 0, RelativeUnit.Absolute),
            SpreadMethod = GradientSpreadMethod.Pad,
        };

        brush.GradientStops.Add(new GradientStop(ColorAt(0.0), 0));
        brush.GradientStops.Add(new GradientStop(ColorAt(s0), s0));
        brush.GradientStops.Add(new GradientStop(ColorAt(s1), s1));
        brush.GradientStops.Add(new GradientStop(ColorAt(1.0), 1));

        return brush;
    }

    /// <summary>
    /// Extracts the solid color from a brush (SolidColorBrush or fallback).
    /// </summary>
    private static Color GetBrushColor(IBrush brush, byte alpha = 255)
    {
        if (brush is SolidColorBrush scb)
            return Color.FromArgb(alpha == 255 ? scb.Color.A : alpha, scb.Color.R, scb.Color.G, scb.Color.B);
        return Color.FromArgb(alpha, 255, 255, 255);
    }

    // ── Held word: f o r e v e r letter-by-letter blossom ──────────────────────────────────────────

    private void DrawHeldWordLetters(
        DrawingContext context,
        WordTiming word,
        IReadOnlyList<SpicyLyricsEvaluatedLetter> letters,
        Typeface typeface,
        double fontSize,
        double wordX,
        double wordY,
        double wordWidth,
        bool isLineActive,
        bool sweepFocused,
        bool rtl,
        ref bool hasActiveSprings)
    {
        var text = word.SweepText;
        if (string.IsNullOrEmpty(text))
            return;

        // Separate leading whitespace (preserved for x-advance) from actual letters
        var lead = text.Length - text.TrimStart().Length;
        var lettersText = text[lead..];
        var starts = StringInfo.ParseCombiningCharacters(lettersText);
        if (starts.Length != letters.Count)
            return;

        // Measure the full word to get absolute x-positions for each letter
        using var fullWordLayout = new TextLayout(text, typeface, fontSize, Brushes.White);
        var fullWordPixelWidth = Math.Max(1.0, fullWordLayout.WidthIncludingTrailingWhitespace);
        var rowHeight = fullWordLayout.Height;

        // Draw leading whitespace (always unsung)
        if (lead > 0)
        {
            var leadText = text[..lead];
            using var leadLayout = new TextLayout(leadText, typeface, fontSize, UnsungBrush);
            leadLayout.Draw(context, new Point(wordX, wordY));
        }

        var brightColor = GetBrushColor(SungBrush);
        var dimColor = GetBrushColor(UnsungBrush);

        for (int i = 0; i < letters.Count; i++)
        {
            var letter = letters[i];
            if (Math.Abs(letter.Scale - 1.0) > 0.003 || Math.Abs(letter.OffsetXEm) > 0.001 || Math.Abs(letter.OffsetYEm) > 0.001 || letter.Glow > 0.01)
            {
                hasActiveSprings = true;
            }

            // Compute this letter's x0..x1 positions within the full word layout
            var from = lead + starts[i];
            var to = lead + (i + 1 < starts.Length ? starts[i + 1] : lettersText.Length);
            var x0 = fullWordLayout.HitTestTextPosition(from).X;
            var x1 = to < text.Length ? fullWordLayout.HitTestTextPosition(to).X : fullWordPixelWidth;
            var letterWidth = Math.Max(1.0, x1 - x0);
            var letterText = text[from..to];

            // Letter canvas position
            var lx = wordX + x0;
            var ly = wordY;
            var cx = lx + letterWidth / 2.0;
            var cy = ly + rowHeight / 2.0;

            // Spread offset: left letters move left, right letters move right (f  o  r  e  v  e  r)
            var dx = letter.OffsetXEm * fontSize;
            var dy = letter.OffsetYEm * fontSize;
            var s = letter.Scale;

            // Reference transform: translate(xOffset, yOffset) then scale(s, s, pivot=letterCenter)
            // Equivalent matrix: T(dx,dy) * T(-cx,-cy) * S(s,s) * T(cx,cy)
            // But we apply to the drawing context: first translate origin to letter center,
            // scale, then translate to final position including spread.
            var letterTransform =
                Matrix.CreateTranslation(-(cx), -(cy)) *
                Matrix.CreateScale(s, s) *
                Matrix.CreateTranslation(cx + dx, cy + dy);

            using (context.PushTransform(letterTransform))
            {
                // 1. Glow halo (only when actively animating and glow exposure > 0)
                if (letter.Glow > 0.01 && Preset.EnableGlow && LyricsRenderer.GlowExposure > 0.01)
                {
                    DrawGlowHalo(context, letterText, typeface, fontSize, new Point(lx, ly), letter.Glow);
                }

                // 2. Draw this letter with the gradient wipe brush
                // The gradient position for each letter is that letter's own gradientPosition
                // but the reference width is the FULL WORD width (not just this letter).
                // startXOffset = x0 (this letter's position within the full word).
                var (lo, hi) = AppleWipeStops(
                    gradientPositionPercent: letter.GradientPosition,
                    fullWordWidth: fullWordPixelWidth,
                    fragmentWidth: letterWidth,
                    startXOffset: x0,
                    fontSize: fontSize,
                    text: lettersText,  // full word text for feather calc
                    rtl: rtl);

                var wipeBrush = BuildWipeBrush(lo, hi, letterWidth, brightColor, dimColor, rtl);

                using var letterLayout = new TextLayout(letterText, typeface, fontSize, wipeBrush);
                letterLayout.Draw(context, new Point(lx, ly));
            }
        }
    }

    // ── Standard word: pop, lift, sweep, glow ──────────────────────────────────────────────────────

    private void DrawStandardWord(
        DrawingContext context,
        WordTiming word,
        SpicyLyricsEvaluatedWord evaluated,
        Typeface typeface,
        double fontSize,
        double x,
        double y,
        double wordWidth,
        double wordHeight,
        bool isLineActive,
        bool sweepFocused,
        bool rtl)
    {
        var cx = x + wordWidth / 2.0;
        var cy = y + wordHeight / 2.0;
        var dy = evaluated.OffsetYEm * fontSize;
        var s = evaluated.Scale;

        // Word transform: scale around center, then lift vertically
        var wordTransform = Matrix.CreateTranslation(-cx, -cy) *
                            Matrix.CreateScale(s, s) *
                            Matrix.CreateTranslation(cx, cy + dy);

        using (context.PushTransform(wordTransform))
        {
            // Glow for emphasis words (> 7 letters, like "spectacular?")
            if (word.IsEmphasis && evaluated.Glow > 0.02 && Preset.EnableGlow && LyricsRenderer.GlowExposure > 0.01)
            {
                DrawGlowHalo(context, word.DisplayText, typeface, fontSize, new Point(x, y), evaluated.Glow * 0.7);
            }

            // Gradient wipe brush: soft-edged Apple Music sweep
            // For standard words, the full word width = fragment width, startXOffset = 0
            var brightColor = GetBrushColor(SungBrush);
            var dimColor = GetBrushColor(UnsungBrush);

            double gradPos;
            if (!sweepFocused || !isLineActive)
            {
                // Not actively sweeping: use state-based solid position
                gradPos = evaluated.State == LyricElementState.Sung ? 100.0 : -20.0;
            }
            else
            {
                gradPos = evaluated.GradientPosition;
            }

            var (lo, hi) = AppleWipeStops(
                gradientPositionPercent: gradPos,
                fullWordWidth: wordWidth,
                fragmentWidth: wordWidth,
                startXOffset: 0.0,
                fontSize: fontSize,
                text: word.DisplayText,
                rtl: rtl);

            var wipeBrush = BuildWipeBrush(lo, hi, wordWidth, brightColor, dimColor, rtl);

            using var wordLayout = new TextLayout(word.DisplayText, typeface, fontSize, wipeBrush);
            wordLayout.Draw(context, new Point(x, y));
        }
    }

    private void DrawGlowHalo(
        DrawingContext context,
        string text,
        Typeface typeface,
        double fontSize,
        Point origin,
        double glowStrength)
    {
        var exposure = LyricsRenderer.GlowExposure;
        if (glowStrength <= 0.005 || !Preset.EnableGlow || exposure <= 0.01)
            return;

        // Upstream Apple Music / SpicyLyrics Mobile halo:
        // Luminous soft aura around held/singing glyphs.
        // Radius and alpha scale dynamically with glowStrength and exposure slider.
        var effective = glowStrength;
        var radius = Math.Clamp(fontSize * (0.04 + 0.04 * Math.Min(effective, 1.8)), 2.5, 12.0);
        var baseAlpha = Math.Clamp(effective * 0.38, 0.04, 0.65);

        var glowColor = GetBrushColor(GlowBrush);
        if (glowColor.A == 0)
        {
            glowColor = Colors.White;
        }

        // 1. Outer halo ring (cardinal + diagonal = 8 samples for smooth circular bloom)
        var outerAlpha = (byte)(baseAlpha * 0.22 * 255);
        if (outerAlpha > 1)
        {
            var diag = radius * 0.7071;
            var outerBrush = new SolidColorBrush(Color.FromArgb(outerAlpha, glowColor.R, glowColor.G, glowColor.B));
            using var outerLayout = new TextLayout(text, typeface, fontSize, outerBrush);
            // 4 cardinal offsets
            outerLayout.Draw(context, new Point(origin.X - radius, origin.Y));
            outerLayout.Draw(context, new Point(origin.X + radius, origin.Y));
            outerLayout.Draw(context, new Point(origin.X, origin.Y - radius));
            outerLayout.Draw(context, new Point(origin.X, origin.Y + radius));
            // 4 diagonal offsets
            outerLayout.Draw(context, new Point(origin.X - diag, origin.Y - diag));
            outerLayout.Draw(context, new Point(origin.X + diag, origin.Y - diag));
            outerLayout.Draw(context, new Point(origin.X - diag, origin.Y + diag));
            outerLayout.Draw(context, new Point(origin.X + diag, origin.Y + diag));
        }

        // 2. Inner tight ring (4 cardinal samples at half radius)
        var innerAlpha = (byte)(baseAlpha * 0.35 * 255);
        if (innerAlpha > 1)
        {
            var halfRadius = radius * 0.5;
            var innerBrush = new SolidColorBrush(Color.FromArgb(innerAlpha, glowColor.R, glowColor.G, glowColor.B));
            using var innerLayout = new TextLayout(text, typeface, fontSize, innerBrush);
            innerLayout.Draw(context, new Point(origin.X - halfRadius, origin.Y));
            innerLayout.Draw(context, new Point(origin.X + halfRadius, origin.Y));
            innerLayout.Draw(context, new Point(origin.X, origin.Y - halfRadius));
            innerLayout.Draw(context, new Point(origin.X, origin.Y + halfRadius));
        }

        // 3. Soft center core sample
        var centerAlpha = (byte)(baseAlpha * 0.55 * 255);
        if (centerAlpha > 1)
        {
            var centerBrush = new SolidColorBrush(Color.FromArgb(centerAlpha, glowColor.R, glowColor.G, glowColor.B));
            using var centerLayout = new TextLayout(text, typeface, fontSize, centerBrush);
            centerLayout.Draw(context, origin);
        }
    }

    private double DrawInterludeDots(
        DrawingContext context,
        LyricLine line,
        Typeface typeface,
        double fontSize,
        double width,
        double top,
        bool centered,
        bool rightAligned,
        ref bool hasActiveSprings)
    {
        hasActiveSprings = line.IsActive;
        var dotFontSize = fontSize * 0.75;
        var progress = double.IsFinite(line.InterludeProgress)
            ? Math.Clamp(line.InterludeProgress, 0.0, 1.0)
            : (line.IsActive ? 0.5 : 0.0);

        // AppleMusicMotion breathing dots from spicylyrics/mobile:
        // Group breathes gently, each dot lights in sequence across the instrumental break!
        var breathPhase = progress * 4.0 * Math.PI;
        var groupScale = line.IsActive ? (0.94 + 0.08 * Math.Sin(breathPhase)) : 1.0;

        const int dotCount = 3;
        var dotSpacing = dotFontSize * 1.5;
        var totalWidth = (dotCount - 1) * dotSpacing + dotFontSize;
        var startX = centered
            ? Math.Max(0, (width - totalWidth) / 2.0)
            : (rightAligned ? Math.Max(0, width - totalWidth) : 0);
        var cy = top + dotFontSize / 2.0;

        using (context.PushTransform(Matrix.CreateTranslation(-width / 2.0, -cy) *
                                    Matrix.CreateScale(groupScale, groupScale) *
                                    Matrix.CreateTranslation(width / 2.0, cy)))
        {
            for (var i = 0; i < dotCount; i++)
            {
                var dx = startX + i * dotSpacing;
                var dotSpan = 1.0 / dotCount;
                var dotP = Math.Clamp((progress - i * dotSpan) / dotSpan, 0.0, 1.0);
                var alpha = line.IsActive ? (0.25 + 0.75 * dotP) : 0.35;

                var brush = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), 255, 255, 255));
                using var dotLayout = new TextLayout("●", typeface, dotFontSize, brush);

                if (line.IsActive && dotP > 0.05 && dotP < 0.95 && Preset.EnableGlow)
                {
                    DrawGlowHalo(context, "●", typeface, dotFontSize, new Point(dx, top), dotP * 0.4);
                }

                dotLayout.Draw(context, new Point(dx, top));
            }
        }

        return Math.Max(fontSize, dotFontSize * 1.5);
    }

    private double DrawStaticText(
        DrawingContext context,
        string text,
        Typeface typeface,
        double fontSize,
        IBrush brush,
        double width,
        double top,
        bool centered,
        bool rightAligned)
    {
        using var layout = new TextLayout(
            text,
            typeface,
            fontSize,
            brush,
            textAlignment: centered ? TextAlignment.Center : (rightAligned ? TextAlignment.Right : TextAlignment.Left),
            textWrapping: TextWrapping.Wrap,
            maxWidth: Math.Max(100.0, width));
        layout.Draw(context, new Point(0, top));
        return Math.Max(fontSize, layout.Height);
    }
}
