using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LibraryArtistsView : UserControl
{
    private LibraryArtistsViewModel? _vm;
    private EventHandler? _pendingScrollRestore;

    public LibraryArtistsView()
    {
        InitializeComponent();

        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.FlatArtistList.CollectionChanged -= OnFlatArtistListChanged;

        _vm = DataContext as LibraryArtistsViewModel;
        if (_vm != null)
            _vm.FlatArtistList.CollectionChanged += OnFlatArtistListChanged;
    }

    private void OnFlatArtistListChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        // Scroll to top when rows change due to an active filter (search text)
        // BUT skip if a scroll restore is pending (returning from artist detail)
        if (_vm?.HasActiveFilter != true)
            return;
        if (_pendingScrollRestore != null || (_vm != null && _vm.SavedScrollOffset > 0))
            return;

        Dispatcher.UIThread.Post(() =>
        {
            var sv = ArtistListBox.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                sv.Offset = new Vector(0, 0);
        }, DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        CancelPendingScrollRestore();

        if (DataContext is LibraryArtistsViewModel vm)
        {
            var sv = ArtistListBox.FindDescendantOfType<ScrollViewer>();
            if (sv != null)
                vm.SavedScrollOffset = sv.Offset.Y;
        }
        if (_vm != null)
            _vm.FlatArtistList.CollectionChanged -= OnFlatArtistListChanged;
        base.OnDetachedFromVisualTree(e);
    }

    private void CancelPendingScrollRestore()
    {
        if (_pendingScrollRestore != null)
        {
            ArtistListBox.LayoutUpdated -= _pendingScrollRestore;
            _pendingScrollRestore = null;
            ArtistListBox.Opacity = 1;
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // Re-subscribe to collection changes (unsubscribed in OnDetachedFromVisualTree)
        if (_vm != null)
        {
            _vm.FlatArtistList.CollectionChanged -= OnFlatArtistListChanged;
            _vm.FlatArtistList.CollectionChanged += OnFlatArtistListChanged;
        }

        if (DataContext is LibraryArtistsViewModel vm && vm.SavedScrollOffset > 0)
        {
            // Hide ListBox until scroll is restored to prevent flash-at-top flicker
            ArtistListBox.Opacity = 0;
            var targetOffset = vm.SavedScrollOffset;
            var attempts = 0;

            _pendingScrollRestore = (s, args) =>
            {
                attempts++;
                var sv = ArtistListBox.FindDescendantOfType<ScrollViewer>();
                if (sv == null) return;

                // Wait until the ScrollViewer extent is tall enough, with safety limit
                if (sv.Extent.Height < targetOffset && attempts < 10)
                    return;

                // Clamp to actual extent if content shrank since last visit
                var clampedOffset = Math.Min(targetOffset, Math.Max(0, sv.Extent.Height - sv.Viewport.Height));
                sv.Offset = new Vector(0, clampedOffset);
                ArtistListBox.Opacity = 1;
                CancelPendingScrollRestore();
            };

            ArtistListBox.LayoutUpdated += _pendingScrollRestore;
        }
    }
}
