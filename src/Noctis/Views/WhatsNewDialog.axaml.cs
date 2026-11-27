using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Settings › About › What's new: this version's release notes in the shared rounded pill
/// pop-up (PillDialogHost). The notes load when it opens (WhatsNewViewModel.LoadAsync). The X,
/// Done, Esc and a click on the blurred backdrop close it; every close animates out once.
/// </summary>
public partial class WhatsNewDialog : Window
{
    /// <summary>Tallest card; a shorter window gets a card that fits it with this margin.</summary>
    private const double CardMaxHeight = 760, CardMinHeight = 320, WindowMargin = 32;

    public WhatsNewDialog()
    {
        InitializeComponent();
        FitToWindow(ClientSize);
        // Light dismiss: outside the card the host draws nothing hit-testable, so the press
        // reaches the window (the WrapDialog recipe).
        AddHandler(PointerPressedEvent, OnBackdropPointerPressed, RoutingStrategies.Tunnel);
    }

    public WhatsNewDialog(WhatsNewViewModel vm) : this()
    {
        DataContext = vm;
    }

    /// <summary>The notes' load (tests wait on it).</summary>
    internal Task Loading { get; private set; } = Task.CompletedTask;

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        if (DataContext is WhatsNewViewModel vm) Loading = vm.LoadAsync();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        // Interface Size (GitHub #145) changes the room the card has without a resize.
        if (change.Property == ClientSizeProperty || change.Property == UiScale.AppliedProperty)
            FitToWindow(UiScale.ContentSize(this));
    }

    /// <summary>The card grows with its notes up to the window's height (then they scroll).</summary>
    internal void FitToWindow(Size client)
    {
        // ClientSize is first set while InitializeComponent is still building the tree.
        if (CardRoot is null || client.Height <= 0) return;
        CardRoot.MaxHeight = Math.Clamp(client.Height - 2 * WindowMargin, Math.Min(CardMinHeight, client.Height), CardMaxHeight);
        if (client.Width > 0)
            CardRoot.Width = Math.Min(640, Math.Max(360, client.Width - 2 * WindowMargin));
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
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
        if (new Rect(CardRoot.Bounds.Size).Contains(e.GetPosition(CardRoot))) return;
        e.Handled = true;
        Close();
    }

    public static async Task ShowAsync(WhatsNewViewModel vm)
    {
        var dialog = new WhatsNewDialog(vm);
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
