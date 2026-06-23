using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class CommandPaletteDialog : Window
{
    public CommandPaletteDialog()
    {
        InitializeComponent();
    }

    public CommandPaletteDialog(CommandPaletteViewModel vm) : this()
    {
        DataContext = vm;
        vm.CloseRequested += (_, _) => Close();

        Opened += (_, _) => Dispatcher.UIThread.Post(() => QueryBox.Focus());
        KeyDown += OnDialogKeyDown;
    }

    private CommandPaletteViewModel? Vm => DataContext as CommandPaletteViewModel;

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        var vm = Vm;
        if (vm == null) return;

        switch (e.Key)
        {
            case Key.Escape:
                Close();
                e.Handled = true;
                break;
            case Key.Down:
                vm.MoveSelection(1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Up:
                vm.MoveSelection(-1);
                ScrollSelectionIntoView();
                e.Handled = true;
                break;
            case Key.Enter:
                vm.ExecuteSelectedCommand.Execute(null);
                e.Handled = true;
                break;
        }
    }

    private void ScrollSelectionIntoView()
    {
        var vm = Vm;
        if (vm == null || vm.SelectedIndex < 0) return;
        ResultsList.ScrollIntoView(vm.SelectedIndex);
    }

    private void OnItemPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (sender is Control { DataContext: PaletteItem item })
        {
            Vm?.ExecuteItemCommand.Execute(item);
            e.Handled = true;
        }
    }

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task ShowAsync(CommandPaletteViewModel vm)
    {
        var dialog = new CommandPaletteDialog(vm);

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
