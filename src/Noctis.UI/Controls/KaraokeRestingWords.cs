using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Noctis.Converters;
using Noctis.Models;

namespace Noctis.Controls;

/// <summary>
/// A word-timed lyric line at rest, as ONE control: laid out exactly like the word layer
/// (an ItemsControl of karaoke word cells in a horizontal <see cref="WrapPanel"/>) and drawn
/// as that layer draws an inactive line — every word in <see cref="Foreground"/>.
///
/// The word layer costs ~8 visuals and ~20 bindings per word. A song never notices; a
/// word-synced audiobook does (Discord, nutf!xx, 10-28): 14,238 words on the page were
/// 118,559 visuals and a 1.4 GB heap, built in a 14-17 s UI freeze that allocated 3.3 GB, and
/// the 195 gen-1 collections of that build stopped the managed audio render thread long
/// enough to cut the sound to bursts. Lines away from the sung one show this control instead
/// (<see cref="LyricLine.IsWordLayerLive"/>), so the page costs the same for a book as for a
/// song.
///
/// Swapping the two must not move anything, so each cell is measured by a real
/// <see cref="KaraokeWordText"/> (same styles, fonts, padding and layout rounding as the
/// template's) and the cells are wrapped with WrapPanel's own rules (DownOnly Viewbox
/// clamp included); the tests compare both layouts line by line.
/// </summary>
public class KaraokeRestingWords : Control
{
    public static readonly StyledProperty<IReadOnlyList<WordTiming>?> WordsProperty =
        AvaloniaProperty.Register<KaraokeRestingWords, IReadOnlyList<WordTiming>?>(nameof(Words));

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        AvaloniaProperty.Register<KaraokeRestingWords, IBrush?>(nameof(Foreground), Brushes.White);

    public static readonly StyledProperty<WrapPanelItemsAlignment> ItemsAlignmentProperty =
        AvaloniaProperty.Register<KaraokeRestingWords, WrapPanelItemsAlignment>(nameof(ItemsAlignment));

    public IReadOnlyList<WordTiming>? Words { get => GetValue(WordsProperty); set => SetValue(WordsProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Row alignment, as the word layer's WrapPanel.ItemsAlignment (duet voices).</summary>
    public WrapPanelItemsAlignment ItemsAlignment { get => GetValue(ItemsAlignmentProperty); set => SetValue(ItemsAlignmentProperty, value); }

    static KaraokeRestingWords()
    {
        AffectsMeasure<KaraokeRestingWords>(WordsProperty);
        AffectsArrange<KaraokeRestingWords>(ItemsAlignmentProperty);
        AffectsRender<KaraokeRestingWords>(ForegroundProperty);
        ClipToBoundsProperty.OverrideDefaultValue<KaraokeRestingWords>(false);
    }

    // Measures the cells; never drawn (its text is emptied after each pass).
    private readonly KaraokeWordText _measurer = new() { FontWeight = FontWeight.Bold };

    private Size[] _cellSizes = Array.Empty<Size>();   // per word, as the template's cell child measures
    private object? _measuredKey;                       // what _cellSizes were measured with
    private Rect[] _cells = Array.Empty<Rect>();       // arranged cell slots
    private double[] _scales = Array.Empty<double>();  // DownOnly Viewbox scale per cell
    private TextLayout?[] _layouts = Array.Empty<TextLayout?>();
    private IBrush? _layoutBrush;

    public KaraokeRestingWords()
    {
        _measurer.Classes.Add("word-sweep");
        LogicalChildren.Add(_measurer);
        VisualChildren.Add(_measurer);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WordsProperty)
        {
            _measuredKey = null;
            DisposeLayouts();
        }
        else if (change.Property == ForegroundProperty)
        {
            DisposeLayouts();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        DisposeLayouts();
    }

    private void DisposeLayouts()
    {
        foreach (var layout in _layouts) layout?.Dispose();
        if (_layouts.Length > 0) Array.Clear(_layouts);
    }

    private object MeasureKey() => (Words, _measurer.FontFamily, _measurer.FontSize, _measurer.FontWeight,
        _measurer.FontStyle, _measurer.FontStretch, _measurer.LetterSpacing, LayoutHelper.GetLayoutScale(this));

    private void EnsureCellSizes()
    {
        var key = MeasureKey();
        if (Equals(key, _measuredKey)) return;
        _measuredKey = key;
        DisposeLayouts();

        var words = Words;
        var count = words?.Count ?? 0;
        if (_cellSizes.Length != count)
        {
            _cellSizes = new Size[count];
            _cells = new Rect[count];
            _scales = new double[count];
            _layouts = new TextLayout?[count];
        }
        // The template's Viewbox measures its word with no constraint.
        for (int i = 0; i < count; i++)
        {
            var w = words![i];
            _measurer.Text = w.Text;
            _measurer.Padding = WordOverflowPaddingConverter.PaddingFor(w);
            _measurer.Measure(Size.Infinity);
            _cellSizes[i] = _measurer.DesiredSize;
        }
        _measurer.Text = string.Empty;
        _measurer.Padding = default;
        _measurer.Measure(Size.Infinity);
    }

    // The cell as the WrapPanel sees it: the word, scaled down (Viewbox Uniform, DownOnly) to fit
    // the available width.
    // the available width, its size rounded up to the pixel grid as the Viewbox's measure is.
    private (Size Size, double Scale) Cell(Size word, double availableWidth)
    {
        if (double.IsInfinity(availableWidth) || word.Width <= availableWidth || word.Width <= 0)
            return (word, 1);
        var scale = availableWidth / word.Width;
        var size = new Size(word.Width * scale, word.Height * scale);
        if (UseLayoutRounding)
        {
            var dpi = LayoutHelper.GetLayoutScale(this);
            size = LayoutHelper.RoundLayoutSizeUp(size, dpi);
        }
        return (size, scale);
    }

    // WrapPanel's line-break test (MathUtilities.GreaterThan is internal: same relative epsilon).
    private bool GreaterThan(double value1, double value2)
    {
        if (UseLayoutRounding)
            return value1 > value2 && value1 - value2 > LayoutHelper.LayoutEpsilon;
        var eps = (Math.Abs(value1) + Math.Abs(value2) + 10.0) * 2.2204460492503131e-016;
        return value1 > value2 && value1 - value2 >= eps;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        EnsureCellSizes();
        // WrapPanel.MeasureOverride, horizontal, no item size or spacing.
        double lineU = 0, lineV = 0, panelU = 0, panelV = 0;
        for (int i = 0; i < _cellSizes.Length; i++)
        {
            var (size, _) = Cell(_cellSizes[i], availableSize.Width);
            if (GreaterThan(lineU + size.Width, availableSize.Width))
            {
                panelU = Math.Max(lineU, panelU);
                panelV += lineV;
                lineU = size.Width;
                lineV = size.Height;
            }
            else
            {
                lineU += size.Width;
                lineV = Math.Max(size.Height, lineV);
            }
        }
        panelU = Math.Max(lineU, panelU);
        panelV += lineV;
        return new Size(panelU, panelV);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _measurer.Arrange(default);
        // WrapPanel.ArrangeOverride. The cells were measured against the measure constraint;
        // the template's Viewbox re-fits against its arranged slot, which is the same width.
        var available = finalSize.Width;
        int first = 0;
        double v = 0, lineU = 0, lineV = 0;
        for (int i = 0; i < _cellSizes.Length; i++)
        {
            var (size, _) = Cell(_cellSizes[i], available);
            if (GreaterThan(lineU + size.Width, available))
            {
                ArrangeLine(first, i, v, lineV, available);
                v += lineV;
                lineU = size.Width;
                lineV = size.Height;
                first = i;
            }
            else
            {
                lineU += size.Width;
                lineV = Math.Max(size.Height, lineV);
            }
        }
        if (first < _cellSizes.Length)
            ArrangeLine(first, _cellSizes.Length, v, lineV, available);
        return finalSize;
    }

    private void ArrangeLine(int start, int end, double v, double lineV, double available)
    {
        double u = 0;
        if (ItemsAlignment != WrapPanelItemsAlignment.Start)
        {
            double total = 0;
            for (int i = start; i < end; i++) total += Cell(_cellSizes[i], available).Size.Width;
            u = ItemsAlignment == WrapPanelItemsAlignment.Center ? (available - total) / 2 : available - total;
        }
        // Arrange puts each cell on the pixel grid (a centred row can start mid-pixel).
        var dpi = UseLayoutRounding ? LayoutHelper.GetLayoutScale(this) : 0;
        for (int i = start; i < end; i++)
        {
            var (size, scale) = Cell(_cellSizes[i], available);
            _cells[i] = dpi > 0
                ? new Rect(LayoutHelper.RoundLayoutValue(u, dpi), LayoutHelper.RoundLayoutValue(v, dpi), size.Width, lineV)
                : new Rect(u, v, size.Width, lineV);
            _scales[i] = scale;
            u += size.Width;
        }
    }

    public override void Render(DrawingContext context)
    {
        var words = Words;
        if (words == null || words.Count != _cells.Length) return;
        var brush = Foreground ?? Brushes.White;
        if (!ReferenceEquals(brush, _layoutBrush))
        {
            DisposeLayouts();
            _layoutBrush = brush;
        }
        var typeface = new Typeface(_measurer.FontFamily, _measurer.FontStyle, _measurer.FontWeight, _measurer.FontStretch);
        for (int i = 0; i < words.Count; i++)
        {
            // What KaraokeWordText draws at rest: the trimmed word at the cell's padding origin.
            var text = (words[i].Text ?? string.Empty).TrimEnd();
            if (text.Length == 0) continue;
            var layout = _layouts[i] ??= new TextLayout(text, typeface, _measurer.FontSize, brush);
            var pad = WordOverflowPaddingConverter.PaddingFor(words[i]);
            var cell = _cells[i];
            var scale = _scales[i];
            var transform = Matrix.CreateTranslation(pad.Left, pad.Top);
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (scale != 1) transform *= Matrix.CreateScale(scale, scale);
            transform *= Matrix.CreateTranslation(cell.X, cell.Y);
            using (context.PushTransform(transform))
                layout.Draw(context, default);
        }
    }
}
