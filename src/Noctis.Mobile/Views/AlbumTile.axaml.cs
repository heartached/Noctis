using Avalonia.Controls;

namespace Noctis.Mobile.Views;

public partial class AlbumTile : UserControl
{
    public AlbumTile()
    {
        InitializeComponent();
        Services.FrameStallMonitor.Count("album tiles built");
    }
}
