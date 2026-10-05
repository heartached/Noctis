using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    static ArtistCreditLink()
    {
        IsEnabledProperty.Changed.AddClassHandler<Button>((button, e) =>
        {
            if (e.NewValue is true)
            {
                // Button marks its own press handled, so listen to handled events too.
                button.AddHandler(InputElement.PointerPressedEvent, OnPressed, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);
                button.AddHandler(Button.ClickEvent, OnClick);
            }
            else
            {
                button.RemoveHandler(InputElement.PointerPressedEvent, OnPressed);
                button.RemoveHandler(Button.ClickEvent, OnClick);
            }
        });
    }

    private static void OnPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is Button button)
            PressPoints.GetOrCreateValue(button).Value = e.GetPosition(button);
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
