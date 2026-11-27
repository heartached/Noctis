using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// A caret that sits in the middle of the gap between two letters, for a TextBox with
/// LetterSpacing (the colour picker's hex field, owner 11-26). Avalonia's own caret stands at
/// the next glyph's origin, so with tracking it hugs the left edge of the next digit, and at
/// the end of the line TextPresenter draws it 1px back inside the text, on the last digit.
/// <c>helpers:CenteredCaret.IsEnabled="True"</c> hides the native caret (CaretBrush
/// Transparent on that TextBox only) and draws one in the adorner layer over the TextBox:
/// placed from the presenter's TextLayout (HitTestTextPosition), centred between the ink of
/// the glyphs either side (at index 0 at least 1px before the first glyph, at the end at least
/// 1px after the last; half the spacing back from the next origin where a glyph has no ink), one
/// device-independent pixel wide snapped to device pixels, the line's height, the text colour
/// (or <see cref="BrushProperty"/>), blinking at TextBox.CaretBlinkInterval and restarting on
/// every caret move or key press. Shown only while the TextBox is focused with no selection.
/// Selection, typing, paste and IME are the TextBox's own, untouched.
/// </summary>
public static class CenteredCaret
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<TextBox, bool>("IsEnabled", typeof(CenteredCaret));

    /// <summary>The caret's colour; unset, the TextBox's Foreground.</summary>
    public static readonly AttachedProperty<IBrush?> BrushProperty =
        AvaloniaProperty.RegisterAttached<TextBox, IBrush?>("Brush", typeof(CenteredCaret));

    private static readonly AttachedProperty<CaretAdorner?> AdornerProperty =
        AvaloniaProperty.RegisterAttached<TextBox, CaretAdorner?>("Adorner", typeof(CenteredCaret));

    public static bool GetIsEnabled(TextBox box) => box.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(TextBox box, bool value) => box.SetValue(IsEnabledProperty, value);
    public static IBrush? GetBrush(TextBox box) => box.GetValue(BrushProperty);
    public static void SetBrush(TextBox box, IBrush? value) => box.SetValue(BrushProperty, value);

    static CenteredCaret()
    {
        IsEnabledProperty.Changed.AddClassHandler<TextBox>((box, e) =>
        {
            box.GetValue(AdornerProperty)?.Detach();
            box.ClearValue(AdornerProperty);
            if (e.NewValue is true) box.SetValue(AdornerProperty, new CaretAdorner(box));
        });
        BrushProperty.Changed.AddClassHandler<TextBox>((box, _) => box.GetValue(AdornerProperty)?.InvalidateVisual());
    }

    /// <summary>Where the caret's centre goes, in TextPresenter coordinates, for the caret
    /// <paramref name="index"/> (0..<paramref name="length"/>) of <paramref name="layout"/>:
    /// halfway between the ink of the glyphs either side (their side bearings differ, so the
    /// middle of the spacing alone sat up to a pixel off); at the end past the last glyph's
    /// ink by at least a pixel; at the start at least a pixel before the first glyph's ink.
    /// Without glyph metrics (a space), half the spacing back from the next glyph's origin.</summary>
    internal static double CaretCentreX(TextLayout layout, int index, int length, double letterSpacing, double caretWidth)
    {
        var origin = layout.HitTestTextPosition(index).X;
        var centre = origin - Math.Max(letterSpacing, 0) / 2;
        var before = index > 0 ? InkSpan(layout, index - 1) : null;
        var after = index < length ? InkSpan(layout, index) : null;
        var clear = 1 + caretWidth;
        if (before is { } b && after is { } a) centre = (b.Right + a.Left) / 2;
        else if (before is { } last) centre = Math.Max((last.Right + origin) / 2, last.Right + clear);
        else if (after is { } first) centre = Math.Min(centre, first.Left - clear);
        else if (index == 0) centre = origin - clear;
        return centre;
    }

    /// <summary>The horizontal ink extent of the glyph for character <paramref name="index"/>,
    /// in layout coordinates, or null when it has no ink or no metrics.</summary>
    internal static (double Left, double Right)? InkSpan(TextLayout layout, int index)
    {
        foreach (var line in layout.TextLines)
            foreach (var run in line.TextRuns)
            {
                if (run is not ShapedTextRun shaped) continue;
                var glyphs = shaped.GlyphRun;
                foreach (var g in glyphs.GlyphInfos)
                {
                    if (g.GlyphCluster != index) continue;
                    var ink = GlyphInk(glyphs.GlyphTypeface, g.GlyphIndex, glyphs.FontRenderingEmSize);
                    if (ink is not { } span) return null;
                    var x = layout.HitTestTextPosition(index).X + g.GlyphOffset.X;
                    return (x + span.Left, x + span.Right);
                }
            }
        return null;
    }

    private static readonly Dictionary<(GlyphTypeface Face, ushort Glyph, double Size), (double Left, double Right)?> InkCache = new();

    /// <summary>A glyph's ink extent from its pen origin: the bounds of its outline (a
    /// one-glyph GlyphRun's geometry). GlyphMetrics.Width came back as the advance, and
    /// GlyphRun.InkBounds is whole-pixel and starts at the pen origin.</summary>
    private static (double Left, double Right)? GlyphInk(GlyphTypeface face, ushort glyph, double size)
    {
        if (InkCache.TryGetValue((face, glyph, size), out var known)) return known;
        (double, double)? span = null;
        try
        {
            using var run = new GlyphRun(face, size, " ".AsMemory(), new[] { glyph }, new Point(0, 0));
            var ink = run.BuildGeometry().Bounds;
            if (ink.Width > 0) span = (ink.Left, ink.Right);
        }
        catch (Exception)
        {
            // No platform glyph run (a stub renderer): fall back to the spacing.
        }
        if (InkCache.Count > 512) InkCache.Clear();
        InkCache[(face, glyph, size)] = span;
        return span;
    }

    internal sealed class CaretAdorner : Control
    {
        private readonly TextBox _box;
        private readonly DispatcherTimer _blink = new();
        private TextPresenter? _presenter;
        private AdornerLayer? _layer;
        private bool _shown = true;

        public CaretAdorner(TextBox box)
        {
            _box = box;
            IsHitTestVisible = false;
            // Local value: the theme and app styles set CaretBrush and would win over a current value.
            _box.SetValue(TextBox.CaretBrushProperty, Brushes.Transparent);
            _blink.Tick += (_, _) => { _shown = !_shown; InvalidateVisual(); };
            _box.PropertyChanged += OnBoxPropertyChanged;
            _box.TemplateApplied += OnTemplateApplied;
            _box.AttachedToVisualTree += OnBoxAttached;
            _box.DetachedFromVisualTree += OnBoxDetached;
            _box.AddHandler(InputElement.KeyDownEvent, OnBoxKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            _box.AddHandler(InputElement.TextInputEvent, OnBoxKey, RoutingStrategies.Tunnel, handledEventsToo: true);
            if (_box.IsAttachedToVisualTree()) AttachToLayer();
        }

        public void Detach()
        {
            _blink.Stop();
            _box.PropertyChanged -= OnBoxPropertyChanged;
            _box.TemplateApplied -= OnTemplateApplied;
            _box.AttachedToVisualTree -= OnBoxAttached;
            _box.DetachedFromVisualTree -= OnBoxDetached;
            _box.RemoveHandler(InputElement.KeyDownEvent, OnBoxKey);
            _box.RemoveHandler(InputElement.TextInputEvent, OnBoxKey);
            SetPresenter(null);
            DetachFromLayer();
            _box.ClearValue(TextBox.CaretBrushProperty);
        }

        private void OnBoxAttached(object? sender, VisualTreeAttachmentEventArgs e) => AttachToLayer();
        private void OnBoxDetached(object? sender, VisualTreeAttachmentEventArgs e) => DetachFromLayer();

        private void AttachToLayer()
        {
            DetachFromLayer();
            _layer = AdornerLayer.GetAdornerLayer(_box);
            if (_layer is null) return;
            AdornerLayer.SetAdornedElement(this, _box);
            _layer.Children.Add(this);
            Restart();
        }

        private void DetachFromLayer()
        {
            _blink.Stop();
            _layer?.Children.Remove(this);
            _layer = null;
        }

        private void OnTemplateApplied(object? sender, TemplateAppliedEventArgs e)
            => SetPresenter(e.NameScope.Find<TextPresenter>("PART_TextPresenter"));

        private TextPresenter? Presenter
        {
            get
            {
                if (_presenter is null)
                    SetPresenter(_box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault());
                return _presenter;
            }
        }

        private void SetPresenter(TextPresenter? presenter)
        {
            if (ReferenceEquals(presenter, _presenter)) return;
            if (_presenter != null) _presenter.LayoutUpdated -= OnPresenterLayoutUpdated;
            _presenter = presenter;
            // Any layout pass may have moved the text (a scroll, a resize): redraw.
            if (_presenter != null) _presenter.LayoutUpdated += OnPresenterLayoutUpdated;
        }

        private void OnPresenterLayoutUpdated(object? sender, EventArgs e) => InvalidateVisual();

        private void OnBoxKey(object? sender, RoutedEventArgs e) => Restart();

        private void OnBoxPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
        {
            if (e.Property == TextBox.CaretIndexProperty || e.Property == TextBox.TextProperty
                || e.Property == TextBox.SelectionStartProperty || e.Property == TextBox.SelectionEndProperty
                || e.Property == InputElement.IsFocusedProperty || e.Property == TextBox.CaretBlinkIntervalProperty)
                Restart();
            else if (e.Property == TemplatedControl.ForegroundProperty || e.Property == Visual.IsVisibleProperty)
                InvalidateVisual();
        }

        /// <summary>Solid again and the blink starts over, as the native caret does.</summary>
        private void Restart()
        {
            _shown = true;
            _blink.Stop();
            var interval = _box.CaretBlinkInterval;
            if (_layer != null && _box.IsFocused && interval > TimeSpan.Zero)
            {
                _blink.Interval = interval;
                _blink.Start();
            }
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            if (!_shown || !_box.IsFocused || !_box.IsEffectivelyVisible || _box.SelectionStart != _box.SelectionEnd)
                return;
            var presenter = Presenter;
            var layout = presenter?.TextLayout;
            var top = TopLevel.GetTopLevel(this);
            if (presenter is null || layout is null || top is null) return;
            var toRoot = presenter.TransformToVisual(top);
            var toHere = presenter.TransformToVisual(this);
            if (toRoot is not { } rootMatrix || toHere is not { } hereMatrix) return;

            var scale = top.RenderScaling;
            var width = Math.Max(1, Math.Round(scale)) / scale;
            var index = Math.Clamp(_box.CaretIndex, 0, presenter.Text?.Length ?? 0);
            var line = layout.HitTestTextPosition(index);
            var centre = CaretCentreX(layout, index, presenter.Text?.Length ?? 0, presenter.LetterSpacing, width);

            // Snap in window coordinates (device pixels), then back into ours.
            var a = new Point(centre - width / 2, line.Top).Transform(rootMatrix);
            var b = new Point(centre - width / 2, line.Bottom).Transform(rootMatrix);
            var left = Math.Round(a.X * scale) / scale;
            var y0 = Math.Round(a.Y * scale) / scale;
            var y1 = Math.Round(b.Y * scale) / scale;
            if (!rootMatrix.TryInvert(out var fromRoot)) return;
            var topLeft = new Point(left, y0).Transform(fromRoot).Transform(hereMatrix);
            var bottom = new Point(left, y1).Transform(fromRoot).Transform(hereMatrix);
            // Never cut off at the TextBox's left edge.
            var x = Math.Max(topLeft.X, 0);
            context.FillRectangle(GetBrush(_box) ?? _box.Foreground ?? Brushes.Black,
                new Rect(x, topLeft.Y, width, Math.Max(0, bottom.Y - topLeft.Y)));
        }
    }
}
