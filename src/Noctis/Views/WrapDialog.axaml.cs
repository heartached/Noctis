using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Noctis Wrap: the listening recap as the shared rounded pill pop-up (PillDialogHost: the
/// blurred app behind, animated open/close). Esc, the X and a click on the backdrop all close;
/// every close animates out once. The stats build off the UI thread (WrapViewModel.Loading).
/// </summary>
public partial class WrapDialog : Window
{
    /// <summary>Largest card; a smaller window gets a card that fits it with this margin.</summary>
    private const double CardMaxWidth = 1080, CardMaxHeight = 820, CardMinWidth = 720, CardMinHeight = 460, WindowMargin = 32;

    // One save/copy at a time: a second click while the picker is up would ask again.
    private bool _busy;

    public WrapDialog()
    {
        InitializeComponent();
        PreviewImage.PropertyChanged += OnPreviewImagePropertyChanged;
        FitToWindow(ClientSize);
        // Light-dismiss, as the old overlay had: a click on the blurred backdrop closes. On the
        // window, not the host: outside the card the host draws nothing hit-testable.
        AddHandler(PointerPressedEvent, OnBackdropPointerPressed, RoutingStrategies.Tunnel);
    }

    public WrapDialog(WrapViewModel vm) : this()
    {
        DataContext = vm;
    }

    private WrapViewModel? Vm => DataContext as WrapViewModel;

    /// <summary>Share card cross-fade: the new card fades in over the old one.</summary>
    internal static readonly TimeSpan PreviewFade = TimeSpan.FromMilliseconds(220);

    private WrapViewModel? _previewVm;
    private bool _swapping;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_previewVm != null) _previewVm.PropertyChanged -= OnVmPropertyChanged;
        _previewVm = Vm;
        if (_previewVm != null) _previewVm.PropertyChanged += OnVmPropertyChanged;
        ShowPreview(_previewVm?.Preview, animate: false);
    }

    protected override void OnClosed(EventArgs e)
    {
        // The view-model disposes its cards when the dialog closes; stop following it first.
        if (_previewVm != null) _previewVm.PropertyChanged -= OnVmPropertyChanged;
        _previewVm = null;
        base.OnClosed(e);
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(WrapViewModel.Preview) && sender is WrapViewModel vm)
            ShowPreview(vm.Preview, animate: true);
    }

    private static Avalonia.Animation.Transitions Fade() => new()
    {
        new Avalonia.Animation.DoubleTransition
        {
            Property = OpacityProperty,
            Duration = PreviewFade,
            Easing = new Avalonia.Animation.Easings.CubicEaseOut(),
        },
    };

    /// <summary>
    /// Owner 11-26: the card flickered on Full year / This month. The outgoing card now stays
    /// underneath (PreviewImageOld) while the new one fades in on top; a card of another shape
    /// (1:1 vs 9:16) or no card at all also fades the old one out, so its edges don't snap
    /// away at the end. The view-model keeps the outgoing bitmap alive until the next swap.
    /// </summary>
    private void ShowPreview(Avalonia.Media.Imaging.Bitmap? next, bool animate)
    {
        if (PreviewImage is null || PreviewImageOld is null) return;
        var front = PreviewImage;
        var back = PreviewImageOld;
        if (ReferenceEquals(front.Source, next)) return;

        // The cards are being released (dialog closing): show nothing rather than a card
        // that is about to be disposed.
        if (_previewVm is { IsDisposed: true })
        {
            back.Source = null;
            front.Source = null;
            return;
        }

        // Stop any fade still running first: dropping a transition snaps its value to the
        // target, which would read as "fade finished" below. Keep where the front card was.
        var frontOpacity = front.Opacity;
        _swapping = true;
        try
        {
            back.Transitions = null;
            front.Transitions = null;
            var old = animate ? front.Source as Avalonia.Media.Imaging.Bitmap : null;
            back.Source = old;
            back.Opacity = old != null ? frontOpacity : 1;
            if (old != null && !(next != null && old.PixelSize == next.PixelSize))
            {
                back.Transitions = Fade();
                back.Opacity = 0;
            }

            front.Source = next;
            front.Opacity = animate ? 0 : 1;
            if (!animate) return;
            front.Transitions = Fade();
            front.Opacity = 1;
        }
        finally { _swapping = false; }
    }

    /// <summary>The new card is fully in: let the old one go.</summary>
    private void OnPreviewImagePropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_swapping || e.Property != OpacityProperty || PreviewImage.Opacity < 0.999 || PreviewImageOld.Source is null) return;
        PreviewImageOld.Transitions = null;
        PreviewImageOld.Source = null;
        PreviewImageOld.Opacity = 1;
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Interface Size (GitHub #145) changes the room the card has without a resize.
        if (change.Property == ClientSizeProperty || change.Property == Helpers.UiScale.AppliedProperty)
            FitToWindow(Helpers.UiScale.ContentSize(this));
    }

    /// <summary>The card at its designed size, or smaller to fit a small window (the report
    /// scrolls, so nothing is lost).</summary>
    internal void FitToWindow(Size client)
    {
        // ClientSize is first set while InitializeComponent is still building the tree.
        if (WrapCard is null || client.Width <= 0 || client.Height <= 0) return;
        WrapCard.Width = Math.Clamp(client.Width - 2 * WindowMargin, Math.Min(CardMinWidth, client.Width), CardMaxWidth);
        WrapCard.Height = Math.Clamp(client.Height - 2 * WindowMargin, Math.Min(CardMinHeight, client.Height), CardMaxHeight);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // An open year list takes its own Esc first; otherwise Esc closes, animated.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // With the year list open, the click outside closes the list, not the dialog.
        if (YearBox.IsDropDownOpen) return;
        if (new Rect(WrapCard.Bounds.Size).Contains(e.GetPosition(WrapCard))) return;
        e.Handled = true;
        Close();
    }

    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        if (_busy || Vm?.CurrentPng is not { } png) return;
        _busy = true;
        try
        {
            var status = await PngExportHelper.SavePngAsync(this, png, Vm!.SuggestedFileName);
            if (status != null)
                Vm?.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WrapDialog] Save failed: {ex.Message}");
            Vm?.ReportStatus(Localization.Loc.T("Wrap.SaveFailed"));
        }
        finally { _busy = false; }
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        if (_busy || Vm?.CurrentPng is not { } png) return;
        _busy = true;
        try
        {
            var status = await PngExportHelper.CopyPngAsync(this, png, Vm!.SuggestedFileName);
            if (status != null)
                Vm?.ReportStatus(status);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WrapDialog] Copy failed: {ex.Message}");
            Vm?.ReportStatus(Localization.Loc.T("Wrap.CopyFailed"));
        }
        finally { _busy = false; }
    }

    public static async Task ShowAsync(WrapViewModel vm)
    {
        var dialog = new WrapDialog(vm);
        // The view-model is built fresh per open and holds a full-size share-card bitmap
        // plus its PNG bytes; nothing released them when the dialog went away.
        dialog.Closed += (_, _) => vm.Dispose();

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
        else
        {
            dialog.Show();
        }
    }
}
