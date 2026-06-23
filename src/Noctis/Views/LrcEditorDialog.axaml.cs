using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Noctis.Helpers;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LrcEditorDialog : Window
{
    public LrcEditorDialog()
    {
        InitializeComponent();
    }

    public LrcEditorDialog(LrcEditorViewModel vm) : this()
    {
        DataContext = vm;
        KeyDown += OnDialogKeyDown;

        // Keep the highlighted line in view while tap-syncing.
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(LrcEditorViewModel.SelectedIndex) && vm.SelectedIndex >= 0)
                LinesList.ScrollIntoView(vm.SelectedIndex);
        };
    }

    private LrcEditorViewModel? Vm => DataContext as LrcEditorViewModel;

    private void OnDialogKeyDown(object? sender, KeyEventArgs e)
    {
        // Space = tap-to-sync stamp (the core editing gesture).
        if (e.Key == Key.Space)
        {
            Vm?.StampCurrentCommand.Execute(null);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void OnCloseClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => Close();

    private void OnOverlayPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        Close();
        e.Handled = true;
    }

    private void OnCardPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    public static async Task ShowAsync(LrcEditorViewModel vm)
    {
        var dialog = new LrcEditorDialog(vm);

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
