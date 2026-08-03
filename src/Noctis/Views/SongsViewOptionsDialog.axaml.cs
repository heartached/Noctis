using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Apple Music-style View Options sheet for the Songs list: sort field, direction,
/// favorites filter and column visibility. Everything applies live, so the dialog has
/// no OK/Cancel — closing it simply dismisses the sheet.
/// </summary>
public partial class SongsViewOptionsDialog : Window
{
    private bool _closing;

    public SongsViewOptionsDialog()
    {
        InitializeComponent();
    }

    public SongsViewOptionsDialog(SongsViewOptionsViewModel vm) : this()
    {
        DataContext = vm;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // Settle to the open state on the next frame so the fade/scale
        // transitions animate it (same pattern as the description dialogs).
        Dispatcher.UIThread.Post(() =>
        {
            DialogOverlay.Opacity = 1;
            OptionsCard.RenderTransform = TransformOperations.Parse("scale(1)");
        }, DispatcherPriority.Loaded);
    }

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        // async void: an escaped exception would crash the app.
        try { await CloseAnimatedAsync(); }
        catch { Close(); }
    }

    /// <summary>Plays the fade/scale close animation, then closes the window.</summary>
    private async Task CloseAnimatedAsync()
    {
        if (_closing) return;
        _closing = true;
        DialogOverlay.Opacity = 0;
        OptionsCard.RenderTransform = TransformOperations.Parse("scale(0.96)");
        await Task.Delay(200);
        Close();
    }

    private async void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        // async void: an escaped exception would crash the app.
        try { await CloseAnimatedAsync(); }
        catch { Close(); }
    }

    private async void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Light-dismiss: a click on the dimmed backdrop closes the sheet. Nothing here
        // is pending, so unlike the description dialogs there is no unsaved-edit guard.
        e.Handled = true;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // async void: an escaped exception would crash the app.
        try { await CloseAnimatedAsync(); }
        catch { Close(); }
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnOverlayWheel(object? sender, PointerWheelEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task ShowAsync(SongsViewOptionsViewModel vm)
    {
        var dialog = new SongsViewOptionsDialog(vm);

        if (Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop
            && desktop.MainWindow is Window owner)
        {
            DialogHelper.SizeToOwner(dialog, owner);
            await dialog.ShowDialog(owner);
        }
    }
}
