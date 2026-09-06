using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class LyricsStudioDialog : Window
{
    public LyricsStudioDialog()
    {
        InitializeComponent();
    }

    public LyricsStudioDialog(LyricsStudioViewModel vm) : this()
    {
        DataContext = vm;
        vm.Closed += (_, _) => Close();
        vm.Confirm = message => ConfirmationDialog.ShowAsync(this, message);

        // Tap mode: Space stamps the next word, Esc leaves. Tunnelled so a focused Button
        // cannot swallow Space first; typing in a TextBox is left alone.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (DataContext is not LyricsStudioViewModel m || !m.IsTapping) return;
            if (e.Source is TextBox) return;
            if (e.Key == Key.Space) { m.TapCommand.Execute(null); e.Handled = true; }
            else if (e.Key == Key.Escape) { m.CancelTapCommand.Execute(null); e.Handled = true; }
        }, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (DataContext is LyricsStudioViewModel { IsTapping: true } && e.Key == Key.Space && e.Source is not TextBox) e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }
}
