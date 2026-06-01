using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace Noctis.Helpers;

/// <summary>
/// Forwards a tunneling <see cref="InputElement.KeyDownEvent"/> from the window (TopLevel)
/// to a page view's handler while that view is attached to the visual tree.
///
/// Page shortcuts like Ctrl+A were previously registered on the view itself, so they only
/// fired once keyboard focus was already inside the view (i.e. after the user clicked an
/// item). Listening at the TopLevel makes the shortcut work as soon as the page is shown,
/// without a priming click. The handler is added on attach and removed on detach, so only
/// the currently-visible page receives the key.
///
/// Text editing is respected: when a <see cref="TextBox"/> is focused (e.g. a search box),
/// the key is left alone so Ctrl+A still selects text instead of being hijacked.
/// </summary>
public sealed class WindowKeyForwarder
{
    private readonly Control _owner;
    private readonly EventHandler<KeyEventArgs> _handler;
    private TopLevel? _topLevel;

    public WindowKeyForwarder(Control owner, EventHandler<KeyEventArgs> handler)
    {
        _owner = owner;
        _handler = handler;
        _owner.AttachedToVisualTree += OnAttached;
        _owner.DetachedFromVisualTree += OnDetached;
    }

    private void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel = TopLevel.GetTopLevel(_owner);
        _topLevel?.AddHandler(InputElement.KeyDownEvent, OnKeyDown,
            RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    private void OnDetached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _topLevel?.RemoveHandler(InputElement.KeyDownEvent, OnKeyDown);
        _topLevel = null;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Don't steal Ctrl+A (etc.) while the user is editing text in a box.
        if (e.Source is TextBox) return;
        _handler(sender, e);
    }
}
