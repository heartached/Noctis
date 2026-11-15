using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Noctis.Controls;

/// <summary>
/// The main window's page area: shows the view for <see cref="Page"/> (the current view model).
/// A ContentControl took the previous page out of the tree on every switch, and leaving the
/// logical tree drops every style of the page, so coming back to a cached section restyled the
/// whole page (11-15, PageScrollBench: Songs 68-169 ms, Home 91-208 ms on a second visit).
///
/// Views from <see cref="CachedViewLocator"/> (the root sections, one per view model type)
/// stay logical children here while another page is shown, but leave the visual tree. Out of
/// the visual tree a page is not laid out, not rendered, not focusable and not in the
/// automation tree, and everything the views do on AttachedToVisualTree / DetachedFromVisualTree
/// / Loaded / Unloaded (scroll save and restore, timers, cover release) runs on each switch as
/// before; only their styles and templates are kept. A kept page is also IsVisible=false while
/// hidden, which pauses its style animations. At most <see cref="MaxKept"/> of them are
/// kept (least recently shown dropped first; a dropped one is restyled on its next visit, as
/// before).
///
/// Other views (album, artist, playlist pages) are built per view model and leave entirely
/// when another page is shown, as with the ContentControl, which also re-used the shown view
/// for a next view model of the same template; so does this.
/// </summary>
public sealed class PageDeck : Control
{
    public static readonly StyledProperty<object?> PageProperty =
        AvaloniaProperty.Register<PageDeck, object?>(nameof(Page));

    /// <summary>Cached section views kept styled while hidden (the shown one included).</summary>
    public const int MaxKept = 8;

    /// <summary>Off: every page leaves the tree when another shows, as with the ContentControl
    /// (the benchmark's "before"). Internal for tests.</summary>
    internal static bool KeepSections = true;

    public object? Page { get => GetValue(PageProperty); set => SetValue(PageProperty, value); }

    private readonly List<Control> _kept = new(); // least recently shown first
    private Control? _shown;
    private IDataTemplate? _shownTemplate;

    /// <summary>The view on screen.</summary>
    public Control? ShownView => _shown;

    /// <summary>Cached section views held (shown or hidden), least recently shown first.</summary>
    public IReadOnlyList<Control> KeptViews => _kept;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == PageProperty)
            Show(change.NewValue);
    }

    private void Show(object? page)
    {
        Control? view = null;
        IDataTemplate? template = null;
        if (page is Control control)
        {
            view = control;
        }
        else if (page != null)
        {
            template = this.FindDataTemplate(page) ?? FuncDataTemplate.Default;
            if (template is not CachedViewLocator && template is IRecyclingDataTemplate recycling
                && ReferenceEquals(template, _shownTemplate) && _shown != null && !_kept.Contains(_shown))
                view = recycling.Build(page, _shown); // the same template's page: re-used, as the ContentControl did
            else
                view = template.Build(page);
        }

        var old = _shown;
        if (old != null && !ReferenceEquals(old, view))
        {
            if (_kept.Contains(old))
            {
                // Hidden as well as out of the visual tree. Detaching makes every control of the
                // page count as effectively visible again (each takes its own IsVisible), so a
                // kept page's running style animations (a spinner's INFINITE rotation, which the
                // window's inherited clock still drives) would go on ticking. IsVisible=false set
                // after the detach reaches the whole detached subtree; Avalonia pauses animations
                // of effectively invisible controls.
                VisualChildren.Remove(old);
                old.SetValue(IsVisibleProperty, false);
            }
            else
            {
                VisualChildren.Remove(old);
                LogicalChildren.Remove(old);
            }
        }

        _shown = view;
        _shownTemplate = template;
        if (view != null)
        {
            // As the ContentControl's presenter did, the page's DataContext is the view model.
            if (page is not Control && !ReferenceEquals(view.DataContext, page))
                view.DataContext = page;
            // A cached view still held by another window's deck (a closed main window) moves here.
            if (view.Parent is PageDeck other && !ReferenceEquals(other, this))
                other.Release(view);
            if (!LogicalChildren.Contains(view))
                LogicalChildren.Add(view);
            view.ClearValue(IsVisibleProperty);
            if (!VisualChildren.Contains(view))
                VisualChildren.Add(view);
            if (template is CachedViewLocator && KeepSections)
                Keep(view);
        }
        InvalidateMeasure();
    }

    private void Release(Control view)
    {
        if (ReferenceEquals(view, _shown))
        {
            _shown = null;
            _shownTemplate = null;
        }
        _kept.Remove(view);
        VisualChildren.Remove(view);
        LogicalChildren.Remove(view);
        view.ClearValue(IsVisibleProperty);
    }

    private void Keep(Control view)
    {
        _kept.Remove(view);
        _kept.Add(view);
        while (_kept.Count > MaxKept)
        {
            var dropped = _kept[0];
            _kept.RemoveAt(0);
            if (!ReferenceEquals(dropped, _shown))
            {
                LogicalChildren.Remove(dropped);
                dropped.ClearValue(IsVisibleProperty);
            }
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_shown == null) return default;
        _shown.Measure(availableSize);
        return _shown.DesiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        _shown?.Arrange(new Rect(finalSize));
        return finalSize;
    }
}
