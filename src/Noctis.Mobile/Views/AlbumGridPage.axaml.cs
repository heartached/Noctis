using Avalonia.Controls;

namespace Noctis.Mobile.Views;

public partial class AlbumGridPage : UserControl
{
    public AlbumGridPage()
    {
        InitializeComponent();
        AlbumRows.ContainerPrepared += (_, _) => Services.FrameStallMonitor.Count("album rows prepared");
    }
}
