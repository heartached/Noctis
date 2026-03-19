using Avalonia.Controls;

namespace Noctis.Views;

public partial class CreateSmartPlaylistDialog : Window
{
    public CreateSmartPlaylistDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameTextBox.Focus();
    }
}
