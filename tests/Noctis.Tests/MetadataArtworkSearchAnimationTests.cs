using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: animate artwork search + one Find online style. The Artwork and Animated
/// Artwork search pop-ups open and close on the pill dialog's curves and timings — fade, short
/// rise, slight grow (0.96 → 1), reversed on every way out — and each way out (click outside,
/// Esc, a pick / the VM flag, the editor closing) plays the exit once, hides the flyout and
/// leaves the VM flag false.
/// </summary>
public class MetadataArtworkSearchAnimationTests
{
    private readonly ITestOutputHelper _o;
    public MetadataArtworkSearchAnimationTests(ITestOutputHelper o) => _o = o;

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static MetadataViewModel Vm()
    {
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "monaco", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny", Album = "nadie sabe",
            AlbumId = Guid.NewGuid(), TrackNumber = 2, Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(267),
            FilePath = "C:/m/does-not-exist/monaco.flac",
        };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        return new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), metadataSearch: new MetadataSearchPanelTests.FakeSearch());
    }

    private static Matrix Pose(Visual v) => ((TransformOperations)v.RenderTransform!).Value;

    private static void AssertTiming(Visual content, TimeSpan fade, TimeSpan move, object easing)
    {
        var fadeT = content.Transitions!.OfType<DoubleTransition>().Single(t => t.Property == Visual.OpacityProperty);
        var moveT = content.Transitions!.OfType<TransformOperationsTransition>().Single();
        Assert.Equal(fade, fadeT.Duration);
        Assert.Equal(move, moveT.Duration);
        Assert.Same(easing, fadeT.Easing);
        Assert.Same(easing, moveT.Easing);
    }

    /// <summary>The editor on the given tab, its open animation settled.</summary>
    private static (MetadataViewModel vm, MetadataWindow win) Editor(string tab)
    {
        EnsureAppStyles();
        var vm = Vm();
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        var load = vm.InitializeAsync();
        Assert.True(PumpUntil(() => load.IsCompleted));
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }), "dialog open animation never settled");
        var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
        tabs.SelectedItem = tabs.Items.OfType<TabItem>().First(t => Equals(t.Header, tab));
        PumpUntil(() => false, 300);
        return (vm, win);
    }

    private static bool Settled(Visual content) =>
        content.Opacity > 0.999 && Math.Abs(Pose(content).M11 - 1) < 1e-4 && Math.Abs(Pose(content).M32) < 1e-3;

    /// <summary>Artwork tab, pop-up open and settled (status card: "Searching…").</summary>
    private (MetadataViewModel vm, MetadataWindow win, MetadataWindow.SearchPopoverMotion motion, Control content) OpenArtworkSearch()
    {
        var (vm, win) = Editor("Artwork");
        vm.ArtworkSearchStatus = "Searching…";
        vm.IsArtworkSearchOpen = true;
        var motion = win.ArtworkSearchMotion!;
        var content = motion.Content!;
        Assert.True(PumpUntil(() => motion.IsOpen && Settled(content)), "pop-up never settled open");
        return (vm, win, motion, content);
    }

    [AvaloniaFact]
    public void ArtworkSearch_Open_FadesRisesAndGrows_OnTheDialogsCurve()
    {
        var (vm, win) = Editor("Artwork");
        try
        {
            vm.ArtworkSearchStatus = "Searching…";
            vm.IsArtworkSearchOpen = true;
            var motion = win.ArtworkSearchMotion!;
            var content = motion.Content!;
            Assert.True(motion.IsOpen);
            // The content root holds the pill-popover cards.
            Assert.Contains(content.GetLogicalDescendants().OfType<Border>(), b => b.Classes.Contains("pill-popover"));

            // Pinned at the hidden pose first: transparent, 8 px low, at 96 %.
            Assert.Equal(0, content.Opacity);
            Assert.Equal(0.96, Pose(content).M11, 3);
            Assert.InRange(Pose(content).M32, 7.6, 8.01);

            Dispatcher.UIThread.RunJobs();
            AssertTiming(content, PillDialogHost.OpenFadeDuration, PillDialogHost.OpenMoveDuration, PillDialogHost.OpenEase);

            var midway = false;
            var sw = Stopwatch.StartNew();
            Assert.True(PumpUntil(() =>
            {
                var m = Pose(content);
                if (content.Opacity is > 0.02 and < 0.98 && m.M11 is > 0.9601 and < 0.9999) midway = true;
                return Settled(content);
            }), $"pop-up never settled: opacity {content.Opacity}, pose {Pose(content)}");
            _o.WriteLine($"settled after {sw.ElapsedMilliseconds} ms, seen midway: {midway}");
            Assert.True(midway, "the pop-up jumped to its shown pose");
            Assert.True(vm.IsArtworkSearchOpen);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>Starts an exit: the VM flag drops at once, the pop-up reverses on the close curve
    /// while still open, then hides once it has played — exactly once.</summary>
    private void AssertAnimatedClose(MetadataWindow win, MetadataWindow.SearchPopoverMotion motion, Control content,
        Func<bool> vmFlag, Action trigger)
    {
        var closed = 0;
        motion.Flyout.Closed += (_, _) => closed++;
        var sw = Stopwatch.StartNew();
        trigger();
        Assert.False(vmFlag(), "the VM still thinks the pop-up is open");
        Assert.True(motion.IsClosing);
        Assert.True(motion.IsOpen, "the pop-up vanished without its exit");
        AssertTiming(content, PillDialogHost.CloseDuration, PillDialogHost.CloseDuration, PillDialogHost.CloseEase);

        var midway = false;
        Assert.True(PumpUntil(() =>
        {
            if (content.Opacity is > 0.02 and < 0.98 && Pose(content).M11 is > 0.9601 and < 0.9999) midway = true;
            return !motion.IsOpen;
        }, 2000), "pop-up never hid");
        _o.WriteLine($"hidden after {sw.ElapsedMilliseconds} ms at opacity {content.Opacity:0.000}, midway {midway}");
        Assert.True(sw.ElapsedMilliseconds >= 150, $"hidden after {sw.ElapsedMilliseconds} ms: the exit was skipped");
        Assert.True(midway, "the pop-up jumped to its hidden pose");
        Assert.True(content.Opacity < 0.1);
        Assert.False(motion.IsClosing);
        Assert.False(vmFlag());
        PumpUntil(() => false, 100);
        Assert.Equal(1, closed);
        Assert.True(win.IsVisible, "the editor closed with the pop-up");
    }

    [AvaloniaFact]
    public void ArtworkSearch_VmFlag_ReversesThePopup()
    {
        // A picked result (SelectArtworkResultAsync) and the VM's CloseArtworkSearch both close
        // through the flag.
        var (vm, win, motion, content) = OpenArtworkSearch();
        try { AssertAnimatedClose(win, motion, content, () => vm.IsArtworkSearchOpen, () => vm.CloseArtworkSearchCommand.Execute(null)); }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void ArtworkSearch_ClickOutside_ReversesThePopup()
    {
        var (vm, win, motion, content) = OpenArtworkSearch();
        try
        {
            var corner = new Point(130, 790); // the card's footer, well outside the pop-up
            AssertAnimatedClose(win, motion, content, () => vm.IsArtworkSearchOpen, () =>
            {
                win.MouseDown(corner, MouseButton.Left);
                win.MouseUp(corner, MouseButton.Left);
            });
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void ArtworkSearch_Escape_ReversesThePopup_AndLeavesTheEditorOpen()
    {
        var (vm, win, motion, content) = OpenArtworkSearch();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        try
        {
            AssertAnimatedClose(win, motion, content, () => vm.IsArtworkSearchOpen,
                () => win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None));
            Assert.False(host.IsClosing, "Esc closed the editor under the pop-up");
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    /// <summary>Esc with the caret inside the pop-up (on the desktop it is its own native
    /// window, so the key may never reach the editor's OnKeyDown).</summary>
    [AvaloniaFact]
    public void ArtworkSearch_EscapeInsideThePopup_ReversesIt()
    {
        var (vm, win, motion, content) = OpenArtworkSearch();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        try
        {
            var inner = content.GetLogicalDescendants().OfType<TextBlock>().First(t => t.IsEffectivelyVisible);
            AssertAnimatedClose(win, motion, content, () => vm.IsArtworkSearchOpen, () =>
                inner.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape, Source = inner }));
            Assert.False(host.IsClosing);
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void ArtworkSearch_ClosingTheEditor_TakesThePopupWithIt()
    {
        var (vm, win, motion, content) = OpenArtworkSearch();
        var closed = 0;
        motion.Flyout.Closed += (_, _) => closed++;
        win.Close();
        // Plays its exit with the card's.
        Assert.True(motion.IsClosing);
        Assert.False(vm.IsArtworkSearchOpen);
        Assert.True(PumpUntil(() => !win.IsVisible, 2000));
        PumpUntil(() => false, 100);
        Assert.False(motion.IsOpen);
        Assert.Equal(1, closed);
    }

    [AvaloniaFact]
    public void ArtworkSearch_ReopenDuringTheExit_ReversesFromWhereItIs()
    {
        var (vm, win, motion, content) = OpenArtworkSearch();
        try
        {
            vm.IsArtworkSearchOpen = false;
            PumpUntil(() => content.Opacity < 0.8, 500);
            var from = content.Opacity;
            vm.IsArtworkSearchOpen = true;   // a new search right after the dismiss
            Assert.True(content.Opacity >= from - 0.05, $"opacity jumped from {from} to {content.Opacity}");
            Assert.False(motion.IsClosing);
            Assert.True(PumpUntil(() => Settled(content)));
            // The exit's pending hide must not take the reopened pop-up down.
            PumpUntil(() => false, PillDialogHost.CloseDuration.Milliseconds + 150);
            Assert.True(motion.IsOpen);
            Assert.True(vm.IsArtworkSearchOpen);

            // And it still closes normally afterwards.
            AssertAnimatedClose(win, motion, content, () => vm.IsArtworkSearchOpen, () => vm.IsArtworkSearchOpen = false);
            // Reopening after a full close plays the entrance again.
            vm.IsArtworkSearchOpen = true;
            Assert.True(motion.IsOpen);
            Assert.Equal(0, content.Opacity);
            Assert.True(PumpUntil(() => Settled(content)));
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    [AvaloniaFact]
    public void AnimatedArtworkSearch_OpensAndClosesTheSameWay()
    {
        var (vm, win) = Editor("Animated Artwork");
        try
        {
            vm.AnimatedSearchStatus = "Searching…";
            vm.IsAnimatedArtworkSearchOpen = true;
            var motion = win.AnimatedSearchMotion!;
            var content = motion.Content!;
            Assert.True(motion.IsOpen);
            Assert.Equal(0, content.Opacity);
            Dispatcher.UIThread.RunJobs();
            AssertTiming(content, PillDialogHost.OpenFadeDuration, PillDialogHost.OpenMoveDuration, PillDialogHost.OpenEase);
            Assert.True(PumpUntil(() => Settled(content)));

            var corner = new Point(130, 790);
            AssertAnimatedClose(win, motion, content, () => vm.IsAnimatedArtworkSearchOpen, () =>
            {
                win.MouseDown(corner, MouseButton.Left);
                win.MouseUp(corner, MouseButton.Left);
            });
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }
}
