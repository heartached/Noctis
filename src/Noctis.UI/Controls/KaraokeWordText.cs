using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// One karaoke word: the dim (unsung) and bright (sung) text in a single draw, split by a
/// soft edge that follows <see cref="Progress"/>, plus the Apple Music held-note letter
/// animation (<see cref="EmphasisMotion"/>) for <see cref="IsEmphasis"/> words and the
/// float every sung word rises by (<see cref="Lift"/>, <see cref="WordFloat"/>). With
/// <see cref="BaseOpacity"/> 0 it draws only the sung part — the held-note glow layer.
///
/// It replaces a dimmed base TextBlock with a second TextBlock on top whose Foreground was
/// a new gradient brush every frame: a TextBlock rebuilds its text layout and invalidates
/// its measure when Foreground changes, so every frame of a sweep ran a layout pass up the
/// lyrics list (measured: 30 of 30 frames). Here the brushes are created once per layout
/// and only their stops move; a Progress change is a render-only invalidation.
///
/// Measure, arrange, fonts and styles are the TextBlock's own (it IS one, styled as one),
/// so the cell keeps exactly the geometry the template always had.
/// </summary>
public class KaraokeWordText : TextBlock
{
    public static readonly StyledProperty<double> ProgressProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(Progress), KaraokeSweep.InertFuture);

    /// <summary>Opacity of the unsung text (1 on inactive lines, a soft ghost while active).</summary>
    public static readonly StyledProperty<double> BaseOpacityProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(BaseOpacity), 1.0);

    public static readonly StyledProperty<bool> LeadInProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(LeadIn));

    public static readonly StyledProperty<bool> TrailOutProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(TrailOut));

    public static readonly StyledProperty<bool> IsEmphasisProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(IsEmphasis));

    public static readonly StyledProperty<double> EmphasisMsProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(EmphasisMs), double.NaN);

    public static readonly StyledProperty<double> HeldDurationMsProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(HeldDurationMs));

    public static readonly StyledProperty<bool> IsLastInLineProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(IsLastInLine));

    public static readonly StyledProperty<double> LiftProperty =
        AvaloniaProperty.Register<KaraokeWordText, double>(nameof(Lift));

    public static readonly StyledProperty<bool> RightToLeftProperty =
        AvaloniaProperty.Register<KaraokeWordText, bool>(nameof(RightToLeft));

    /// <summary>Front position through the word (driven by <see cref="LyricsTimeline"/>): 0..1
    /// inside it, beyond either end on a neighbour of the current word, sentinels far away.</summary>
    public double Progress { get => GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double BaseOpacity { get => GetValue(BaseOpacityProperty); set => SetValue(BaseOpacityProperty, value); }
    /// <summary>A rest (or the line start) precedes the word: the edge enters from dark.</summary>
    public bool LeadIn { get => GetValue(LeadInProperty); set => SetValue(LeadInProperty, value); }
    /// <summary>A rest (or the line end) follows the word: the edge has crossed it by its end.</summary>
    public bool TrailOut { get => GetValue(TrailOutProperty); set => SetValue(TrailOutProperty, value); }
    public bool IsEmphasis { get => GetValue(IsEmphasisProperty); set => SetValue(IsEmphasisProperty, value); }
    /// <summary>Time since the word started while its held-note animation runs; NaN at rest.</summary>
    public double EmphasisMs { get => GetValue(EmphasisMsProperty); set => SetValue(EmphasisMsProperty, value); }
    public double HeldDurationMs { get => GetValue(HeldDurationMsProperty); set => SetValue(HeldDurationMsProperty, value); }
    public bool IsLastInLine { get => GetValue(IsLastInLineProperty); set => SetValue(IsLastInLineProperty, value); }
    /// <summary>How far the sung word has floated up, 0..1 of <see cref="WordFloat.LiftEm"/>
    /// (driven by <see cref="LyricsTimeline"/>). Drawn as an offset: no layout pass.</summary>
    public double Lift { get => GetValue(LiftProperty); set => SetValue(LiftProperty, value); }
    /// <summary>The word's line reads right to left (plugin API 1.5): the sweep fills it from the
    /// right. Such a word is always drawn in one piece.</summary>
    public bool RightToLeft { get => GetValue(RightToLeftProperty); set => SetValue(RightToLeftProperty, value); }

    // Styled exactly like the TextBlocks it replaces (app-wide TextBlock font rules apply).
    protected override Type StyleKeyOverride => typeof(TextBlock);

    static KaraokeWordText()
    {
        AffectsRender<KaraokeWordText>(ProgressProperty, BaseOpacityProperty, LeadInProperty, TrailOutProperty,
            EmphasisMsProperty, HeldDurationMsProperty, IsLastInLineProperty, LiftProperty);
        // A TextBlock clips to its layout box by default (Avalonia 12), which sliced every
        // ink pixel drawn past it: the float, the held-note swell, glyph overhang.
        ClipToBoundsProperty.OverrideDefaultValue<KaraokeWordText>(false);
    }

    /// <summary>One drawn piece: the whole word, or one letter of a held-note word.</summary>
    private sealed class Piece
    {
        public required TextLayout Layout;
        public required LinearGradientBrush Brush;
        public double X;       // left edge inside the word's glyph run
        public double Width;   // advance width
    }

    private readonly List<Piece> _pieces = new();
    private double _glyphWidth;   // width the edge travels: the visible word, trailing space excluded
    private double _layoutHeight;
    private bool _perLetter;
    private bool _dirty = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        var p = change.Property;
        if (p == TextProperty || p == FontFamilyProperty || p == FontSizeProperty || p == FontWeightProperty
            || p == FontStyleProperty || p == FontStretchProperty || p == IsEmphasisProperty || p == RightToLeftProperty
            || (p == HeldDurationMsProperty && _effect is not null))
        {
            _dirty = true;
            InvalidateVisual();
        }
        else if (p == ProgressProperty || p == BaseOpacityProperty || p == LeadInProperty || p == TrailOutProperty
                 || p == EmphasisMsProperty || p == HeldDurationMsProperty || p == IsLastInLineProperty
                 || p == ForegroundProperty)
        {
            // Brush stops move here, not in Render: changing a brush while the frame is
            // being recorded would re-dirty the visual it is drawn into.
            if (!_dirty) UpdateStops();
            InvalidateVisual();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LyricsWordEffect.Changed += OnEffectChanged;
        _dirty = true;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LyricsWordEffect.Changed -= OnEffectChanged;
        foreach (var piece in _pieces) piece.Layout.Dispose();
        _pieces.Clear();
        _effect = null;
        _dirty = true;
    }

    private void OnEffectChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        InvalidateVisual();
    }

    // ── Lyrics effect (plugin API 1.5) ──

    /// <summary>The effect this word was laid out for, while it poses the word (mode not Native).</summary>
    private IWordEffect? _effect;
    private WordDrawMode _mode;
    private int _letterCount;

    /// <summary>Tests: pieces drawn (1 = the word in one piece) and the effect's mode.</summary>
    internal int PieceCount { get { EnsurePieces(); return _pieces.Count; } }
    internal WordDrawMode EffectMode { get { EnsurePieces(); return _effect is null ? WordDrawMode.Native : _mode; } }

    private WordPoseInput Input() => new(Progress, HeldDurationMs, Lift, IsEmphasis, IsLastInLine, RightToLeft,
        _letterCount, FontSize, LyricsWordEffect.GetSurface(this));

    /// <summary>The effect's pose of piece <paramref name="i"/>; an effect that throws is dropped
    /// for this word (Noctis's own motion comes back) and the host is told through
    /// <see cref="EffectFailed"/>.</summary>
    private LetterPose Pose(int i)
    {
        var effect = _effect;
        if (effect is null) return LetterPose.Rest;
        try
        {
            var pose = effect.Pose(Input(), i);
            if (!double.IsFinite(pose.Scale) || pose.Scale <= 0.05) pose = pose with { Scale = 1 };
            if (!double.IsFinite(pose.OffsetXEm)) pose = pose with { OffsetXEm = 0 };
            if (!double.IsFinite(pose.OffsetYEm)) pose = pose with { OffsetYEm = 0 };
            if (!double.IsFinite(pose.Glow)) pose = pose with { Glow = 1 };
            return pose;
        }
        catch (Exception ex)
        {
            _effect = null;
            _dirty = true;
            EffectFailed?.Invoke(effect, ex);
            return LetterPose.Rest;
        }
    }

    /// <summary>Raised (UI thread, while drawing) when an effect threw; the host withdraws it.</summary>
    public static event Action<IWordEffect, Exception>? EffectFailed;

    /// <summary>Reports an effect that threw outside a word (the line directions).</summary>
    public static void ReportEffectFailure(IWordEffect effect, Exception ex) => EffectFailed?.Invoke(effect, ex);

    /// <summary>A pose as a transform about the piece's centre, in word space.</summary>
    private Matrix PoseTransform(int i, in LetterPose pose)
    {
        var piece = _pieces[i];
        var cx = piece.X + piece.Width / 2;
        var cy = _layoutHeight / 2;
        return Matrix.CreateTranslation(piece.X - cx, -cy)
               * Matrix.CreateScale(pose.Scale, pose.Scale)
               * Matrix.CreateTranslation(cx + pose.OffsetXEm * FontSize, cy + pose.OffsetYEm * FontSize);
    }

    private bool Posed => _effect is not null && _mode != WordDrawMode.Native;

    /// <summary>
    /// Arabic, Syriac, N'Ko and the other joining scripts draw a letter in the form its
    /// neighbours give it. Drawn one letter per layout (the held-note animation), every letter
    /// came out in its isolated form: "سلام" as "س ل ا م". Such a word is drawn in one piece.
    /// </summary>
    internal static bool HasJoiningScript(string text)
    {
        foreach (var c in text)
        {
            if ((c >= '؀' && c <= 'ࣿ') || (c >= 'ﭐ' && c <= '﷿') || (c >= 'ﹰ' && c <= '﻿')
                || (c >= '᠀' && c <= '᢯') || (c >= 'ꡀ' && c <= '꡿'))
                return true;
            // Surrogates: Adlam, Hanifi Rohingya and other supplementary joining scripts.
            if (char.IsHighSurrogate(c) && (c == '\uD803' || c == '\uD83A')) return true;
        }
        return false;
    }

    private void EnsurePieces()
    {
        if (!_dirty) return;
        _dirty = false;
        foreach (var piece in _pieces) piece.Layout.Dispose();
        _pieces.Clear();

        var sweep = (Text ?? string.Empty).TrimEnd();
        var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretch);
        using var word = new TextLayout(sweep, typeface, FontSize, Brushes.White);
        _glyphWidth = word.WidthIncludingTrailingWhitespace;
        _layoutHeight = word.Height;
        _effect = null;
        _mode = WordDrawMode.Native;
        if (sweep.Length == 0) return;

        var trimmed = sweep.TrimStart();
        _letterCount = new StringInfo(trimmed).LengthInTextElements;
        if (LyricsWordEffect.Current is { } effect)
        {
            _effect = effect;
            try { _mode = effect.Mode(Input()); }
            catch (Exception ex)
            {
                _effect = null;
                EffectFailed?.Invoke(effect, ex);
            }
            if (_mode == WordDrawMode.Native) _effect = null;
        }

        var joined = RightToLeft || HasJoiningScript(trimmed);
        _perLetter = !joined && (_effect is not null ? _mode == WordDrawMode.Letters : IsEmphasis);
        if (!_perLetter)
        {
            _pieces.Add(MakePiece(sweep, typeface, 0, _glyphWidth));
            return;
        }

        // Held-note words draw letter by letter, each at the position the whole-word layout
        // gives it (kerning kept), so a letter can swell and lift on its own. Drawn this
        // way at rest too: switching layouts when the animation starts would shift glyphs.
        var lead = sweep.Length - sweep.TrimStart().Length;
        var starts = StringInfo.ParseCombiningCharacters(sweep[lead..]);
        for (int i = 0; i < starts.Length; i++)
        {
            var from = lead + starts[i];
            var to = i + 1 < starts.Length ? lead + starts[i + 1] : sweep.Length;
            var x0 = word.HitTestTextPosition(from).X;
            var x1 = to < sweep.Length ? word.HitTestTextPosition(to).X : _glyphWidth;
            _pieces.Add(MakePiece(sweep[from..to], typeface, x0, x1 - x0));
        }
    }

    private Piece MakePiece(string text, Typeface typeface, double x, double width)
    {
        // Absolute endpoints across the piece's own advance: stop offsets are then plain
        // fractions of it, and only those move per frame.
        var brush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Absolute),
            EndPoint = new RelativePoint(Math.Max(1, width), 0, RelativeUnit.Absolute),
            GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(Colors.White, 1) },
        };
        return new Piece
        {
            Layout = new TextLayout(text, typeface, FontSize, brush),
            Brush = brush,
            X = x,
            Width = Math.Max(1, width),
        };
    }

    /// <summary>Where the soft edge is, in word-glyph pixels.</summary>
    private (double Lo, double Hi) Band()
    {
        var feather = KaraokeBand.Feather(_glyphWidth, FontSize);
        var center = KaraokeBand.Center(Progress, feather, LeadIn, TrailOut);
        return ((center - feather) * _glyphWidth, (center + feather) * _glyphWidth);
    }

    private (Color Lit, Color Dim) Colours()
    {
        var color = (Foreground as ISolidColorBrush)?.Color ?? Colors.White;
        var dim = Color.FromArgb((byte)Math.Round(color.A * Math.Clamp(BaseOpacity, 0, 1)), color.R, color.G, color.B);
        return (color, dim);
    }

    /// <summary>A letter's held-note pose as a transform about its centre, in word space.</summary>
    private Matrix LetterTransform(int i, in EmphasisParams parameters, out double scale, out double dx)
    {
        var piece = _pieces[i];
        var pose = EmphasisMotion.CharPose(EmphasisMs, i, _pieces.Count, parameters);
        scale = pose.Scale;
        dx = pose.OffsetXEm * FontSize;
        var dy = pose.OffsetYEm * FontSize;
        var cx = piece.X + piece.Width / 2;
        var cy = _layoutHeight / 2;
        return Matrix.CreateTranslation(piece.X - cx, -cy)
               * Matrix.CreateScale(scale, scale)
               * Matrix.CreateTranslation(cx + dx, cy + dy);
    }

    private void UpdateStops()
    {
        if (_pieces.Count == 0 || _glyphWidth <= 0) return;
        var (lit, dim) = Colours();
        var (lo, hi) = Band();
        var rtl = RightToLeft;
        // A right-to-left word fills from its right edge: the band is mirrored and the lit
        // side is the one past it (SetStops).
        if (rtl) (lo, hi) = (_glyphWidth - hi, _glyphWidth - lo);
        var posed = Posed;
        if (!_perLetter && !posed)
        {
            SetStops(_pieces[0], lo, hi, lit, dim, rtl);
            return;
        }

        var parameters = posed ? default : EmphasisMotion.For(HeldDurationMs, IsLastInLine);
        for (int i = 0; i < _pieces.Count; i++)
        {
            var piece = _pieces[i];
            double s, dx;
            if (posed)
            {
                var pose = Pose(i);
                s = pose.Scale;
                dx = pose.OffsetXEm * FontSize;
            }
            else LetterTransform(i, parameters, out s, out dx);
            var cx = piece.X + piece.Width / 2;
            // The edge stays in word space (AMLL masks the word and its letters move
            // inside the mask): map the band back through this letter's swell and push.
            double ToLocal(double xWord) => (xWord - dx - cx) / s + cx - piece.X;
            SetStops(piece, ToLocal(lo), ToLocal(hi), lit, dim, rtl);
        }
    }

    // TextBlock.Render (sealed) draws the background and resolves where the text goes
    // (padding, alignment); only the text itself is drawn differently.
    protected override void RenderTextLayout(DrawingContext context, Point origin)
    {
        if (_dirty)
        {
            EnsurePieces();
            UpdateStops();
        }
        if (_pieces.Count == 0 || _glyphWidth <= 0) return;

        if (Posed)
        {
            // The effect's pose replaces Noctis's float and held-note letters. Drawn off the
            // pixel grid while the word moves (see Unsnapped); the glow layer (BaseOpacity 0)
            // takes the effect's glow strength on top of its own fade.
            var at = Matrix.CreateTranslation(origin.X, origin.Y);
            if (Progress is > KaraokeSweep.InertFuture and < KaraokeSweep.InertPast || Lift is > 0 and < 1)
                at = Unsnapped * at;
            using var placed = context.PushTransform(at);
            var glowLayer = BaseOpacity <= 0;
            for (int i = 0; i < _pieces.Count; i++)
            {
                var pose = Pose(i);
                if (_effect is null) break; // it threw: Noctis's own look from the next frame
                using (context.PushTransform(PoseTransform(i, pose)))
                {
                    if (glowLayer)
                    {
                        using (context.PushOpacity(Math.Clamp(pose.Glow, 0, 1)))
                            _pieces[i].Layout.Draw(context, default);
                    }
                    else _pieces[i].Layout.Draw(context, default);
                }
            }
            return;
        }

        var transform = Matrix.CreateTranslation(origin.X, origin.Y - LiftPixels());
        if (IsMoving) transform = Unsnapped * transform;
        using var offset = context.PushTransform(transform);
        if (!_perLetter)
        {
            _pieces[0].Layout.Draw(context, default);
            return;
        }

        var parameters = EmphasisMotion.For(HeldDurationMs, IsLastInLine);
        for (int i = 0; i < _pieces.Count; i++)
        {
            using (context.PushTransform(LetterTransform(i, parameters, out _, out _)))
                _pieces[i].Layout.Draw(context, default);
        }
    }

    /// <summary>
    /// Skia draws text under an axis-aligned transform with each glyph's baseline rounded to
    /// a whole pixel, so a 2 px float rose in two 1 px hops (and the held-note letters
    /// jumped the same way). A shear too small to see (0.1 px over 100 px; Skia's
    /// axis-aligned test allows 1/4096) moves glyphs in quarter pixels instead. Only used
    /// while something moves: at rest the text is drawn exactly as before, crisp on the grid.
    /// </summary>
    private static readonly Matrix Unsnapped = new(1, 1e-3, 0, 1, 0, 0);

    private bool IsMoving =>
        Lift is > 0 and < 1 || (_perLetter && !double.IsNaN(EmphasisMs));

    /// <summary>The float in DIPs. The full lift is rounded to whole device pixels, so a word
    /// that has finished rising rests on the pixel grid.</summary>
    private double LiftPixels()
    {
        var lift = Lift;
        if (double.IsNaN(lift) || lift <= 0) return 0;
        var scaling = VisualRoot is TopLevel { RenderScaling: > 0 and var s } ? s : 1.0;
        var full = Math.Round(WordFloat.LiftEm * FontSize * scaling) / scaling;
        return Math.Min(1, lift) * full;
    }

    /// <summary>Points the piece's two stops at the band (piece-local pixels, unclamped):
    /// clamped to the piece, each carrying the colour the band has at that point.</summary>
    private static void SetStops(Piece piece, double lo, double hi, Color lit, Color dim, bool rightToLeft = false)
    {
        var w = piece.Width;
        var o0 = Math.Clamp(lo / w, 0, 1);
        var o1 = Math.Clamp(hi / w, 0, 1);
        var c0 = Mix(dim, lit, Lit(o0 * w));
        var c1 = Mix(dim, lit, Lit(o1 * w));
        // Right to left the lit side is past the band (its right), the mirror of LitAt.
        double Lit(double x) => rightToLeft
            ? (hi <= lo ? (x >= hi ? 1 : 0) : Math.Clamp((x - lo) / (hi - lo), 0, 1))
            : KaraokeBand.LitAt(x, lo, hi);
        var stops = piece.Brush.GradientStops;
        Set(stops[0], o0, c0);
        Set(stops[1], o1, c1);

        static void Set(GradientStop stop, double offset, Color c)
        {
            // ReSharper disable once CompareOfFloatsByEqualityOperator
            if (stop.Offset != offset) stop.Offset = offset;
            if (stop.Color != c) stop.Color = c;
        }
    }

    private static Color Mix(Color a, Color b, double t) => Color.FromArgb(
        (byte)Math.Round(a.A + (b.A - a.A) * t), b.R, b.G, b.B);
}
