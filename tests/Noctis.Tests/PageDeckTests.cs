using Avalonia;
using Avalonia.Animation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Rendering.Composition;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// PageDeck (11-15): cached section views stay in the logical tree (styled) while another page
/// shows, out of the visual tree and invisible, so a revisit does not restyle the whole page.
/// These pin each hazard of keeping a hidden page: no layout or rendering, paused animations,
/// no focus, nothing in the automation tree, the views' attach/detach work (scroll restore,
/// cover release) still running on every switch, transient pages leaving entirely, and the cap.
/// </summary>
public class PageDeckTests
{
    public sealed class VmA { }
    public sealed class VmB { }
    public sealed class VmC { }
    public sealed class DetailVm { public string Name { get; init; } = ""; }

    /// <summary>A page that counts its layout and render passes.</summary>
    public sealed class CountingPage : UserControl
    {
        public int Measures, Renders, Attaches, Detaches;
        public readonly Button Button = new() { Content = "x", Focusable = true };
        public readonly Border Spinner = new() { Width = 10, Height = 10, Classes = { "spin" }, RenderTransform = new RotateTransform() };
        public readonly CachedImage Cover = new() { Width = 40, Height = 40 };
        public readonly EqVisualizer Eq = new() { Width = 20, Height = 20 };

        public CountingPage()
        {
            Content = new StackPanel { Children = { Button, Spinner, Cover, Eq } };
        }

        protected override Size MeasureOverride(Size availableSize) { Measures++; return base.MeasureOverride(availableSize); }
        public override void Render(DrawingContext context) { Renders++; base.Render(context); }
        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { Attaches++; base.OnAttachedToVisualTree(e); }
        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { Detaches++; base.OnDetachedFromVisualTree(e); }
    }

    private static void Pump(int n = 3)
    {
        for (var i = 0; i < n; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static (PageDeck Deck, Window Window, Dictionary<Type, CountingPage> Pages) Mount(bool eqTemplate = false)
    {
        var pages = new Dictionary<Type, CountingPage>
        {
            [typeof(VmA)] = new CountingPage(), [typeof(VmB)] = new CountingPage(), [typeof(VmC)] = new CountingPage(),
        };
        var deck = new PageDeck();
        deck.DataTemplates.Add(new CachedViewLocator(pages.ToDictionary(p => p.Key, p => (Func<Control>)(() => p.Value))));
        deck.DataTemplates.Add(new FuncDataTemplate<DetailVm>((vm, _) => new CountingPage(), supportsRecycling: true));
        // An INFINITE style animation, keyed on the element's own IsVisible as the app's spinners are.
        var spin = new Style(x => x.OfType<Border>().Class("spin"));
        spin.Animations.Add(new Animation
        {
            Duration = TimeSpan.FromSeconds(1), IterationCount = IterationCount.Infinite,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(RotateTransform.AngleProperty, 0d) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(RotateTransform.AngleProperty, 360d) } },
            },
        });
        deck.Styles.Add(spin);
        var window = new Window { Width = 800, Height = 600, Content = deck };
        if (eqTemplate)
            window.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/"))
                { Source = new Uri("avares://Noctis/Controls/EqVisualizer.axaml") });
        window.Show();
        Pump();
        return (deck, window, pages);
    }

    [AvaloniaFact]
    public void ASectionShownAgain_IsTheSameStyledView_KeptLogicallyWhileHidden()
    {
        var (deck, window, pages) = Mount();
        var a = new VmA();
        deck.Page = a;
        Pump();
        var view = pages[typeof(VmA)];
        Assert.Same(view, deck.ShownView);
        Assert.Same(a, view.DataContext);

        deck.Page = new VmB();
        Pump();
        Assert.True(((ILogical)view).IsAttachedToLogicalTree, "a hidden section left the logical tree (its styles go)");
        Assert.Null(view.GetVisualParent());
        Assert.False(view.IsVisible);
        Assert.Equal(1, view.Detaches);

        deck.Page = a;
        Pump();
        Assert.Same(view, deck.ShownView);
        Assert.True(view.IsVisible);
        Assert.Same(deck, view.GetVisualParent());
        Assert.Equal(2, view.Attaches); // the view's own attach work runs on every show, as before
        window.Close();
    }

    [AvaloniaFact]
    public void AHiddenSection_IsNotLaidOutOrRendered_AndItsAnimationsPause()
    {
        var (deck, window, pages) = Mount();
        deck.Page = new VmA();
        Pump();
        var view = pages[typeof(VmA)];
        Assert.True(view.Measures > 0 && view.Renders > 0);
        deck.Page = new VmB();
        Pump();

        int measures = view.Measures, renders = view.Renders;
        var angle = ((RotateTransform)view.Spinner.RenderTransform!).Angle;
        for (var i = 0; i < 20; i++)
        {
            view.InvalidateMeasure();
            view.InvalidateVisual();
            view.Spinner.InvalidateMeasure();
            Pump(1);
            Thread.Sleep(5);
        }
        Assert.Equal(measures, view.Measures);
        Assert.Equal(renders, view.Renders);
        Assert.Null(ElementComposition.GetElementVisual(view));
        // Paused: Avalonia pauses the animations of effectively invisible controls (the headless
        // clock does not advance on its own, so the pause is pinned through the visibility).
        Assert.False(view.Spinner.IsEffectivelyVisible);
        Assert.False(view.Button.IsEffectivelyVisible);
        Assert.Equal(angle, ((RotateTransform)view.Spinner.RenderTransform!).Angle);
        window.Close();
    }

    [AvaloniaFact]
    public void AHiddenSection_TakesNoFocus_AndIsNotInTheAutomationTree()
    {
        var (deck, window, pages) = Mount();
        deck.Page = new VmA();
        Pump();
        var view = pages[typeof(VmA)];
        Assert.True(view.Button.Focus());
        deck.Page = new VmB();
        Pump();

        Assert.NotSame(view.Button, window.FocusManager!.GetFocusedElement());
        Assert.False(view.Button.Focus());

        var peers = new List<AutomationPeer>();
        void Walk(AutomationPeer p) { foreach (var c in p.GetChildren()) { peers.Add(c); Walk(c); } }
        Walk(ControlAutomationPeer.CreatePeerForElement(window));
        var owners = peers.OfType<ControlAutomationPeer>().Select(p => p.Owner).ToList();
        Assert.DoesNotContain(view, owners);
        Assert.DoesNotContain(view.Button, owners);
        Assert.Contains(pages[typeof(VmB)].Button, owners);
        window.Close();
    }

    /// <summary>The background cover trim drops covers no control holds: a hidden page's
    /// CachedImage lets its bitmap go (DetachedFromVisualTree), as a detached page's did.</summary>
    [AvaloniaFact]
    public void AHiddenSection_ReleasesItsCovers()
    {
        var (deck, window, pages) = Mount();
        var view = pages[typeof(VmA)];
        deck.Page = new VmA();
        Pump();
        Assert.True(view.Cover.IsAttachedToVisualTree());
        deck.Page = new VmB();
        Pump();
        Assert.False(view.Cover.IsAttachedToVisualTree());
        Assert.Null(view.Cover.Source);
        window.Close();
    }

    /// <summary>Album / artist / playlist pages leave entirely when another page shows (their
    /// view model may be disposed with the history), and the next one of the same kind re-uses
    /// the shown view as the ContentControl did.</summary>
    [AvaloniaFact]
    public void TransientPages_LeaveEntirely_AndASameKindPageReusesTheShownView()
    {
        var (deck, window, _) = Mount();
        deck.Page = new DetailVm { Name = "one" };
        Pump();
        var first = deck.ShownView!;
        var two = new DetailVm { Name = "two" };
        deck.Page = two;
        Pump();
        Assert.Same(first, deck.ShownView);
        Assert.Same(two, first.DataContext);

        deck.Page = new VmA();
        Pump();
        Assert.False(((ILogical)first).IsAttachedToLogicalTree);
        Assert.Null(first.Parent);
        Assert.DoesNotContain(first, deck.KeptViews);
        Assert.DoesNotContain(first, deck.GetLogicalChildren());

        deck.Page = new DetailVm { Name = "three" };
        Pump();
        Assert.NotSame(first, deck.ShownView); // a section shown in between: a fresh view
        window.Close();
    }

    [AvaloniaFact]
    public void KeptSections_AreCapped_LeastRecentlyShownLeavesFirst()
    {
        // One view model type (and cached view) per section: Slot<byte>, Slot<sbyte>, ...
        var count = PageDeck.MaxKept + 3;
        var views = new List<Control>();
        var pages = new List<object>();
        var map = new Dictionary<Type, Func<Control>>();
        for (var i = 0; i < count; i++)
        {
            var type = typeof(Slot<>).MakeGenericType(SlotTypes[i]);
            var view = new UserControl();
            views.Add(view);
            map[type] = () => view;
            pages.Add(Activator.CreateInstance(type)!);
        }
        var deck = new PageDeck();
        deck.DataTemplates.Add(new CachedViewLocator(map));
        var window = new Window { Width = 400, Height = 300, Content = deck };
        window.Show();
        foreach (var page in pages)
        {
            deck.Page = page;
            Pump(1);
        }
        Assert.Equal(PageDeck.MaxKept, deck.KeptViews.Count);
        Assert.Equal(PageDeck.MaxKept, deck.GetLogicalChildren().Count());
        for (var i = 0; i < count; i++)
        {
            var kept = i >= count - PageDeck.MaxKept;
            Assert.Equal(kept, ((ILogical)views[i]).IsAttachedToLogicalTree);
            if (!kept) Assert.True(views[i].IsVisible); // a dropped view is left as it was found
        }
        window.Close();
    }

    public sealed class Slot<T> { }
    private static readonly Type[] SlotTypes =
    {
        typeof(byte), typeof(sbyte), typeof(short), typeof(ushort), typeof(int), typeof(uint), typeof(long), typeof(ulong),
        typeof(float), typeof(double), typeof(decimal), typeof(char), typeof(bool), typeof(string),
    };

    // ── real views ──

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    /// <summary>The Songs page saves its scroll offset when it leaves the screen and restores
    /// it when it comes back (DetachedFrom/AttachedToVisualTree): still so when kept.</summary>
    [AvaloniaFact]
    public async Task Songs_KeptBetweenVisits_ComesBackWhereItWasScrolled_WithoutARestyle()
    {
        EnsureAppStyles();
        var lib = new FakeLibraryService();
        for (var i = 0; i < 400; i++)
            lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Artist", Album = "Album",
                FilePath = TestPaths.Primary("k", $"{i}.mp3"), Duration = TimeSpan.FromMinutes(3) });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var songs = new LibrarySongsViewModel(lib, player, new SidebarViewModel(persistence, lib), persistence) { IsActive = true };
        var songsView = new LibrarySongsView();
        var other = new VmB();
        var deck = new PageDeck();
        deck.DataTemplates.Add(new CachedViewLocator(new Dictionary<Type, Func<Control>>
        {
            [typeof(LibrarySongsViewModel)] = () => songsView,
            [typeof(VmB)] = () => new UserControl(),
        }));
        var window = new Window { Width = 1400, Height = 900, Content = deck };
        window.Show();
        deck.Page = songs;
        var deadline = Environment.TickCount64 + 5000;
        while (songs.FilteredTracks.Count < 400 && Environment.TickCount64 < deadline) { Pump(1); await Task.Delay(5); }
        Pump();
        var sv = songsView.TrackList.FindDescendantOfType<ScrollViewer>()!;
        sv.Offset = new Vector(0, 1500);
        Pump();
        var row = songsView.TrackList.GetRealizedContainers()!.First();
        var rowBody = row.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("row-body"));

        var styleApplications = 0;
        void Count(object? s, Avalonia.LogicalTree.LogicalTreeAttachmentEventArgs e) => styleApplications++;
        rowBody.AttachedToLogicalTree += Count;
        deck.Page = other;
        Pump();
        deck.Page = songs;
        deadline = Environment.TickCount64 + 2000;
        while (Math.Abs(sv.Offset.Y - 1500) > 1 && Environment.TickCount64 < deadline) { Pump(1); await Task.Delay(5); }

        Assert.Equal(1500, sv.Offset.Y, 0);
        Assert.Equal(0, styleApplications); // no logical re-attach: no restyle
        Assert.Same(songsView, deck.ShownView);

        // Window shortcuts reach the shown page only (its WindowKeyForwarder follows the visual tree).
        void CtrlA() => window.RaiseEvent(new Avalonia.Input.KeyEventArgs
        {
            RoutedEvent = Avalonia.Input.InputElement.KeyDownEvent, Key = Avalonia.Input.Key.A,
            KeyModifiers = Noctis.Helpers.PlatformHelper.PrimaryModifier, Source = window,
        });
        deck.Page = other;
        Pump();
        CtrlA();
        Pump();
        Assert.Empty(songsView.GetSelectedTracks());
        deck.Page = songs;
        Pump();
        CtrlA();
        Pump();
        Assert.Equal(400, songsView.GetSelectedTracks().Count);
        window.Close();
    }

    /// <summary>A now-playing EQ in a hidden page: it went by the logical tree, which a kept
    /// page stays in, and would have animated off screen. It stops on hide, does not start
    /// while hidden, and starts again on show.</summary>
    [AvaloniaFact]
    public void AnEqVisualizerInAHiddenSection_DoesNotAnimate()
    {
        var (deck, window, pages) = Mount(eqTemplate: true);
        var view = pages[typeof(VmA)];
        view.Eq.IsPlaying = true;
        deck.Page = new VmA();
        Pump();
        var timer = (DispatcherTimer?)typeof(EqVisualizer).GetField("_animTimer",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(view.Eq);
        Assert.True(timer?.IsEnabled == true);

        deck.Page = new VmB();
        Pump();
        Assert.False(timer!.IsEnabled);
        view.Eq.IsPlaying = false;
        view.Eq.IsPlaying = true;
        Pump();
        Assert.False(timer.IsEnabled);

        deck.Page = new VmA();
        Pump();
        Assert.True(timer.IsEnabled);
        window.Close();
    }
}
