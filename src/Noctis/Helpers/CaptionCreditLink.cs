using System.Runtime.CompilerServices;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Makes the artist credit at the start of a caption TextBlock ("Bonobo, Jordan Rakei · 2019 ·
/// 12 songs" on an album tile) open the ONE artist under the pointer, without a Button or any
/// extra control per tile: the names are hit-tested on the caption's own text layout, the
/// hover box is painted as the caption's background (the same box as
/// <see cref="ArtistCreditLink"/>), and the rest of the caption stays part of the tile, so a
/// click on the year still opens the album. A press on a name is handled before the tile
/// Button sees it; Ctrl/Shift presses are left to the tile (multi-select).
/// </summary>
public static class CaptionCreditLink
{
    /// <summary>Runs with the artist name clicked. Setting it turns the link on.</summary>
    public static readonly AttachedProperty<ICommand?> CommandProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, ICommand?>("Command", typeof(CaptionCreditLink));

    /// <summary>The credit the caption starts with (Album.ArtistDisplay); only its names link.</summary>
    public static readonly AttachedProperty<string?> CreditProperty =
        AvaloniaProperty.RegisterAttached<TextBlock, string?>("Credit", typeof(CaptionCreditLink));

    public static ICommand? GetCommand(TextBlock text) => text.GetValue(CommandProperty);
    public static void SetCommand(TextBlock text, ICommand? value) => text.SetValue(CommandProperty, value);
    public static string? GetCredit(TextBlock text) => text.GetValue(CreditProperty);
    public static void SetCredit(TextBlock text, string? value) => text.SetValue(CreditProperty, value);

    private const double BoxRadius = 4;

    private sealed class State
    {
        public string? Pressed;
        public (int Left, int Right, bool Pressed, Size Size)? Painted;
    }

    private static readonly ConditionalWeakTable<TextBlock, State> States = new();

    static CaptionCreditLink()
    {
        CommandProperty.Changed.AddClassHandler<TextBlock>((text, e) =>
        {
            var on = e.NewValue is ICommand;
            var was = e.OldValue is ICommand;
            if (on == was) return;
            if (on)
            {
                text.PointerMoved += OnMoved;
                text.PointerExited += OnExited;
                text.PointerPressed += OnPressed;
                text.PointerReleased += OnReleased;
                text.PointerCaptureLost += OnCaptureLost;
                text.DataContextChanged += OnDataContextChanged;
                // A TextBlock is hit only on its glyphs; a transparent fill makes the whole
                // caption take the pointer, so the box follows it between letters too.
                text.Background = Brushes.Transparent;
            }
            else
            {
                text.PointerMoved -= OnMoved;
                text.PointerExited -= OnExited;
                text.PointerPressed -= OnPressed;
                text.PointerReleased -= OnReleased;
                text.PointerCaptureLost -= OnCaptureLost;
                text.DataContextChanged -= OnDataContextChanged;
                Clear(text);
                text.ClearValue(TextBlock.BackgroundProperty);
            }
        });
    }

    /// <summary>The credited name under <paramref name="local"/> (caption coordinates) with
    /// its left/right edges, clamped to the visible text; null off the credit (the year, the
    /// count, a separator, past the text).</summary>
    internal static (string Name, double Left, double Right)? NameAt(TextBlock text, Point local)
    {
        var shown = text is HighlightTextBlock h ? h.DisplayText : text.Text;
        var credit = GetCredit(text);
        if (string.IsNullOrWhiteSpace(shown) || string.IsNullOrWhiteSpace(credit)
            || !shown.StartsWith(credit, StringComparison.Ordinal))
            return null;
        if (local.X < 0 || local.Y < 0 || local.X > text.Bounds.Width || local.Y > text.Bounds.Height)
            return null;

        var index = text.TextLayout.HitTestPoint(local).TextPosition;
        foreach (var span in ArtistCreditSpans.Locate(credit))
        {
            if (index < span.Start || index >= span.Start + span.Length) continue;
            var left = Math.Clamp(text.TextLayout.HitTestTextPosition(span.Start).X, 0, text.Bounds.Width);
            var right = Math.Clamp(text.TextLayout.HitTestTextPosition(span.Start + span.Length).X, 0, text.Bounds.Width);
            // Past the ellipsis: the hidden part of a trimmed credit is not a link.
            if (right - left < 1 || local.X > right) return null;
            return (span.Name, left, right);
        }
        return null;
    }

    private static void OnMoved(object? sender, PointerEventArgs e)
    {
        if (sender is TextBlock text) Update(text, e.GetPosition(text));
    }

    private static void OnExited(object? sender, PointerEventArgs e)
    {
        if (sender is TextBlock text && States.TryGetValue(text, out var s) && s.Pressed == null) Clear(text);
    }

    private static void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock text) Clear(text);
    }

    private static void OnCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        if (sender is TextBlock text && States.TryGetValue(text, out var s) && s.Pressed != null)
        {
            s.Pressed = null;
            Clear(text);
        }
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not TextBlock text) return;
        var point = e.GetCurrentPoint(text);
        if (!point.Properties.IsLeftButtonPressed
            || (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift | KeyModifiers.Meta)) != 0)
            return;
        if (NameAt(text, point.Position) is not { } hit) return;

        // Handled here, so the tile Button underneath never presses (no album open, no drag).
        e.Handled = true;
        States.GetOrCreateValue(text).Pressed = hit.Name;
        e.Pointer.Capture(text);
        Update(text, point.Position);
    }

    private static void OnReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is not TextBlock text || !States.TryGetValue(text, out var s) || s.Pressed is not { } pressed) return;
        s.Pressed = null;
        e.Handled = true;
        e.Pointer.Capture(null);
        var position = e.GetPosition(text);
        var hit = NameAt(text, position);
        Update(text, position);
        if (hit?.Name != pressed || GetCommand(text) is not { } command || !command.CanExecute(pressed)) return;
        command.Execute(pressed);
    }

    private static void Update(TextBlock text, Point position)
    {
        var state = States.GetOrCreateValue(text);
        var hit = NameAt(text, position);
        text.Cursor = hit != null ? HandCursor : null;

        if (hit is not { } h)
        {
            Paint(text, state, null);
            return;
        }
        var pressed = state.Pressed != null;
        Paint(text, state, ((int)Math.Round(h.Left), (int)Math.Round(h.Right), pressed, text.Bounds.Size));
    }

    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);

    private static void Paint(TextBlock text, State state, (int Left, int Right, bool Pressed, Size Size)? key)
    {
        if (Nullable.Equals(state.Painted, key)) return;
        state.Painted = key;
        if (key is not { } k)
        {
            text.Background = Brushes.Transparent;
            return;
        }
        var resourceKey = k.Pressed ? "ButtonBackgroundPressed" : "ButtonBackgroundPointerOver";
        var fill = text.TryFindResource(resourceKey, text.ActualThemeVariant, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        text.Background = ArtistCreditLink.NameBoxBrush(k.Size,
            new Rect(k.Left, 0, k.Right - k.Left, k.Size.Height), fill, BoxRadius);
    }

    private static void Clear(TextBlock text)
    {
        if (!States.TryGetValue(text, out var state)) return;
        state.Pressed = null;
        state.Painted = null;
        text.Background = Brushes.Transparent;
        text.ClearValue(InputElement.CursorProperty);
    }
}
