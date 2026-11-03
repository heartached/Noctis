using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Noctis.Plugins.Chords.Analysis;

namespace Noctis.Plugins.Chords.Views;

/// <summary>
/// A strip of the chords around the playhead: <see cref="Before"/> seconds behind it and
/// <see cref="After"/> ahead, scrolling with the song; the chord under the playhead is lit.
/// Clicking a chord seeks to its start. Labels are laid out once per chord name and theme.
/// </summary>
public sealed class ChordLane : Control
{
    public const double Before = 3;
    public const double After = 11;
    private const double Gap = 3;

    private readonly Dictionary<(string, bool), FormattedText> _labels = new();
    private ChordsTheme _theme = ChordsTheme.Dark;

    public ChordLane()
    {
        Height = 52;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Hand);
    }

    public ChordAnalysis? Analysis { get; set; }
    public double Position { get; set; }

    public ChordsTheme Palette
    {
        get => _theme;
        set
        {
            _theme = value;
            _labels.Clear();
            InvalidateVisual();
        }
    }

    /// <summary>Raised with the start of the chord the user clicked.</summary>
    public event EventHandler<TimeSpan>? SeekRequested;

    /// <summary>Seconds into the song at <paramref name="x"/>.</summary>
    public double TimeAt(double x) => Position - Before + x / Math.Max(1, Bounds.Width) * (Before + After);

    private double X(double seconds) => (seconds - (Position - Before)) / (Before + After) * Bounds.Width;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (Analysis is not { } a || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var i = a.IndexAt(TimeAt(e.GetPosition(this).X));
        if (i < 0) return;
        SeekRequested?.Invoke(this, TimeSpan.FromSeconds(a.Segments[i].Start));
        e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var w = Bounds.Width;
        var h = Bounds.Height;
        if (w <= 0 || h <= 0) return;
        context.FillRectangle(Brushes.Transparent, new Rect(0, 0, w, h)); // the whole strip takes clicks
        if (Analysis is not { } a) return;
        var from = Position - Before;
        var to = Position + After;
        var now = a.IndexAt(Position);
        var first = Math.Max(0, a.IndexAt(Math.Max(0, from)));
        for (var i = first; i < a.Segments.Count; i++)
        {
            var s = a.Segments[i];
            if (s.Start > to) break;
            if (s.End < from) continue;
            var x0 = Math.Max(0, X(s.Start)) + (s.Start >= from ? Gap / 2 : 0);
            var x1 = Math.Min(w, X(s.End)) - Gap / 2;
            if (x1 - x0 < 1) continue;
            var rect = new Rect(x0, 0, x1 - x0, h);
            context.DrawRectangle(i == now ? _theme.BlockNow : _theme.Block, null, rect, 8, 8);
            var label = Label(s.Chord.Name(a.Flats), i == now);
            if (label.Width + 8 <= rect.Width)
                context.DrawText(label, new Point(x0 + Math.Max(4, Math.Min(10, (rect.Width - label.Width) / 2)), (h - label.Height) / 2));
        }
        var px = X(Position);
        context.DrawLine(_theme.Playhead, new Point(px, -2), new Point(px, h + 2));
    }

    private FormattedText Label(string text, bool now)
    {
        if (_labels.TryGetValue((text, now), out var label)) return label;
        label = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(FontFamily.Default, FontStyle.Normal, now ? FontWeight.Bold : FontWeight.SemiBold), 16, now ? _theme.Text : _theme.Muted);
        if (_labels.Count > 200) _labels.Clear();
        _labels[(text, now)] = label;
        return label;
    }
}
