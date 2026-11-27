using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Noctis.Mobile.Views;

/// <summary>
/// A pressed button dims as one picture. Every press style on the phone dims the button through
/// its Opacity (MobileStyles' rows, tiles, pills and round buttons, and the pages' own), and
/// Avalonia's Skia backend applies Opacity to each element drawn inside it, not to the whole:
/// an artist circle's photo at 60 % let the initials under it show through ("Bs" through Blake
/// Shelton, beta 6), an album tile's cover its placeholder glyph. Avalonia draws a visual's
/// Opacity as a layer only when its PARENT asks for full opacity handling (measured, headless
/// Skia: the setting on the button itself changes nothing, on its parent it groups the button,
/// and every nested Opacity below it groups too). So while a button is pressed, and until its
/// Opacity has eased back after the release, its parent asks for it; the parent's own setting is
/// put back after. Only then: a layer per faded element costs a GPU pass, and lists hold many
/// placeholder glyphs at reduced opacity.
/// </summary>
public static class PressOpacity
{
    private static bool _installed;

    /// <summary>Each pressed (or still easing back) button, and the parent asked for it.</summary>
    private static readonly Dictionary<Button, Visual> Engaged = new();

    /// <summary>Each parent asked for, by how many buttons, and its own setting to put back.</summary>
    private static readonly Dictionary<Visual, (int Count, bool? Own)> Parents = new();

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        Button.IsPressedProperty.Changed.AddClassHandler<Button>(OnPressedChanged);
        Visual.OpacityProperty.Changed.AddClassHandler<Button>(OnOpacityChanged);
    }

    /// <summary>Tests: whether <paramref name="button"/>'s parent is asked to group it right now.</summary>
    internal static bool IsGrouped(Button button) => Engaged.ContainsKey(button);

    private static void OnPressedChanged(Button button, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.GetNewValue<bool>())
        {
            if (Engaged.ContainsKey(button) || button.GetVisualParent() is not { } parent) return;
            Engaged[button] = parent;
            button.DetachedFromVisualTree += OnDetached;
            var (count, own) = Parents.TryGetValue(parent, out var held) ? held : (0, RenderOptions.GetRequiresFullOpacityHandling(parent));
            Parents[parent] = (count + 1, own);
            if (count == 0) RenderOptions.SetRequiresFullOpacityHandling(parent, true);
        }
        else if (AtRest(button))
            Release(button);   // no dim to ease back from (Previous / Play / Next keep full opacity)
    }

    private static void OnOpacityChanged(Button button, AvaloniaPropertyChangedEventArgs e)
    {
        if (!button.IsPressed && Engaged.ContainsKey(button) && AtRest(button))
            Release(button);
    }

    /// <summary>The Opacity is where the styles put it, no transition still easing it there.</summary>
    private static bool AtRest(Button button)
    {
        var target = button.GetBaseValue(Visual.OpacityProperty);
        return button.Opacity == (target.HasValue ? target.Value : Visual.OpacityProperty.GetDefaultValue(button.GetType()));
    }

    private static void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is Button button) Release(button);
    }

    private static void Release(Button button)
    {
        if (!Engaged.Remove(button, out var parent)) return;
        button.DetachedFromVisualTree -= OnDetached;
        if (!Parents.TryGetValue(parent, out var held)) return;
        if (held.Count > 1)
        {
            Parents[parent] = (held.Count - 1, held.Own);
            return;
        }
        Parents.Remove(parent);
        RenderOptions.SetRequiresFullOpacityHandling(parent, held.Own);
    }
}
