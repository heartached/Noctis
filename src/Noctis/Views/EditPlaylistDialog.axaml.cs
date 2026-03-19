using Avalonia.Controls;

namespace Noctis.Views;

public partial class EditPlaylistDialog : Window
{
    public EditPlaylistDialog()
    {
        InitializeComponent();
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        NameTextBox.Focus();
    }
}
