using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Noctis.Services;

namespace Noctis.Controls;

/// <summary>
/// An Image control that loads artwork asynchronously using the shared LRU cache.
/// Cache hits are instant (no UI thread blocking). Cache misses load on a
/// background thread, preventing scroll stutter in virtualized lists.
/// A generation counter discards stale results when the control is recycled.
/// </summary>
public class CachedImage : Image
{
    public static readonly StyledProperty<string?> SourcePathProperty =
        AvaloniaProperty.Register<CachedImage, string?>(nameof(SourcePath));

    public string? SourcePath
    {
        get => GetValue(SourcePathProperty);
        set => SetValue(SourcePathProperty, value);
    }

    public static readonly StyledProperty<int> DecodeWidthProperty =
        AvaloniaProperty.Register<CachedImage, int>(nameof(DecodeWidth), 512);

    public int DecodeWidth
    {
        get => GetValue(DecodeWidthProperty);
        set => SetValue(DecodeWidthProperty, value);
    }

    public static readonly StyledProperty<bool> ClearOnSourceChangeProperty =
        AvaloniaProperty.Register<CachedImage, bool>(nameof(ClearOnSourceChange), defaultValue: true);

    /// <summary>
    /// Whether to blank the image while a new source is decoding (default true).
    ///
    /// True is correct in virtualized lists: a recycled container must not keep showing
    /// the previous item's cover. Set it to false on single-item surfaces — the player
    /// bar, the lyrics page — where holding the old cover across a track change avoids a
    /// visible placeholder flash.
    /// </summary>
    public bool ClearOnSourceChange
    {
        get => GetValue(ClearOnSourceChangeProperty);
        set => SetValue(ClearOnSourceChangeProperty, value);
    }

    private int _loadGeneration;
    private readonly object _generationLock = new();

    static CachedImage()
    {
        SourcePathProperty.Changed.AddClassHandler<CachedImage>((img, _) => img.OnSourcePathChanged());
        DecodeWidthProperty.Changed.AddClassHandler<CachedImage>((img, _) => img.OnSourcePathChanged());
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        ArtworkCache.Invalidated += OnArtworkInvalidated;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        ArtworkCache.Invalidated -= OnArtworkInvalidated;
        base.OnDetachedFromVisualTree(e);
    }

    private void OnArtworkInvalidated(string path)
    {
        // Invalidated can be raised from any thread; SourcePath is an
        // AvaloniaProperty and may only be read on the UI thread, so the
        // comparison has to happen inside the post, not before it.
        Dispatcher.UIThread.Post(() =>
        {
            if (string.Equals(SourcePath, path, StringComparison.Ordinal))
                OnSourcePathChanged();
        });
    }

    private async void OnSourcePathChanged()
    {
        var path = SourcePath;
        int generation;
        lock (_generationLock)
        {
            generation = ++_loadGeneration;
        }

        if (string.IsNullOrEmpty(path))
        {
            Source = null;
            return;
        }

        var decodeWidth = DecodeWidth;

        // Fast path: cache hit returns immediately, no I/O
        var cached = ArtworkCache.TryGet(path, decodeWidth);
        if (cached != null)
        {
            Source = cached;
            return;
        }

        // Slow path: load on background thread to avoid blocking UI.
        //
        // Keeping the previous Source visible avoids a placeholder flash on every track
        // switch — correct for the player's single cover, wrong for a virtualized list,
        // where a recycled container gets a new SourcePath and keeps rendering the
        // PREVIOUS item's art until the decode lands. Scrolling a large grid past the
        // cache budget therefore showed a trail of wrong covers.
        if (ClearOnSourceChange)
            Source = null;

        try
        {
            var bitmap = await Task.Run(() => ArtworkCache.LoadAndCache(path, decodeWidth));

            // Discard result if the control was recycled (SourcePath changed again)
            bool isCurrentGeneration;
            lock (_generationLock)
            {
                isCurrentGeneration = generation == _loadGeneration;
            }

            if (isCurrentGeneration)
                Source = bitmap;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CachedImage] Failed to load artwork '{path}': {ex.Message}");
        }
    }
}
