using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
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

    private async void OnChangeArtistImageClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (sender is not Control control || control.DataContext is not Artist artist) return;
            if (DataContext is not LibraryArtistsViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select Artist Picture",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new FilePickerFileType("Images")
                    {
                        Patterns = new[] { "*.jpg", "*.jpeg", "*.png", "*.webp", "*.bmp", "*.gif" }
                    }
                }
            });

            if (files.Count == 0) return;

            byte[] data;
            try
            {
                await using var stream = await files[0].OpenReadAsync();
                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms);
                data = ms.ToArray();
            }
            catch
            {
                return;
            }

            if (data.Length == 0) return;
            await vm.ChangeArtistImageAsync(artist, data);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArtistsView] Change artist image failed: {ex.Message}");
        }
    }

    private async void OnSearchArtistImageClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try
        {
            if (sender is not Control control || control.DataContext is not Artist artist) return;
            if (DataContext is LibraryArtistsViewModel vm)
                await vm.SearchArtistImageAsync(artist);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ArtistsView] Artist image search failed: {ex.Message}");
        }
    }

    private void OnRemoveArtistImageClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || control.DataContext is not Artist artist) return;
        if (DataContext is LibraryArtistsViewModel vm)
            vm.RemoveArtistImage(artist);
    }

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_vm != null)
            _vm.ArtistRows.CollectionChanged -= OnArtistRowsChanged;

        _vm = DataContext as LibraryArtistsViewModel;
        if (_vm != null)
            _vm.ArtistRows.CollectionChanged += OnArtistRowsChanged;
    }

    private void OnArtistRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
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
            _vm.ArtistRows.CollectionChanged -= OnArtistRowsChanged;
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
            _vm.ArtistRows.CollectionChanged -= OnArtistRowsChanged;
            _vm.ArtistRows.CollectionChanged += OnArtistRowsChanged;
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
