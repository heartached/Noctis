using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class CreateSmartPlaylistDialog : Window
{
    public CreateSmartPlaylistDialog()
    {
        InitializeComponent();
        AddDescriptionButton.Click += (_, _) => ShowDescription(focus: true);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        // A description that is already there (never for a new playlist today) shows open.
        if (DataContext is CreateSmartPlaylistDialogViewModel { PlaylistDescription.Length: > 0 })
            ShowDescription(focus: false);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        // The card's open animation is PillDialogHost's; just put the caret in the name.
        Dispatcher.UIThread.Post(() => NameTextBox.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>"Add description": the optional field glides open under the name and the
    /// link that asked for it goes away.</summary>
    private void ShowDescription(bool focus)
    {
        DescriptionReveal.IsOpen = true;
        AddDescriptionButton.IsVisible = false;
        if (focus)
            Dispatcher.UIThread.Post(() => DescriptionTextBox.Focus(), DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Closes the dialog. PillDialogHost turns the close into the animated one (it plays once
    /// however many closes arrive). The caller reads the result from the view model's
    /// SmartPlaylistCreated event, so nothing rides on Close(result).
    /// </summary>
    public Task CloseAnimatedAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        // Escape closes the same way Cancel does (an open drop-down takes its own Escape
        // first, so the key never reaches here then).
        if (e.Key == Key.Escape && DataContext is CreateSmartPlaylistDialogViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }
}
