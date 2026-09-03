using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Cover Flow stage is a pile of covers with the now-playing text beside it. On a
/// window too narrow for both, the text column must drop under the pile and centre —
/// otherwise the pile squeezes to a sliver next to a 400px text column.
/// </summary>
public class CoverFlowStageLayoutTests
{
    [AvaloniaFact]
    public void WideWindow_PutsTheTextBesideThePile()
    {
        var view = new CoverFlowView();
        var info = view.FindControl<StackPanel>("InfoStack")!;
        var pile = view.FindControl<Viewbox>("PileViewbox")!;

        view.ApplyStageLayout(1400);

        Assert.Equal(1, Grid.GetColumn(info));
        Assert.Equal(0, Grid.GetRow(info));
        Assert.Equal(2, Grid.GetRowSpan(info));
        Assert.Equal(2, Grid.GetRowSpan(pile));
        Assert.Equal(HorizontalAlignment.Left, info.HorizontalAlignment);
    }

    [AvaloniaFact]
    public void NarrowWindow_DropsTheTextUnderThePile_Centred()
    {
        var view = new CoverFlowView();
        var info = view.FindControl<StackPanel>("InfoStack")!;
        var pile = view.FindControl<Viewbox>("PileViewbox")!;

        view.ApplyStageLayout(1400);
        view.ApplyStageLayout(800);

        Assert.Equal(0, Grid.GetColumn(info));
        Assert.Equal(1, Grid.GetRow(info));
        Assert.Equal(1, Grid.GetRowSpan(info));
        Assert.Equal(1, Grid.GetRowSpan(pile));
        Assert.Equal(HorizontalAlignment.Center, info.HorizontalAlignment);
        Assert.Equal(HorizontalAlignment.Center, view.FindControl<Control>("TitleMarquee")!.HorizontalAlignment);
    }

    [AvaloniaFact]
    public void WideWindow_MakesThePileAFullHeightSquare()
    {
        // The design canvas is square and its top/bottom must be the page's own edges
        // (the 24/12 page insets are cancelled), so the outer cards are cut by the page,
        // never by an invisible canvas boundary in mid-air.
        var view = new CoverFlowView();
        var pile = view.FindControl<Viewbox>("PileViewbox")!;

        view.ApplyStageLayout(1600, 900);

        Assert.Equal(936, pile.Width, 3); // 900 + 24 + 12 page insets; 62% of 1600 (992) does not cap it
        Assert.Equal(pile.Width, pile.Height, 3);
    }

    [AvaloniaFact]
    public void VeryWideShortWindow_CapsThePileToAShareOfTheWidth()
    {
        var view = new CoverFlowView();
        var pile = view.FindControl<Viewbox>("PileViewbox")!;

        view.ApplyStageLayout(1200, 1400);

        Assert.Equal(1200 * 0.62, pile.Width, 3);
        Assert.Equal(pile.Width, pile.Height, 3);
    }

    [AvaloniaFact]
    public void NarrowWindow_SizesThePileToTheWidth_LeavingRoomForTheText()
    {
        var view = new CoverFlowView();
        var pile = view.FindControl<Viewbox>("PileViewbox")!;

        view.ApplyStageLayout(800, 900);

        Assert.Equal(540, pile.Width, 3); // 60% of the height wins over width-32
        Assert.Equal(pile.Width, pile.Height, 3);
    }

    [AvaloniaFact]
    public void Resizing_BackToWide_RestoresTheSideBySideLayout()
    {
        var view = new CoverFlowView();
        var info = view.FindControl<StackPanel>("InfoStack")!;

        view.ApplyStageLayout(800);
        view.ApplyStageLayout(CoverFlowView.SideBySideMinWidth);

        Assert.Equal(1, Grid.GetColumn(info));
        Assert.Equal(HorizontalAlignment.Left, view.FindControl<Control>("ArtistLink")!.HorizontalAlignment);
    }
}
