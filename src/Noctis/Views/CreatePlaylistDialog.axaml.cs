using Avalonia.Controls;
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

        // Focus the name text box when the dialog opens
        NameTextBox.Focus();
    }
}
