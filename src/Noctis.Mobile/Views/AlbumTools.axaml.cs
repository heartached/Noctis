using Noctis.Controls;

namespace Noctis.Mobile.Views;

public partial class AlbumTools : GlassPanel
{
    /// <summary>Styled as the GlassPanel it is (MobileStyles' header-glass).</summary>
    protected override Type StyleKeyOverride => typeof(GlassPanel);

    public AlbumTools()
    {
        InitializeComponent();
    }
}
