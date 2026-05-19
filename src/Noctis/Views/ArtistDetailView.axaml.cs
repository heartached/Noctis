using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Noctis.Views;

public partial class ArtistDetailView : UserControl
{
    public ArtistDetailView()
    {
        InitializeComponent();
    }

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);
}
