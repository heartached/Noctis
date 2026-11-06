using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Automation;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.Views;

/// <summary>What the full-size viewer shows: the still cover and, when the album has one
/// and Animated Artwork is on, its animated cover. <paramref name="PlayAnimated"/> is the
/// remembered play/pause choice; <paramref name="PlayAnimatedChanged"/> stores a new one.</summary>
public sealed record ArtworkViewerRequest(
    string StillPath,
    string? AnimatedPath = null,
    bool PlayAnimated = true,
    Action<bool>? PlayAnimatedChanged = null);

/// <summary>
/// GitHub #114: the album page's cover, full size, over the dimmed window. The cover is
/// fitted inside the window and decoded at exactly the device pixels it is drawn at, from
/// the original artwork file (the header's decode is sized for the header). The bitmap is this
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

    /// <summary>How long the animated cover takes to fade out before its decoder lease goes.</summary>
    private const int AnimatedFadeMs = 250;

    private readonly string? _path;
    private readonly PixelSize? _sourceSize;
    private readonly ArtworkViewerRequest? _request;
    private int _pauseToken;
    private readonly Stopwatch _sinceOpened = new();
    private bool _closing;
    private bool _closed;

    /// <summary>The sharp decode on screen; null until it lands and after the window closes.</summary>
    internal Bitmap? FullBitmap { get; private set; }

    /// <summary>Device-pixel width the sharp decode was asked for (the drawn width); 0 before open.</summary>
    internal int FullDecodeWidth { get; private set; }

    /// <summary>The album has an animated cover to offer (the play/pause button shows).</summary>
    internal bool HasAnimatedCover => _request?.AnimatedPath is { Length: > 0 };

    /// <summary>The animated cover is wanted on screen (not paused with the button).</summary>
    internal bool IsAnimatedOn { get; private set; }

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

    /// <summary>The still cover, plus the animated one on top when the request carries it.</summary>
    public ArtworkViewerDialog(ArtworkViewerRequest request) : this(request.StillPath)
    {
        _request = request;
        if (!HasAnimatedCover) return;

        AnimatedImage.Source = request.AnimatedPath;
        AnimatedImage.IsVisible = true;
        AnimatedImage.PropertyChanged += (_, e) =>
        {
            if (e.Property == Controls.AnimatedCoverImage.HasFrameProperty) UpdateAnimatedOpacity();
        };
        AnimatedToggle.IsVisible = true;
        IsAnimatedOn = request.PlayAnimated;
        // Leased once the window is shown (AnimatedCoverImage's own visibility rules): the
        // album page's header already runs this file's decoder, so the viewer joins it and
        // its current frame shows at once.
        AnimatedImage.IsActive = IsAnimatedOn;
        UpdateAnimatedToggle();
    }

    /// <summary>Plays or pauses the animated cover; <paramref name="remember"/> stores the
    /// choice for the next viewer.</summary>
    internal void SetAnimated(bool on, bool remember)
    {
        if (!HasAnimatedCover || _closed || on == IsAnimatedOn) return;
        IsAnimatedOn = on;
        var token = ++_pauseToken;
        if (on)
        {
            AnimatedImage.IsActive = true;
        }
        else
        {
            _ = ReleaseAfterFadeAsync(token);
        }
        UpdateAnimatedOpacity();
        UpdateAnimatedToggle();
        if (remember) _request?.PlayAnimatedChanged?.Invoke(on);
    }

    /// <summary>Paused: fade out over the still, then give the decoder lease back (a play
    /// press during the fade keeps it).</summary>
    private async Task ReleaseAfterFadeAsync(int token)
    {
        await Task.Delay(AnimatedFadeMs);
        if (token == _pauseToken && !IsAnimatedOn && !_closed)
            AnimatedImage.IsActive = false;
    }

    /// <summary>Gives the animated cover's decoder lease back at once (not after the usual
    /// hand-off linger): the decoder stops unless the album page behind still shows the file.</summary>
    private void ReleaseAnimated()
    {
        _pauseToken++;
        AnimatedImage.IsActive = false;
        AnimatedImage.Source = null;
    }

    private void UpdateAnimatedOpacity()
        => AnimatedImage.Opacity = IsAnimatedOn && AnimatedImage.HasFrame ? 1 : 0;

    private void UpdateAnimatedToggle()
    {
        var label = Loc.T(IsAnimatedOn ? "ArtworkViewer.PauseAnimated" : "ArtworkViewer.PlayAnimated");
        ToolTip.SetTip(AnimatedToggle, label);
        AutomationProperties.SetName(AnimatedToggle, label);
        if (this.TryFindResource(IsAnimatedOn ? "MenuLinePause" : "MenuLinePlay", out var glyph) && glyph is Geometry g)
            AnimatedToggleIcon.Data = g;
    }

    private void OnAnimatedToggleClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        e.Handled = true;
        SetAnimated(!IsAnimatedOn, remember: true);
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
            // Interface Size (GitHub #145): the frame is laid out in scaled units, so a device
            // pixel is RenderScaling × the scale, and the room is the scaled ClientSize.
            var scaling = Helpers.UiScale.DeviceScaling(this);
            var fit = FitArtwork(_sourceSize ?? new PixelSize(1, 1), Helpers.UiScale.ContentSize(this), scaling);
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        // Before the window hides and detaches: either of those would only release the
        // animated cover after its hand-off linger.
        if (!e.Cancel) ReleaseAnimated();
    }

    protected override void OnClosed(EventArgs e)
    {
        // Full-size covers run to tens of MB: let go now rather than at the next collection.
        _closed = true;
        FullImage.Source = null;
        FullBitmap?.Dispose();
        FullBitmap = null;
        PreviewImage.SourcePath = null;
        ReleaseAnimated();
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
    public static Task ShowAsync(string path) => ShowAsync(new ArtworkViewerRequest(path));

    /// <summary>Shows the request's cover (animated when it has one) over the main window until it is closed.</summary>
    public static async Task ShowAsync(ArtworkViewerRequest request)
    {
        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            var dialog = new ArtworkViewerDialog(request);
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}
