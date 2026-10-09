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

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ClientSizeProperty)
            FitToWindow(ClientSize);
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
