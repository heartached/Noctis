using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Apple Music-style View Options sheet for the Songs list: sort field, direction,
/// favorites filter and column visibility, in the shared rounded pill pop-up
/// (PillDialogHost: blurred app behind, animated open/close). Everything applies live, so
/// the dialog has no OK/Cancel — Done, Escape and a click on the backdrop all just close.
/// </summary>
public partial class SongsViewOptionsDialog : Window
{
    public SongsViewOptionsDialog()
    {
        InitializeComponent();
        // Light-dismiss, as the sheet always had: nothing is pending, so a click on the
        // blurred backdrop closes it. On the window, not the host: outside the card the host
        // draws nothing hit-testable (its backdrop layer is IsHitTestVisible=False), so a
        // backdrop press is routed to the window alone and a handler on the host never saw it.
        AddHandler(PointerPressedEvent, OnBackdropPointerPressed, RoutingStrategies.Tunnel);
    }

    public SongsViewOptionsDialog(SongsViewOptionsViewModel vm) : this()
    {
        DataContext = vm;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape is Done (an open drop-down takes its own Escape first, so the key never
        // reaches here then). PillDialogHost turns the Close into the animated one.
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close();
            return;
        }
        base.OnKeyDown(e);
    }

    private void OnDoneClick(object? sender, RoutedEventArgs e) => Close();

    private void OnBackdropPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        // With the Sort by list open, the click outside belongs to the list's own
        // light-dismiss: it closes the list, not the sheet.
        if (SortCombo.IsDropDownOpen) return;
        // Inside the card (its rounded corners included) is the card's business.
        if (new Rect(OptionsCard.Bounds.Size).Contains(e.GetPosition(OptionsCard))) return;
        e.Handled = true;
        Close();
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
