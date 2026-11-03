using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class ThemeEditorDialog : Window
{
    public CustomThemeDefinition? Result { get; private set; }

    public ThemeEditorDialog()
    {
        InitializeComponent();
    }

    public ThemeEditorDialog(ThemeEditorViewModel vm) : this()
    {
        DataContext = vm;
        // PillDialogHost turns Close() into the animated close (once, however many arrive).
        vm.Saved += def => { Result = def; Close(); };
        vm.Cancelled += () => { Result = null; Close(); };
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The card's open animation is PillDialogHost's; just put the caret in the name.
        Dispatcher.UIThread.Post(() => NameTextBox.Focus(), DispatcherPriority.Loaded);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way the X does.
        if (e.Key == Key.Escape && DataContext is ThemeEditorViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
