using Avalonia.Controls;
using Noctis.Mobile.Services;

namespace Noctis.Mobile.Views;

public partial class SongListPage : UserControl
{
    public SongListPage()
    {
        InitializeComponent();
        // For the frame stall monitor (no-op while it is off): rows realized in a stalled frame.
        SongList.ContainerPrepared += (_, _) => FrameStallMonitor.Count("rows prepared");
    }
}
