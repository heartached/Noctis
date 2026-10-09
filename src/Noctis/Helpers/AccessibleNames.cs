using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;

namespace Noctis.Helpers;

/// <summary>
/// Gives every icon-only button the accessible name its tooltip already shows. With no
/// AutomationProperties.Name, Avalonia's button peer falls back to its content's
/// ToString(), so screen readers and UI Automation read the island's transport, the top
/// bar's corner icons and every "…" as "Avalonia.Controls.PathIcon" and the like (UIA
/// measured live 10-09). One class handler covers every Button (and ToggleButton,
/// RepeatButton) in the app, including ones templated in later.
/// </summary>
/// <remarks>
/// The name is written at Style priority, so an explicit AutomationProperties.Name in XAML
/// always wins, and it follows the tooltip: a state tooltip (Play/Pause) and a language
/// switch re-name the button. Only string tooltips count (a TextBlock tooltip's runs bind
/// later; those buttons name themselves), and a button whose content is already plain text
/// keeps that text as its name, which is what its peer reads.
/// </remarks>
public static class AccessibleNames
{
    private static readonly ConditionalWeakTable<Button, IDisposable> Mirrored = new();
    private static bool _installed;

    public static void Install()
    {
        if (_installed) return;
        _installed = true;
        ToolTip.TipProperty.Changed.AddClassHandler<Button>(static (button, _) => Sync(button));
        ContentControl.ContentProperty.Changed.AddClassHandler<Button>(static (button, _) => Sync(button));
    }

    /// <summary>The name a button takes from its tooltip, or null when it has its own
    /// (plain-text content) or the tooltip carries no plain text.</summary>
    public static string? FromTip(Button button)
        => button.Content is string ? null
            : ToolTip.GetTip(button) is string tip && !string.IsNullOrWhiteSpace(tip) ? tip
            : null;

    private static void Sync(Button button)
    {
        if (Mirrored.TryGetValue(button, out var previous))
        {
            Mirrored.Remove(button);
            previous.Dispose();
        }
        if (FromTip(button) is not { } name) return;
        if (button.SetValue(AutomationProperties.NameProperty, name, BindingPriority.Style) is { } handle)
            Mirrored.Add(button, handle);
    }
}
