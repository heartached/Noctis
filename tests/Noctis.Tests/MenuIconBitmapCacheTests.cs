using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Context-menu icons are PNG masks (several 512×512). Every menu build used to decode its
/// own copy of each; album and playlist pages build a fresh menu per page instance.
/// </summary>
public class MenuIconBitmapCacheTests
{
    [AvaloniaFact]
    public void MenuIcons_ShareOneDecodedBitmapPerAsset()
    {
        const string uri = "avares://Noctis.UI/Assets/Icons/Play%20ICON.png";
        var a = TrackContextMenuBuilder.CreatePngIcon(uri);
        var b = TrackContextMenuBuilder.CreatePngIcon(uri, 17);
        var sourceA = ((ImageBrush)a.OpacityMask!).Source;
        var sourceB = ((ImageBrush)b.OpacityMask!).Source;
        Assert.NotNull(sourceA);
        Assert.Same(sourceA, sourceB);
    }

    [AvaloniaFact]
    public void TwoMenuBuilds_ShareTheirIconBitmaps()
    {
        var host = new Avalonia.Controls.Border();
        foreach (var key in new[] { "HeartFillIcon", "StarIcon", "TrashIcon" })
            host.Resources[key] = Geometry.Parse("M0 0L1 1"); // the geometry icons Build resolves
        var first = new TrackContextMenuBuilder();
        first.Build("Remove", null, host);
        var second = new TrackContextMenuBuilder();
        second.Build("Remove", null, host);

        var playA = ((ImageBrush)((Avalonia.Controls.Border)first.Play.Icon!).OpacityMask!).Source;
        var playB = ((ImageBrush)((Avalonia.Controls.Border)second.Play.Icon!).OpacityMask!).Source;
        Assert.Same(playA, playB);
    }
}
