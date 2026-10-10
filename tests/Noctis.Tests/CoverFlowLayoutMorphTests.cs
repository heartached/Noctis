using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Cover Flow layout switch (owner 10-08): Carousel / Cascade / Collage glide into each
/// other instead of snapping. The playing cover morphs from the old layout's slot to the
/// new one's (both layers ride whole-layer transforms that keep their covers on top of each
/// other) while the rest crossfades; rapid switching carries on from wherever it is.
/// </summary>
public class CoverFlowLayoutMorphTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static (CoverFlowView view, CoverFlowViewModel vm, Window window) Open(bool withQueue = true)
    {
        EnsureAppStyles();
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        if (withQueue)
        {
            player.ReplaceQueueAndPlay(new[] { new Track { Title = "a", FilePath = "a.mp3" } }, 0);
            player.ReplaceQueueAndPlay(new[]
            {
                new Track { Title = "b", FilePath = "b.mp3" },
                new Track { Title = "c", FilePath = "c.mp3" },
            }, 0);
        }
        var vm = new CoverFlowViewModel(player) { IsActive = true };
        var view = new CoverFlowView { DataContext = vm };
        var window = new Window { Width = 1600, Height = 900, Content = view };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (view, vm, window);
    }

    private static Control Layer(CoverFlowView view, int layer) => layer switch
    {
        CoverFlowView.LayerCollage => view.FindControl<Viewbox>("CollageViewbox")!,
        CoverFlowView.LayerCarousel => view.FindControl<Grid>("CarouselHost")!,
        CoverFlowView.LayerCascade => view.FindControl<Grid>("StageGrid")!,
        _ => view.FindControl<StackPanel>("EmptyState")!,
    };

    private static int[] VisibleLayers(CoverFlowView view) =>
        Enumerable.Range(0, 4).Where(i => Layer(view, i).IsVisible).ToArray();

    /// <summary>The playing cover of a layer as drawn right now (layer morph transform
    /// included), in the layers' shared parent.</summary>
    private static Rect HeroOnScreen(CoverFlowView view, int layer)
    {
        Visual hero = layer switch
        {
            CoverFlowView.LayerCollage => view.FindControl<Border>("CollageCenterCover")!,
            CoverFlowView.LayerCarousel => view.FindControl<CoverFlowCard>("CenterCard")!.FindControl<Panel>("ArtworkPanel")!,
            _ => view.FindControl<Border>("CascadeCenterCover")!,
        };
        var parent = (Visual)Layer(view, layer).GetVisualParent()!;
        var m = hero.TransformToVisual(parent)!.Value;
        return new Rect(hero.Bounds.Size).TransformToAABB(m);
    }

    private static void AssertSameRect(Rect a, Rect b, double tol = 1.0)
    {
        Assert.Equal(a.Center.X, b.Center.X, tol);
        Assert.Equal(a.Center.Y, b.Center.Y, tol);
        Assert.Equal(a.Width, b.Width, tol);
    }

    [Fact]
    public void Map_PutsOneRectOnAnother_AndInterpolateRunsEndToEnd()
    {
        var from = new Rect(100, 100, 300, 300);
        var to = new Rect(600, 200, 480, 480);
        var pose = CoverFlowLayoutMorph.Map(from, to, 0.5);
        var landed = pose.Apply(from);
        Assert.Equal(to.Center, landed.Center);
        Assert.Equal(to.Width, landed.Width, 6);
        Assert.Equal(0.5, pose.Opacity);

        var start = new LayerPose(0, 0.5, 10, 20);
        Assert.Equal(start, CoverFlowLayoutMorph.Interpolate(start, LayerPose.Rest, 0));
        Assert.Equal(LayerPose.Rest, CoverFlowLayoutMorph.Interpolate(start, LayerPose.Rest, 1));
        // Coming in: fully opaque early so it covers the outgoing layer; going out: late.
        Assert.Equal(1, CoverFlowLayoutMorph.Interpolate(start, LayerPose.Rest, CoverFlowLayoutMorph.FadeInShare).Opacity, 6);
        Assert.True(CoverFlowLayoutMorph.Interpolate(LayerPose.Rest, start, 0.5).Opacity > 0.5);
        Assert.False(LayerPose.Rest.HasTransform);
    }

    [AvaloniaFact]
    public void OpensOnTheChosenLayout_WithoutAnimating()
    {
        var (view, _, _) = Open();
        Assert.False(view.IsMorphing);
        Assert.Equal(new[] { CoverFlowView.LayerCarousel }, VisibleLayers(view));
        Assert.Null(Layer(view, CoverFlowView.LayerCarousel).RenderTransform);
        Assert.True(view.FindControl<Panel>("BlurredBackground")!.IsVisible);
        Assert.False(view.FindControl<Panel>("CollageBackground")!.IsVisible);
    }

    [AvaloniaFact]
    public void CarouselToCascade_KeepsBothAlive_AndThePlayingCoverGlidesBetweenSlots()
    {
        var (view, vm, _) = Open();
        var carouselCoverAtRest = HeroOnScreen(view, CoverFlowView.LayerCarousel);

        vm.Layout = CoverFlowLayout.Cascade;

        Assert.True(view.IsMorphing);
        var carousel = Layer(view, CoverFlowView.LayerCarousel);
        var cascade = Layer(view, CoverFlowView.LayerCascade);
        Assert.True(carousel.IsVisible && cascade.IsVisible);
        Assert.True(cascade.ZIndex > carousel.ZIndex);
        Assert.False(carousel.IsHitTestVisible);

        // Frame 0: nothing has moved yet; the cascade's cover sits on the carousel's.
        view.ApplyMorphFrame(0);
        Assert.Equal(1, carousel.Opacity, 6);
        Assert.Equal(0, cascade.Opacity, 6);
        AssertSameRect(carouselCoverAtRest, HeroOnScreen(view, CoverFlowView.LayerCarousel));
        AssertSameRect(carouselCoverAtRest, HeroOnScreen(view, CoverFlowView.LayerCascade));

        // Mid-way both covers are the same card in between, crossfading.
        view.ApplyMorphFrame(0.4);
        var a = HeroOnScreen(view, CoverFlowView.LayerCarousel);
        var b = HeroOnScreen(view, CoverFlowView.LayerCascade);
        AssertSameRect(a, b);
        Assert.NotEqual(carouselCoverAtRest.Center.X, a.Center.X, 0);
        Assert.InRange(cascade.Opacity, 0.5, 1);
        Assert.InRange(carousel.Opacity, 0.01, 0.99);

        // Settled: only the cascade, untransformed, opaque, clickable.
        view.ApplyMorphFrame(1);
        AssertSameRect(HeroOnScreen(view, CoverFlowView.LayerCascade), HeroOnScreen(view, CoverFlowView.LayerCarousel));
        view.FinishLayoutMorph();
        Assert.False(view.IsMorphing);
        Assert.Equal(new[] { CoverFlowView.LayerCascade }, VisibleLayers(view));
        Assert.Null(cascade.RenderTransform);
        Assert.Null(carousel.RenderTransform);
        Assert.Equal(1, cascade.Opacity);
        Assert.Equal(1, carousel.Opacity);
        Assert.True(cascade.IsHitTestVisible && carousel.IsHitTestVisible);
    }

    [AvaloniaFact]
    public void ToCollage_FadesTheBlurredBackdropOverTheCharcoal_ThenStopsDrawingIt()
    {
        var (view, vm, _) = Open();
        var blurred = view.FindControl<Panel>("BlurredBackground")!;
        var charcoal = view.FindControl<Panel>("CollageBackground")!;

        vm.Layout = CoverFlowLayout.Collage;
        view.ApplyMorphFrame(0.3);
        var collage = Layer(view, CoverFlowView.LayerCollage);
        Assert.True(charcoal.IsVisible && blurred.IsVisible);
        Assert.Equal(1 - collage.Opacity, blurred.Opacity, 6);
        AssertSameRect(HeroOnScreen(view, CoverFlowView.LayerCarousel), HeroOnScreen(view, CoverFlowView.LayerCollage));

        view.FinishLayoutMorph();
        Assert.Equal(new[] { CoverFlowView.LayerCollage }, VisibleLayers(view));
        Assert.True(charcoal.IsVisible);
        Assert.False(blurred.IsVisible); // no blur rendered behind the collage at rest

        vm.Layout = CoverFlowLayout.Carousel;
        view.FinishLayoutMorph();
        Assert.True(blurred.IsVisible);
        Assert.Equal(1, blurred.Opacity);
        Assert.False(charcoal.IsVisible);
    }

    [AvaloniaFact]
    public void RapidSwitching_CarriesOnFromWhereItIs_AndLandsOnTheLastChoice()
    {
        var (view, vm, _) = Open();
        vm.Layout = CoverFlowLayout.Cascade;
        view.ApplyMorphFrame(0.3);
        var carousel = Layer(view, CoverFlowView.LayerCarousel);
        var cascade = Layer(view, CoverFlowView.LayerCascade);
        var before = (carousel.Opacity, cascade.Opacity, carousel.RenderTransform!.Value, cascade.RenderTransform!.Value);

        // Interrupt: every layer starts the new switch from exactly where it was drawn.
        vm.Layout = CoverFlowLayout.Collage;
        Assert.True(view.IsMorphing);
        view.ApplyMorphFrame(0);
        Assert.Equal(before.Item1, carousel.Opacity, 6);
        Assert.Equal(before.Item2, cascade.Opacity, 6);
        Assert.Equal(before.Item3, carousel.RenderTransform!.Value);
        Assert.Equal(before.Item4, cascade.RenderTransform!.Value);
        Assert.Equal(3, VisibleLayers(view).Length);
        Assert.Equal(0, Layer(view, CoverFlowView.LayerCollage).Opacity, 6);

        // And back again before that one ends, then let it land.
        view.ApplyMorphFrame(0.5);
        vm.Layout = CoverFlowLayout.Carousel;
        view.ApplyMorphFrame(0.5);
        Assert.True(carousel.ZIndex > cascade.ZIndex);
        view.ApplyMorphFrame(1);
        Assert.Equal(1, carousel.Opacity, 6);
        Assert.Equal(0, cascade.Opacity, 6);
        Assert.Equal(0, Layer(view, CoverFlowView.LayerCollage).Opacity, 6);
        Assert.Null(carousel.RenderTransform);
        view.FinishLayoutMorph();
        Assert.Equal(new[] { CoverFlowView.LayerCarousel }, VisibleLayers(view));
        Assert.Equal(CoverFlowView.LayerCarousel, view.LayoutTarget);
    }

    [AvaloniaFact]
    public void Morph_RunsOnTheFrameClock_AndStopsByItself()
    {
        var (view, vm, _) = Open();
        vm.Layout = CoverFlowLayout.Cascade;
        Assert.True(view.IsMorphing);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (view.IsMorphing && DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(10);
        }
        Assert.False(view.IsMorphing);
        Assert.Equal(new[] { CoverFlowView.LayerCascade }, VisibleLayers(view));
    }

    [AvaloniaFact]
    public void NothingPlaying_EmptyStateAndCollage_FadeWithoutASharedCover()
    {
        var (view, vm, _) = Open(withQueue: false);
        Assert.Equal(new[] { CoverFlowView.LayerEmpty }, VisibleLayers(view));

        vm.Layout = CoverFlowLayout.Collage;
        Assert.True(view.IsMorphing);
        view.ApplyMorphFrame(0);
        var collage = Layer(view, CoverFlowView.LayerCollage);
        var empty = Layer(view, CoverFlowView.LayerEmpty);
        Assert.Equal(0, collage.Opacity, 6);
        // No cover to share: the collage grows in gently about its centre.
        Assert.Equal(CoverFlowLayoutMorph.FallbackScale, collage.RenderTransform!.Value.M11, 6);
        Assert.Null(empty.RenderTransform);
        view.ApplyMorphFrame(1);
        Assert.Equal(0, empty.Opacity, 6);
        view.FinishLayoutMorph();
        Assert.Equal(new[] { CoverFlowView.LayerCollage }, VisibleLayers(view));

        // Collage → Carousel with nothing playing lands on the empty state.
        vm.Layout = CoverFlowLayout.Carousel;
        view.FinishLayoutMorph();
        Assert.Equal(new[] { CoverFlowView.LayerEmpty }, VisibleLayers(view));
    }
}
