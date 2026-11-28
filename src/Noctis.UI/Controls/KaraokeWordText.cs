using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Rendering.Composition;
using Noctis.Models;
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
/// and only their stops move; a Progress change is not even a redraw (see OnPropertyChanged).
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

    /// <summary>The word this cell shows, followed straight from the model instead of through
    /// bindings (the phone's lyrics): its text and timing flags are taken once, its per-frame
    /// values (<see cref="Progress"/>, <see cref="Lift"/>, <see cref="EmphasisMs"/>) read as they
    /// change. Unset (the desktop), all of them come from their properties.</summary>
    public static readonly StyledProperty<WordTiming?> WordProperty =
        AvaloniaProperty.Register<KaraokeWordText, WordTiming?>(nameof(Word));

    /// <summary>Front position through the word (driven by <see cref="LyricsTimeline"/>): 0..1
    /// inside it, beyond either end on a neighbour of the current word, sentinels far away.</summary>
    public double Progress { get => _word?.Progress ?? GetValue(ProgressProperty); set => SetValue(ProgressProperty, value); }
    public double BaseOpacity { get => GetValue(BaseOpacityProperty); set => SetValue(BaseOpacityProperty, value); }
    /// <summary>A rest (or the line start) precedes the word: the edge enters from dark.</summary>
    public bool LeadIn { get => GetValue(LeadInProperty); set => SetValue(LeadInProperty, value); }
    /// <summary>A rest (or the line end) follows the word: the edge has crossed it by its end.</summary>
    public bool TrailOut { get => GetValue(TrailOutProperty); set => SetValue(TrailOutProperty, value); }
    public bool IsEmphasis { get => GetValue(IsEmphasisProperty); set => SetValue(IsEmphasisProperty, value); }
    /// <summary>Time since the word started while its held-note animation runs; NaN at rest.</summary>
    public double EmphasisMs { get => _word?.EmphasisMs ?? GetValue(EmphasisMsProperty); set => SetValue(EmphasisMsProperty, value); }
    public double HeldDurationMs { get => GetValue(HeldDurationMsProperty); set => SetValue(HeldDurationMsProperty, value); }
    public bool IsLastInLine { get => GetValue(IsLastInLineProperty); set => SetValue(IsLastInLineProperty, value); }
    /// <summary>How far the sung word has floated up, 0..1 of <see cref="WordFloat.LiftEm"/>
    /// (driven by <see cref="LyricsTimeline"/>). Drawn as an offset: no layout pass.</summary>
    public double Lift { get => _word?.Lift ?? GetValue(LiftProperty); set => SetValue(LiftProperty, value); }
    /// <summary>The word's line reads right to left (plugin API 1.5): the sweep fills it from the
    /// right. Such a word is always drawn in one piece.</summary>
    public bool RightToLeft { get => GetValue(RightToLeftProperty); set => SetValue(RightToLeftProperty, value); }
    public WordTiming? Word { get => GetValue(WordProperty); set => SetValue(WordProperty, value); }

    // The word followed while in the visual tree (see WordProperty).
    private WordTiming? _word;
    private bool _attached;

    // Styled exactly like the TextBlocks it replaces (app-wide TextBlock font rules apply).
    protected override Type StyleKeyOverride => typeof(TextBlock);

    static KaraokeWordText()
    {
        // No AffectsRender for the per-frame properties: OnPropertyChanged redraws only when
        // the drawing itself changes (see there).
        // A TextBlock clips to its layout box by default (Avalonia 12), which sliced every
        // ink pixel drawn past it: the float, the held-note swell, glyph overhang.
        ClipToBoundsProperty.OverrideDefaultValue<KaraokeWordText>(false);
    }

    /// <summary>One drawn piece: the whole word, or one letter of a held-note word.</summary>
    private sealed class Piece
    {
        public required TextLayout Layout;
        public required LinearGradientBrush Brush;
        /// <summary>The same text in one colour, drawn while the edge is not on the piece.</summary>
        public required TextLayout SolidLayout;
        public required SolidColorBrush Solid;
        public bool Uniform;       // both sides of the band are one colour: Solid is drawn
        public bool DrawnUniform;  // what the last draw used
        public double X;       // left edge inside the word's glyph run
        public double Width;   // advance width

        public TextLayout Current => Uniform ? SolidLayout : Layout;

        public void Dispose()
        {
            Layout.Dispose();
            SolidLayout.Dispose();
        }
    }

    private readonly List<Piece> _pieces = new();
    private double _glyphWidth;   // width the edge travels: the visible word, trailing space excluded
    private double _layoutHeight;
    private bool _perLetter;
    private bool _dirty = true;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WordProperty) Follow(_attached ? Word : null);
        else OnValueChanged(change.Property);
    }

    /// <summary>
    /// S23 (11-23): a sweeping line's words took their values through bindings on every frame,
    /// each value a property change with its event args, boxing and the binding's own relay, a
    /// third of what the page allocated per frame (a GC every ~30 s). Following the word, its
    /// change notifications (cached by the model) reach <see cref="OnValueChanged"/> directly.
    /// </summary>
    private void Follow(WordTiming? word)
    {
        if (ReferenceEquals(word, _word)) return;
        if (_word != null) _word.PropertyChanged -= OnWordChanged;
        _word = word;
        if (word == null)
        {
            ApplyLift();   // back to the float drawn in
            return;
        }
        word.PropertyChanged += OnWordChanged;
        // Bound, each of these was one more listener on the word, and every per-frame change of
        // the word was relayed to all of them (a weak-event snapshot each time). They are set
        // once per word.
        Text = word.Text;
        LeadIn = word.SweepLeadIn;
        TrailOut = word.SweepTrailOut;
        IsEmphasis = word.IsEmphasis;
        HeldDurationMs = word.HeldDurationMs;
        IsLastInLine = word.IsLastInLine;
        // A Persian word fills from its right (#269); the desktop binds this, the phone follows it.
        RightToLeft = word.IsRightToLeft;
        OnValueChanged(ProgressProperty);
        OnValueChanged(LiftProperty);
        OnValueChanged(EmphasisMsProperty);
    }

    private void OnWordChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(WordTiming.Progress): OnValueChanged(ProgressProperty); break;
            case nameof(WordTiming.Lift): OnValueChanged(LiftProperty); break;
            case nameof(WordTiming.EmphasisMs): OnValueChanged(EmphasisMsProperty); break;
        }
    }

    /// <summary>What a change of <paramref name="p"/> redraws or moves.</summary>
    private void OnValueChanged(AvaloniaProperty p)
    {
        if (p == TextProperty || p == FontFamilyProperty || p == FontSizeProperty || p == FontWeightProperty
            || p == FontStyleProperty || p == FontStretchProperty || p == IsEmphasisProperty || p == RightToLeftProperty
            || (p == HeldDurationMsProperty && _effect is not null))
        {
            _dirty = true;
            InvalidateVisual();
            if (_word != null) InvalidateMeasure();   // the letters are built in measure
        }
        else if (p == ProgressProperty || p == BaseOpacityProperty || p == LeadInProperty || p == TrailOutProperty
                 || p == EmphasisMsProperty || p == HeldDurationMsProperty || p == IsLastInLineProperty
                 || p == ForegroundProperty)
        {
            // Brush stops move here, not in Render: changing a brush while the frame is
            // being recorded would re-dirty the visual it is drawn into. A hidden word (the
            // glow layer of every word that is not a held note) catches up when it shows.
            var switched = false;
            if (!IsVisible) _stopsStale = true;
            else if (!_dirty) switched = UpdateStops();
            if (LettersLive)
            {
                // The held-note letters are their own composition visuals: a pose is their
                // transform, written here, not a new drawing (see Letter).
                if (IsVisible && (p == EmphasisMsProperty || p == HeldDurationMsProperty || p == IsLastInLineProperty))
                    ApplyLetterPoses();
                if (IsVisible) RedrawLetters(switched);
                return;
            }
            // The brushes are composition resources: a moved stop reaches the screen without
            // this word being drawn again. Re-recording it every frame (a new draw list, its
            // resource table and glyph runs, ~2-4 KB a word) was most of the ~4 MB a second
            // the phone's lyrics allocated while a line swept, a GC every second (S23, 11-14).
            // Only what moves the glyphs redraws: a held note's letters (their pose follows
            // the held-note clock) and an effect's pose (it reads every input), and a piece
            // the edge enters or leaves (it changes brush, see SetStops).
            if (IsVisible && (_dirty || switched || Posed
                || (_perLetter && (p == EmphasisMsProperty || p == HeldDurationMsProperty || p == IsLastInLineProperty))))
                InvalidateVisual();
        }
        else if (p == IsVisibleProperty && IsVisible && _stopsStale)
        {
            _stopsStale = false;
            if (!_dirty && UpdateStops()) InvalidateVisual();
        }
        else if (p == LiftProperty)
        {
            // The float is drawn in quarter device pixels (LiftPixels, Skia's own glyph
            // precision): a frame that leaves it on the same quarter does not redraw. Hidden
            // (a glow layer at rest), it draws when it shows. Followed from the word, the
            // float is the composition visual's own offset (ApplyLift): only the start and the
            // end of a rise redraw (it is drawn off the pixel grid while it moves).
            if (LiftOnCompositor)
            {
                ApplyLift();
                if (LettersLive)
                {
                    if (IsVisible) RedrawLetters(false);
                }
                else if (IsVisible && (_dirty || IsMoving != _drawnMoving)) InvalidateVisual();
            }
            else if (IsVisible && (_dirty || Posed || LiftPixels() != _drawnLift || IsMoving != _drawnMoving))
                InvalidateVisual();
        }
    }

    /// <summary>
    /// The float moves the word's composition visual instead of being drawn into it (cells
    /// following a <see cref="Word"/>, no plugin effect posing the words). S23 (11-23): each
    /// quarter pixel of a rising word recorded the word's drawing again (a new draw list, its
    /// resource table and glyph runs), most of what a sweeping line allocated per frame. The
    /// screen is the same: the drawing is placed exactly as before, the offset added after it.
    /// </summary>
    private bool LiftOnCompositor => _word != null && LyricsWordEffect.Current is null;

    // The offset ApplyLift last gave the composition visual.
    private double _appliedLift;

    private void ApplyLift()
    {
        var y = LiftOnCompositor ? -LiftPixels() : 0;
        // ReSharper disable once CompareOfFloatsByEqualityOperator
        if (y == _appliedLift || ElementComposition.GetElementVisual(this) is not { } visual) return;
        _appliedLift = y;
        visual.Translation = new Vector3D(0, y, 0);
    }

    // Stops left unmoved while hidden.
    private bool _stopsStale;

    // The float the last draw used, and whether it was drawn off the pixel grid.
    private double _drawnLift;
    private bool _drawnMoving;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        LyricsWordEffect.Changed += OnEffectChanged;
        _dirty = true;
        _attached = true;
        Follow(Word);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        LyricsWordEffect.Changed -= OnEffectChanged;
        _attached = false;
        Follow(null);
        _appliedLift = 0;   // the next attach brings a new composition visual
        foreach (var piece in _pieces) piece.Dispose();
        _pieces.Clear();
        _effect = null;
        _dirty = true;
        _lettersFor = -1;   // rebuilt with the pieces at the next measure
    }

    private void OnEffectChanged(object? sender, EventArgs e)
    {
        _dirty = true;
        if (_word != null) InvalidateMeasure();
        ApplyLift();   // an effect draws the float itself
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

    /// <summary>Tests: every piece was last drawn in one colour (no gradient).</summary>
    internal bool DrawnUniform => _pieces.Count > 0 && _pieces.TrueForAll(p => p.DrawnUniform);

    /// <summary>Tests: draw the gradient even where it is one colour, to compare the two.</summary>
    internal bool ForceGradient
    {
        get => _forceGradient;
        set
        {
            _forceGradient = value;
            _dirty = true;
        }
    }
    private bool _forceGradient;

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
        _piecesVersion++;
        foreach (var piece in _pieces) piece.Dispose();
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
        var solid = new SolidColorBrush(Colors.White);
        return new Piece
        {
            Layout = new TextLayout(text, typeface, FontSize, brush),
            Brush = brush,
            SolidLayout = new TextLayout(text, typeface, FontSize, solid),
            Solid = solid,
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

    /// <returns>True when a piece changed brush (the edge entered or left it): the word must
    /// be drawn again.</returns>
    private bool UpdateStops()
    {
        if (_pieces.Count == 0 || _glyphWidth <= 0) return false;
        var (lit, dim) = Colours();
        var (lo, hi) = Band();
        var rtl = RightToLeft;
        // A right-to-left word fills from its right edge: the band is mirrored and the lit
        // side is the one past it (SetStops).
        if (rtl) (lo, hi) = (_glyphWidth - hi, _glyphWidth - lo);
        var posed = Posed;
        if (!_perLetter && !posed)
        {
            SetStops(_pieces[0], lo, hi, lit, dim, rtl, _forceGradient);
            return _pieces[0].Uniform != _pieces[0].DrawnUniform;
        }

        var parameters = posed ? default : EmphasisMotion.For(HeldDurationMs, IsLastInLine);
        var switched = false;
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
            SetStops(piece, ToLocal(lo), ToLocal(hi), lit, dim, rtl, _forceGradient);
            switched |= piece.Uniform != piece.DrawnUniform;
        }
        return switched;
    }

    // ── Held-note letters on the compositor (followed cells: the phone) ──

    /// <summary>
    /// S23 (11-23): a held note's letters swell, push and lift each on its own curve, and drawn
    /// into the word they were recorded again on every frame of the hold (a new draw list with
    /// every letter, for the word and its glow), the largest cost of a word-timed song. Here each
    /// letter of a followed held-note word is a child visual of its own, drawn once, and its pose
    /// is that visual's composition transform (scale about the letter's centre, then the push and
    /// lift), written per frame without drawing anything. The sweep's band still maps through the
    /// pose (UpdateStops): only the letters' brushes move. The same picture as the word drawn
    /// letter by letter (KaraokeWordFollowTests). Plugin effects and the desktop keep the drawn path.
    /// </summary>
    private sealed class Letter : Visual
    {
        private readonly KaraokeWordText _owner;
        public int Index;
        public bool DrawnMoving;

        public Letter(KaraokeWordText owner, int index)
        {
            _owner = owner;
            Index = index;
        }

        public override void Render(DrawingContext context) => _owner.RenderLetter(this, context);

        public void Place(Rect bounds) => Bounds = bounds;

        public void Redraw() => InvalidateVisual();

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            _owner.ApplyLetterPose(this);   // a new composition visual
        }
    }

    private readonly List<Letter> _letters = new();
    // The pieces the letters were made for (EnsurePieces counts them), -1 when none.
    private int _piecesVersion;
    private int _lettersFor = -1;
    private Point _letterOrigin;

    /// <summary>The letters stand for the current pieces and draw them (the word draws nothing).</summary>
    private bool LettersLive => _lettersFor == _piecesVersion && !_dirty && _letters.Count > 0 && _letters.Count == _pieces.Count;

    /// <summary>Tests: the held-note letters are composition visuals of their own, and how many
    /// times one was drawn.</summary>
    internal bool LettersOnCompositor => LettersLive;
    internal int LetterDraws { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        var size = base.MeasureOverride(availableSize);
        if (_word != null && _dirty && _attached)
        {
            EnsurePieces();
            UpdateStops();
        }
        SyncLetters();
        return size;
    }

    /// <summary>Makes, keeps or removes the letter visuals to match the pieces (never while drawing).</summary>
    private void SyncLetters()
    {
        var want = _attached && !_dirty && _word != null && _perLetter && !Posed && LyricsWordEffect.Current is null && _pieces.Count > 1;
        if (!want)
        {
            if (_letters.Count == 0) return;
            foreach (var letter in _letters) VisualChildren.Remove(letter);
            _letters.Clear();
            _lettersFor = -1;
            InvalidateVisual();
            return;
        }
        if (_lettersFor == _piecesVersion && _letters.Count == _pieces.Count) return;
        while (_letters.Count > _pieces.Count)
        {
            VisualChildren.Remove(_letters[^1]);
            _letters.RemoveAt(_letters.Count - 1);
        }
        while (_letters.Count < _pieces.Count)
        {
            var letter = new Letter(this, _letters.Count);
            _letters.Add(letter);
            VisualChildren.Add(letter);
        }
        _lettersFor = _piecesVersion;
        _letterOrigin = TextOrigin();
        PlaceLetters();
        InvalidateVisual();   // the word stops drawing them itself
    }

    /// <summary>Where TextBlock.Render puts the text (its padding, rounded as layout rounds it).</summary>
    private Point TextOrigin()
    {
        var scale = Avalonia.Layout.LayoutHelper.GetLayoutScale(this);
        var padding = Avalonia.Layout.LayoutHelper.RoundLayoutThickness(Padding, scale);
        return new Point(padding.Left, padding.Top);
    }

    private void PlaceLetters()
    {
        if (!LettersLive) return;
        var box = new Rect(Bounds.Size);
        foreach (var letter in _letters)
        {
            letter.Place(box);
            letter.Redraw();
        }
        ApplyLetterPoses();
    }

    private void ApplyLetterPoses()
    {
        foreach (var letter in _letters) ApplyLetterPose(letter);
    }

    /// <summary>The letter's pose as its visual's transform: scale about its centre, then the push
    /// and lift (LetterTransform, the drawn path's matrix, in that visual's terms).</summary>
    private void ApplyLetterPose(Letter letter)
    {
        if (!LettersLive || letter.Index >= _pieces.Count || ElementComposition.GetElementVisual(letter) is not { } visual) return;
        var piece = _pieces[letter.Index];
        var pose = EmphasisMotion.CharPose(EmphasisMs, letter.Index, _pieces.Count, EmphasisMotion.For(HeldDurationMs, IsLastInLine));
        visual.CenterPoint = new Vector3D(_letterOrigin.X + piece.X + piece.Width / 2, _letterOrigin.Y + _layoutHeight / 2, 0);
        visual.Scale = new Vector3D(pose.Scale, pose.Scale, 1);
        visual.Translation = new Vector3D(pose.OffsetXEm * FontSize, pose.OffsetYEm * FontSize, 0);
    }

    /// <summary>A letter drawn again only when its drawing changes: its brush switches (the edge
    /// came onto it or left it), or the hold starts or ends (drawn off the pixel grid while it
    /// moves, see Unsnapped).</summary>
    private void RedrawLetters(bool switched)
    {
        var moving = IsMoving;
        foreach (var letter in _letters)
        {
            if (letter.Index >= _pieces.Count) continue;
            var piece = _pieces[letter.Index];
            if (letter.DrawnMoving != moving || (switched && piece.Uniform != piece.DrawnUniform)) letter.Redraw();
        }
    }

    private void RenderLetter(Letter letter, DrawingContext context)
    {
        if (!LettersLive || letter.Index >= _pieces.Count || _glyphWidth <= 0) return;
        var piece = _pieces[letter.Index];
        LetterDraws++;
        letter.DrawnMoving = IsMoving;
        var at = Matrix.CreateTranslation(_letterOrigin.X + piece.X, _letterOrigin.Y);
        if (letter.DrawnMoving) at = Unsnapped * at;
        using (context.PushTransform(at))
            Draw(piece, context);
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
                            Draw(_pieces[i], context);
                    }
                    else Draw(_pieces[i], context);
                }
            }
            return;
        }

        if (LettersLive)
        {
            // The letters draw themselves (Letter.Render) at this origin; one that moved (a new
            // padding) re-places them after this frame.
            _drawnLift = 0;
            _drawnMoving = IsMoving;
            if (origin != _letterOrigin)
            {
                _letterOrigin = origin;
                Avalonia.Threading.Dispatcher.UIThread.Post(PlaceLetters, Avalonia.Threading.DispatcherPriority.Render);
            }
            return;
        }
        if (_word != null && _perLetter && !Posed && LyricsWordEffect.Current is null)
            // Built after this frame (never while drawing); drawn here until then.
            Avalonia.Threading.Dispatcher.UIThread.Post(SyncLetters, Avalonia.Threading.DispatcherPriority.Render);

        _drawnLift = LiftOnCompositor ? 0 : LiftPixels();
        _drawnMoving = IsMoving;
        var transform = Matrix.CreateTranslation(origin.X, origin.Y - _drawnLift);
        if (_drawnMoving) transform = Unsnapped * transform;
        using var offset = context.PushTransform(transform);
        if (!_perLetter)
        {
            Draw(_pieces[0], context);
            return;
        }

        var parameters = EmphasisMotion.For(HeldDurationMs, IsLastInLine);
        for (int i = 0; i < _pieces.Count; i++)
        {
            using (context.PushTransform(LetterTransform(i, parameters, out _, out _)))
                Draw(_pieces[i], context);
        }
    }

    private static void Draw(Piece piece, DrawingContext context)
    {
        piece.DrawnUniform = piece.Uniform;
        piece.Current.Draw(context, default);
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
    /// that has finished rising rests on the pixel grid, and the way up to quarter device
    /// pixels, the finest step Skia places an unsnapped glyph at (see <see cref="Unsnapped"/>).</summary>
    private double LiftPixels()
    {
        var lift = Lift;
        if (double.IsNaN(lift) || lift <= 0) return 0;
        var scaling = VisualRoot is TopLevel { RenderScaling: > 0 and var s } ? s : 1.0;
        var full = Math.Round(WordFloat.LiftEm * FontSize * scaling);
        return Math.Round(Math.Min(1, lift) * full * 4) / (4 * scaling);
    }

    /// <summary>Points the piece's two stops at the band (piece-local pixels, unclamped):
    /// clamped to the piece, each carrying the colour the band has at that point.</summary>
    private static void SetStops(Piece piece, double lo, double hi, Color lit, Color dim, bool rightToLeft = false, bool forceGradient = false)
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
        // One opaque colour all over (the edge is off this piece: sung, or a line not being
        // sung): drawn with a solid brush. A gradient brush is a new Skia shader on every frame
        // the word is drawn, for every word on screen - the largest part of what the phone's
        // lyrics allocated (headless Skia, 11-17). The stops wait until the edge comes back.
        // A translucent colour (the unsung ghost) keeps the gradient: Skia draws translucent
        // solid text with a different glyph gamma, 7% dimmer than the same gradient.
        piece.Uniform = c0 == c1 && (c0.A is 255 or 0) && !forceGradient;
        if (piece.Uniform)
        {
            if (piece.Solid.Color != c0) piece.Solid.Color = c0;
            return;
        }
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
