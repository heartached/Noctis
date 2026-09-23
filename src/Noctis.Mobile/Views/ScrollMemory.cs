using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Noctis.Mobile.ViewModels;

namespace Noctis.Mobile.Views;

/// <summary>
/// Keeps a pushed page's scroll position on its <see cref="MobilePage"/>. The shell shows one
/// page at a time through a ContentControl, so going Back re-creates the previous page's
/// view; without this, Back from an album to a 10,000-song list lands at the top.
/// </summary>
public static class ScrollMemory
{
    public static readonly AttachedProperty<bool> IsEnabledProperty =
        AvaloniaProperty.RegisterAttached<ScrollViewer, bool>("IsEnabled", typeof(ScrollMemory));

    public static bool GetIsEnabled(ScrollViewer element) => element.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(ScrollViewer element, bool value) => element.SetValue(IsEnabledProperty, value);

    // Viewers with a restore in flight: the fresh view's first ScrollChanged reports offset 0,
    // which must not overwrite the page's saved offset before the restore lands. UI thread only.
    private static readonly HashSet<ScrollViewer> Restoring = new();

    static ScrollMemory()
    {
        IsEnabledProperty.Changed.AddClassHandler<ScrollViewer>((viewer, args) =>
        {
            viewer.ScrollChanged -= OnScrollChanged;
            viewer.AttachedToVisualTree -= OnAttached;
            if (!args.GetNewValue<bool>()) return;
            viewer.ScrollChanged += OnScrollChanged;
            viewer.AttachedToVisualTree += OnAttached;
        });
    }

    private static void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (sender is not ScrollViewer viewer || viewer.DataContext is not MobilePage page) return;
        var target = page.ScrollOffset;
        if (target == default) return;
        Restoring.Add(viewer);
        // After layout: the extent must be measured before an offset past one screen sticks.
        Dispatcher.UIThread.Post(() => Apply(viewer, page, target, retry: true), DispatcherPriority.Loaded);
    }

    private static void Apply(ScrollViewer viewer, MobilePage page, Vector target, bool retry)
    {
        viewer.Offset = target;
        // A virtualising panel's first extent is an estimate; if it clamped the offset, try
        // once more after the next layout pass has measured more rows.
        if (retry && viewer.Offset.Y < target.Y - 1)
        {
            Dispatcher.UIThread.Post(() => Apply(viewer, page, target, retry: false), DispatcherPriority.Loaded);
            return;
        }
        Restoring.Remove(viewer);
        page.ScrollOffset = viewer.Offset;
    }

    private static void OnScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (sender is not ScrollViewer viewer || Restoring.Contains(viewer)) return;
        if (viewer.DataContext is MobilePage page) page.ScrollOffset = viewer.Offset;
    }
}
