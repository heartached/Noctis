using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class CreatePlaylistDialog : Window
{
    public CreatePlaylistDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The card's open animation is PillDialogHost's; just put the caret in the name.
        Dispatcher.UIThread.Post(() => NameTextBox.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Closes the dialog. PillDialogHost turns the close into the animated one (it plays once
    /// however many closes arrive). The caller reads the result from the view model's
    /// PlaylistCreated event, so nothing rides on Close(result).
    /// </summary>
    public Task CloseAnimatedAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Cancel does.
        if (e.Key == Key.Escape && DataContext is CreatePlaylistDialogViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
