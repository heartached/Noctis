using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;

namespace Noctis.Helpers;

/// <summary>
/// Makes an artist-link Button open the ONE credited artist under the pointer instead of
/// the whole credit. Set <c>helpers:ArtistCreditLink.IsEnabled="True"</c> on a Button whose
/// content is the credit's TextBlock and whose Command takes an artist name: a click on
/// "Drake" in "Rihanna, Drake" runs the Command with "Drake". A single-name credit, a click
/// on a separator, or a keyboard press keep the Button's own CommandParameter. Same
/// hit-testing as the player island (<see cref="ArtistCreditSpans"/>). Discord (Luwi,
/// 2026-10-03): individual artists in Songs/album rows could not be clicked.
/// </summary>
public static class ArtistCreditLink
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<Button, bool>("IsEnabled", typeof(ArtistCreditLink));

    public static bool GetIsEnabled(Button button) => button.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(Button button, bool value) => button.SetValue(IsEnabledProperty, value);

    /// <summary>Where the current press landed, per button (null = no pointer press, e.g. keyboard).</summary>
    private static readonly ConditionalWeakTable<Button, StrongBox<Point?>> PressPoints = new();

    /// <summary>Where the pointer is over the button and which hover box is painted, per button.</summary>
    private sealed class HoverState
    {
        public Point? Pointer;
        public (int Start, bool Pressed, Size Size)? Painted;
    }

    private static readonly ConditionalWeakTable<Button, HoverState> HoverStates = new();

    static ArtistCreditLink()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            if (e.NewValue is true)
            {
                // Button marks its own press handled, so listen to handled events too.
                button.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                button.AddHandler(InputElement.PointerMovedEvent, OnPointerMoved, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                button.AddHandler(InputElement.PointerExitedEvent, OnPointerExited, RoutingStrategies.Direct, handledEventsToo: true);
                button.AddHandler(Button.ClickEvent, OnClick);
                button.PropertyChanged += OnButtonPropertyChanged;
                button.DataContextChanged += OnDataContextChanged;
                button.DetachedFromVisualTree += OnDetached;
                button.Classes.Add("artist-credit-link");
            }
            else
            {
                button.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
                button.RemoveHandler(InputElement.PointerMovedEvent, OnPointerMoved);
                button.RemoveHandler(InputElement.PointerExitedEvent, OnPointerExited);
                button.RemoveHandler(Button.ClickEvent, OnClick);
                button.PropertyChanged -= OnButtonPropertyChanged;
                button.DataContextChanged -= OnDataContextChanged;
                button.DetachedFromVisualTree -= OnDetached;
                button.Classes.Remove("artist-credit-link");
                ClearHover(button);
            }
        });
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Button button)
            PressPoints.GetOrCreateValue(button).Value = e.GetPosition(button);
    }

    // ── Per-name hover box (Discord, Luwi 2026-10-10): Fluent painted its hover/press fill on
    // the whole presenter, so "Bonobo, Jordan Rakei" lit up as one box and it was unclear which
    // artist a click would open. The same fill is now painted behind the hovered name only. ──

    private static void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not Button button) return;
        HoverStates.GetOrCreateValue(button).Pointer = e.GetPosition(button);
        UpdateHover(button);
    }

    private static void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (sender is Button button) ClearHover(button);
    }

    private static void OnButtonPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is Button button && (e.Property == Button.IsPressedProperty || e.Property == Visual.BoundsProperty))
            UpdateHover(button);
    }

    // A recycled row keeps the pointer but shows another credit: drop the old box.
    private static void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (sender is Button button) ClearHover(button);
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button) ClearHover(button);
    }

    private static void ClearHover(Button button)
    {
        if (!HoverStates.TryGetValue(button, out var state)) return;
        state.Pointer = null;
        state.Painted = null;
        Presenter(button)?.ClearValue(ContentPresenter.BackgroundProperty);
    }

    private static void UpdateHover(Button button)
    {
        if (!HoverStates.TryGetValue(button, out var state) || state.Pointer is not { } pointer) return;
        var presenter = Presenter(button);
        if (presenter == null) return;

        var area = NameAreaAt(button, pointer, presenter);
        var size = presenter.Bounds.Size;
        if (area is not { } rect)
        {
            // Over a separator or past the text: no box at all, not the whole credit.
            presenter.Background = Brushes.Transparent;
            state.Painted = null;
            return;
        }

        var key = ((int)Math.Round(rect.X), button.IsPressed, size);
        if (state.Painted == key) return;
        state.Painted = key;

        var resourceKey = button.IsPressed ? "ButtonBackgroundPressed" : "ButtonBackgroundPointerOver";
        var fill = presenter.TryFindResource(resourceKey, presenter.ActualThemeVariant, out var res) && res is IBrush b
            ? b
            : new SolidColorBrush(Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF));
        var radius = presenter.CornerRadius.TopLeft;
        var full = new Rect(size);
        presenter.Background = new DrawingBrush
        {
            // The transparent full-size rect pins the drawing's bounds to the presenter, so with
            // Stretch None the name box lands at its own coordinates.
            Drawing = new DrawingGroup
            {
                Children =
                {
                    new GeometryDrawing { Brush = Brushes.Transparent, Geometry = new RectangleGeometry(full) },
                    new GeometryDrawing { Brush = fill, Geometry = new RectangleGeometry(rect) { RadiusX = radius, RadiusY = radius } },
                },
            },
            Stretch = Stretch.None,
            AlignmentX = AlignmentX.Left,
            AlignmentY = AlignmentY.Top,
            SourceRect = new RelativeRect(full, RelativeUnit.Absolute),
            DestinationRect = new RelativeRect(full, RelativeUnit.Absolute),
        };
    }

    private static ContentPresenter? Presenter(Button button) =>
        button.GetVisualDescendants().OfType<ContentPresenter>().FirstOrDefault(p => p.Name == "PART_ContentPresenter");

    /// <summary>The box (in <paramref name="presenter"/> coordinates, full presenter height) of
    /// the credited name under <paramref name="buttonPoint"/>, or null on a separator / outside
    /// the visible text. A single-name credit boxes that name.</summary>
    internal static Rect? NameAreaAt(Button button, Point buttonPoint, Visual presenter)
    {
        var text = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        if (text == null) return null;
        var credit = text is HighlightTextBlock h ? h.DisplayText : text.Text;
        var spans = ArtistCreditSpans.Locate(credit);
        if (spans.Count == 0) return null;

        var p = button.TranslatePoint(buttonPoint, text);
        if (p is not { } local || local.X < 0 || local.Y < 0 || local.X > text.Bounds.Width || local.Y > text.Bounds.Height)
            return null;
        var index = text.TextLayout.HitTestPoint(local).TextPosition;
        ArtistCreditSpans.Span? hit = spans.Count == 1 ? spans[0] : null;
        foreach (var span in spans)
        {
            if (index >= span.Start && index < span.Start + span.Length) { hit = span; break; }
        }
        if (hit is not { } s) return null;

        // Clamp to the visible text: a trimmed credit's hidden tail has no box ("Bonobo, Jor…").
        var left = Math.Clamp(text.TextLayout.HitTestTextPosition(s.Start).X, 0, text.Bounds.Width);
        var right = Math.Clamp(text.TextLayout.HitTestTextPosition(s.Start + s.Length).X, 0, text.Bounds.Width);
        if (right - left < 1) return null;
        var topLeft = text.TranslatePoint(new Point(left, 0), presenter);
        if (topLeft is not { } tl) return null;
        return new Rect(tl.X, 0, right - left, presenter.Bounds.Height);
    }

    private static void OnClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        var box = PressPoints.GetOrCreateValue(button);
        var point = box.Value;
        box.Value = null;
        if (point is not { } p) return;

        var name = NameAt(button, p);
        if (name == null || button.Command is not { } command || !command.CanExecute(name)) return;

        // Handled before the Button runs its own Command with the whole credit.
        e.Handled = true;
        command.Execute(name);
    }

    /// <summary>The credited name at <paramref name="buttonPoint"/>, or null when the credit has
    /// a single name or the point is on a separator / outside the text.</summary>
    internal static string? NameAt(Button button, Point buttonPoint)
    {
        var text = button.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault();
        if (text == null) return null;
        var credit = text is HighlightTextBlock h ? h.DisplayText : text.Text;
        if (ArtistCreditSpans.Locate(credit).Count < 2) return null;

        var p = button.TranslatePoint(buttonPoint, text);
        if (p is not { } local || local.X < 0 || local.Y < 0 || local.X > text.Bounds.Width || local.Y > text.Bounds.Height)
            return null;
        var hit = text.TextLayout.HitTestPoint(local);
        return ArtistCreditSpans.NameAt(credit, hit.TextPosition);
    }
}
