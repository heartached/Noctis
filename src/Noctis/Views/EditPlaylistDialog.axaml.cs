using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Noctis.ViewModels;

namespace Noctis.Views;

/// <summary>
/// Edit Playlist in the rounded pill pop-up (PillDialogHost: the blurred app behind and the
/// shared open/close animation). Every close — Cancel, Save, Esc, Alt+F4 — is a plain Close()
/// the host turns into the animated one, played once.
/// </summary>
public partial class EditPlaylistDialog : Window
{
    public EditPlaylistDialog()
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
    /// however many closes arrive). The caller reads the edit from the view model's
    /// PlaylistSaved event, so nothing rides on Close(result).
    /// </summary>
    public Task CloseAnimatedAsync()
    {
        Close();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Esc closes like Cancel. An open folder suggestion list takes the first Esc itself (the
    /// AutoCompleteBox handles it), so that one only closes the list.
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DataContext is EditPlaylistDialogViewModel vm)
        {
            e.Handled = true;
            vm.CancelCommand.Execute(null);
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>The chevron in the folder pill: every existing folder (filtered by whatever is
    /// typed), since the box on its own only suggests while typing.</summary>
    private void OnShowFoldersClick(object? sender, RoutedEventArgs e)
    {
        var open = !FolderBox.IsDropDownOpen;
        FolderBox.Focus();
        FolderBox.IsDropDownOpen = open;
    }
}
