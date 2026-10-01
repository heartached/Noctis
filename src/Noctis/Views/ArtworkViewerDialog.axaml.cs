using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Services;

namespace Noctis.Views;

/// <summary>
/// GitHub #114: the album page's cover, full size, over the dimmed window. The cover is
/// fitted inside the window and decoded at exactly the device pixels it is drawn at, from
/// the original artwork file (the header shows a 512px cached decode). The bitmap is this
/// window's own, not the shared artwork cache's, and is disposed when the window closes.
/// </summary>
public partial class ArtworkViewerDialog : Window
{
    /// <summary>Space kept clear on every side of the cover, DIPs (clears the X in the corner).</summary>
    internal const double EdgeMargin = 56;
    /// <summary>Room under the cover for the size caption, DIPs.</summary>
    internal const double CaptionSpace = 28;
    /// <summary>A click this soon after opening is the second half of a double-click on the
    /// page's cover, not a request to close.</summary>
    private const int DismissGuardMs = 300;

    private readonly string? _path;
    private readonly PixelSize? _sourceSize;
    private readonly Stopwatch _sinceOpened = new();
    private bool _closing;
    private bool _closed;

    /// <summary>The sharp decode on screen; null until it lands and after the window closes.</summary>
    internal Bitmap? FullBitmap { get; private set; }

    /// <summary>Device-pixel width the sharp decode was asked for (the drawn width); 0 before open.</summary>
    internal int FullDecodeWidth { get; private set; }

    public ArtworkViewerDialog()
    {
        InitializeComponent();
    }

    public ArtworkViewerDialog(string path) : this()
    {
        _path = path;
        _sourceSize = SkiaArtworkDecoder.ReadFilePixelSize(path); // header only
        SizeText.Text = _sourceSize is { } s ? $"{s.Width} × {s.Height}" : string.Empty;
        PreviewImage.SourcePath = path;
    }

    /// <summary>
    /// The cover's size on screen, in device pixels: its aspect fitted inside the window less
    /// <see cref="EdgeMargin"/> and <see cref="CaptionSpace"/>, enlarged when the cover is
    /// smaller than that. Empty when there is no room or no cover size.
    /// </summary>
    internal static PixelSize FitArtwork(PixelSize source, Size window, double scaling)
    {
        var availW = (window.Width - 2 * EdgeMargin) * scaling;
        var availH = (window.Height - 2 * EdgeMargin - CaptionSpace) * scaling;
        if (source.Width <= 0 || source.Height <= 0 || availW < 1 || availH < 1) return default;

        // Multiply before dividing so an exact fit stays exact (3000×2000 into 888 wide is 592 tall).
        var w = availW;
        var h = availW * source.Height / source.Width;
        if (h > availH)
        {
            h = availH;
            w = availH * source.Width / source.Height;
        }
        return new PixelSize(Math.Max(1, (int)Math.Floor(w + 1e-6)), Math.Max(1, (int)Math.Floor(h + 1e-6)));
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        _sinceOpened.Start();

        if (_path != null)
        {
            var scaling = RenderScaling;
            var fit = FitArtwork(_sourceSize ?? new PixelSize(1, 1), ClientSize, scaling);
            if (fit.Width > 0)
            {
                // Whole device pixels, so the decode maps 1:1 onto the screen.
                ArtworkFrame.Width = fit.Width / scaling;
                ArtworkFrame.Height = fit.Height / scaling;
                if (_sourceSize != null)
                {
                    FullDecodeWidth = fit.Width;
                    _ = LoadFullAsync(_path, fit.Width);
                }
            }
        }

        // Settle to the open state on the next frame so the fade/scale transitions animate
        // it (same pattern as the album description dialog).
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            ArtworkCard.RenderTransform = TransformOperations.Parse("scale(1)");
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Decodes the cover at <paramref name="width"/> device pixels (never above its own
    /// width: a small cover is enlarged by the Image instead) on a worker.</summary>
    private async Task LoadFullAsync(string path, int width)
    {
        Bitmap? bitmap = null;
        try
        {
            bitmap = await Task.Run(() =>
            {
                using var decoded = SkiaArtworkDecoder.DecodeToWidth(path, width);
                return decoded is null ? null : SkiaArtworkDecoder.ToAvaloniaBitmap(decoded);
            });
        }
        catch (Exception ex)
        {
            DebugLog.Write("ArtworkViewer", ex);
        }
        if (bitmap is null) return;
        if (_closed)
        {
            bitmap.Dispose();
            return;
        }
        FullBitmap = bitmap;
        FullImage.Source = bitmap;
        PreviewImage.IsVisible = false;
    }

    private async Task CloseAnimatedAsync()
    {
        if (_closing) return;
        _closing = true;
        DialogOverlay.Opacity = 0;
        ArtworkCard.RenderTransform = TransformOperations.Parse("scale(0.96)");
        await Task.Delay(200);
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Full-size covers run to tens of MB: let go now rather than at the next collection.
        _closed = true;
        FullImage.Source = null;
        FullBitmap?.Dispose();
        FullBitmap = null;
        PreviewImage.SourcePath = null;
        base.OnClosed(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            _ = CloseAnimatedAsync();
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _ = CloseAnimatedAsync();
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Light-dismiss: a click anywhere, on the cover too, closes the viewer.
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (_sinceOpened.ElapsedMilliseconds < DismissGuardMs) return;
        _ = CloseAnimatedAsync();
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    /// <summary>Shows <paramref name="path"/> full size over the main window until it is closed.</summary>
    public static async Task ShowAsync(string path)
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            var dialog = new ArtworkViewerDialog(path);
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}
