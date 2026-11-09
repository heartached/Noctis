using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace Noctis.Helpers;

/// <summary>
/// GitHub #207: whether the queue panel's keys (Ctrl+A, Delete, Escape) and its selection
/// (GitHub #206: the ReplayGain shortcut) are what the keyboard addresses.
///
/// Keyboard focus alone said so before, and focus leaves the panel more often than it looks:
/// a click on its header (plain text, nothing focusable) hands focus to the window's root
/// panel, and a click on the player island hands it to the island's button. So a press
/// anywhere in the panel arms it; a press on the island leaves that as it is; a press
/// anywhere else in the window (the page, the sidebar) disarms it. Focus moved into the page
/// with the keyboard wins over an armed panel, so Tab into a list and Ctrl+A is the list's.
/// </summary>
public sealed class QueueKeyFocus
{
    private readonly TopLevel _window;
    private readonly Func<Visual?> _panel;
    private readonly Func<Visual?> _page;
    private readonly Func<Visual, bool> _isIsland;

    public QueueKeyFocus(TopLevel window, Func<Visual?> panel, Func<Visual?> page, Func<Visual, bool> isIsland)
    {
        _window = window;
        _panel = panel;
        _page = page;
        _isIsland = isIsland;
        // Tunnel, handled too: a row's own press handler marks Ctrl/Shift presses handled.
        window.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    /// <summary>The last press was in the panel (or on the island since).</summary>
    public bool IsArmed { get; private set; }

    /// <summary>The panel is shown.</summary>
    public bool IsPanelShown => _panel() is { IsVisible: true };

    /// <summary>Keyboard focus is inside the shown panel.</summary>
    public bool HasFocus => IsPanelShown && IsFocusWithin(_panel());

    /// <summary>
    /// The queue takes the keys: focus is in it, or it was clicked last and focus has not
    /// gone into the page since, or (<paramref name="pageHasNothingToSelect"/>: the Lyrics
    /// and Visualizer pages) the page underneath has no selection of its own.
    /// </summary>
    public bool IsKeyTarget(bool pageHasNothingToSelect = false) =>
        IsPanelShown && (HasFocus || (IsArmed && !IsFocusWithin(_page())) || pageHasNothingToSelect);

    /// <summary>The panel closed: the next open starts unarmed.</summary>
    public void Disarm() => IsArmed = false;

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.Source is not Visual source) return;
        if (_panel() is { } panel && (source == panel || panel.IsVisualAncestorOf(source))) IsArmed = true;
        else if (!_isIsland(source)) IsArmed = false;
    }

    private bool IsFocusWithin(Visual? container) =>
        container != null
        && _window.FocusManager?.GetFocusedElement() is Visual focused
        && (focused == container || container.IsVisualAncestorOf(focused));
}
