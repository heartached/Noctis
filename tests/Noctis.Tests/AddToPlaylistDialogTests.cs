using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Add to Playlist pop-up as the rounded pill dialog (blurred app behind,
/// filled pill fields with no white outline, pill buttons like the Metadata editor), a smooth
/// animated swap between the playlist list and the inline Create New Playlist form both ways,
/// and "make sure the icons match" between the Playlists page "+ New" menu and the create
/// dialogs' headers. The dialog is wired exactly as SidebarViewModel.OpenAddToPlaylistAsync
/// wires it (CloseRequested → CloseAnimatedAsync, results through the view model's events).
/// </summary>
public class AddToPlaylistDialogTests
{
    private static readonly Color PillFill = Color.Parse("#1CFFFFFF");

    internal static void EnsureAppStyles()
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

    private static bool CardSettledOpen(PillDialogHost host) =>
        host.Card is { } card && card.Opacity > 0.999 && host.BackdropLayer!.Opacity > 0.999;

    internal static ObservableCollection<PlaylistNavItem> Lists(int n, bool withSmart = false)
    {
        var list = new ObservableCollection<PlaylistNavItem>();
        for (var i = 0; i < n; i++)
            list.Add(new PlaylistNavItem { Key = $"p{i}", Label = $"List {i}", PlaylistId = Guid.NewGuid(), MetaText = $"{i + 2} tracks · 9 min" });
        if (withSmart)
            list.Add(new PlaylistNavItem { Key = "s", Label = "Smart", PlaylistId = Guid.NewGuid(), IsSmartPlaylist = true });
        return list;
    }

    private static void AssertPillField(TextBox box)
    {
        Assert.Contains("pill-field", box.Classes);
        var chrome = box.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
        Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
        if (box.IsFocused)
        {
            // Focused: the accent ring only.
            Assert.True(box.TryFindResource("AccentColorBrush", box.ActualThemeVariant, out var accent));
            Assert.Equal(AccentTestHarness.ColorOf(accent as IBrush), AccentTestHarness.ColorOf(chrome.BorderBrush));
        }
        else
        {
            // At rest: the filled pill, no white outline (the ring is transparent).
            Assert.Equal(PillFill, AccentTestHarness.ColorOf(chrome.Background));
            Assert.Equal(0, AccentTestHarness.ColorOf(chrome.BorderBrush).A);
        }
    }

    private sealed class Caller
    {
        public int Selected, Created;
        public Guid? SelectedId;
        public string Name = "", Description = "";
    }

    private sealed record Dialog(AddToPlaylistDialogViewModel Vm, AddToPlaylistDialog Win, PillDialogHost Host, Caller Caller)
    {
        public T Find<T>(string name) where T : Control => Win.FindControl<T>(name)!;
        public Panel PaneHost => Find<Panel>("PaneHost");
        public Control[] ListSide => new Control[] { Find<Control>("ListTitle"), Find<Control>("ListPane"), Find<Control>("ListFooter") };
        public Control[] CreateSide => new Control[] { Find<Control>("CreateTitle"), Find<Control>("CreatePane"), Find<Control>("CreateFooter") };
    }

    /// <summary>Opens the dialog wired as SidebarViewModel.OpenAddToPlaylistAsync does.</summary>
    private static Dialog Open(int playlists = 2, int tracks = 3)
    {
        var vm = new AddToPlaylistDialogViewModel(Lists(playlists), tracks);
        var win = new AddToPlaylistDialog { DataContext = vm, RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        var caller = new Caller();
        vm.PlaylistSelected += (_, p) => { caller.Selected++; caller.SelectedId = p.PlaylistId; };
        vm.NewPlaylistRequested += (_, a) => { caller.Created++; caller.Name = a.Name; caller.Description = a.Description; };
        vm.CloseRequested += (_, _) => _ = win.CloseAnimatedAsync();
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        return new Dialog(vm, win, host, caller);
    }

    private static bool SideSettled(Control[] side) => side.All(c =>
        c.IsVisible && c.IsHitTestVisible && c.Opacity > 0.999
        && c.RenderTransform is TransformOperations t && Math.Abs(t.Value.M31) < 0.01);

    private static bool SideHidden(Control[] side) => side.All(c => !c.IsVisible && !c.IsHitTestVisible);

    private static bool Settled(Dialog d, bool create) =>
        !d.Win.IsPaneSwapping
        && SideSettled(create ? d.CreateSide : d.ListSide)
        && SideHidden(create ? d.ListSide : d.CreateSide)
        && double.IsNaN(d.PaneHost.Height);

    private static void CloseQuietly(Window win)
    {
        if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    // ── View model ───────────────────────────────────────────────────────

    [Fact]
    public void Vm_ConfirmCreate_Twice_FiresOnce()
    {
        // Was: NewPlaylistRequested fired on every Execute (no guard), e.g. Enter in the name
        // field again while the dialog animated out.
        var vm = new AddToPlaylistDialogViewModel(Lists(1), 1);
        var fired = 0;
        vm.NewPlaylistRequested += (_, _) => fired++;
        vm.ShowCreateCommand.Execute(null);
        vm.NewPlaylistName = "Gym";
        vm.ConfirmCreateCommand.Execute(null);
        vm.ConfirmCreateCommand.Execute(null);
        Assert.Equal(1, fired);
    }

    [Fact]
    public void Vm_ConfirmCreate_DisabledWhileNameBlank()
    {
        var vm = new AddToPlaylistDialogViewModel(Lists(1), 1);
        var changes = 0;
        vm.ConfirmCreateCommand.CanExecuteChanged += (_, _) => changes++;
        vm.ShowCreateCommand.Execute(null);
        vm.NewPlaylistName = "   ";
        Assert.False(vm.ConfirmCreateCommand.CanExecute(null));
        // Execute() ignores CanExecute: a blank name is still refused.
        var fired = 0;
        vm.NewPlaylistRequested += (_, _) => fired++;
        vm.ConfirmCreateCommand.Execute(null);
        Assert.Equal(0, fired);
        vm.NewPlaylistName = "Gym";
        Assert.True(vm.ConfirmCreateCommand.CanExecute(null));
        Assert.True(changes > 0);
    }

    [Fact]
    public void Vm_SelectPlaylist_Twice_FiresOnce_AndNothingElseAfterwards()
    {
        var lists = Lists(2);
        var vm = new AddToPlaylistDialogViewModel(lists, 1);
        var picked = new List<PlaylistNavItem>();
        var created = 0;
        vm.PlaylistSelected += (_, p) => picked.Add(p);
        vm.NewPlaylistRequested += (_, _) => created++;
        vm.SelectPlaylistCommand.Execute(vm.Playlists[0]);
        vm.SelectPlaylistCommand.Execute(vm.Playlists[1]);
        vm.ShowCreateCommand.Execute(null);
        Assert.False(vm.IsCreatingNew);
        Assert.Equal(new[] { vm.Playlists[0] }, picked);
        Assert.Equal(0, created);
    }

    [Fact]
    public void Vm_ShowCreate_StartsEmpty_Back_KeepsTextUntilReopened()
    {
        var vm = new AddToPlaylistDialogViewModel(Lists(1), 1);
        vm.ShowCreateCommand.Execute(null);
        vm.NewPlaylistName = "Road";
        vm.NewPlaylistDescription = "Trip";
        vm.CancelCreateCommand.Execute(null);
        Assert.False(vm.IsCreatingNew);
        // Not cleared on Back: the form is still fading out with the text in it.
        Assert.Equal("Road", vm.NewPlaylistName);
        vm.ShowCreateCommand.Execute(null);
        Assert.True(vm.IsCreatingNew);
        Assert.Equal("", vm.NewPlaylistName);
        Assert.Equal("", vm.NewPlaylistDescription);
    }

    [Fact]
    public void Vm_ListsOnlyManualPlaylists()
    {
        var vm = new AddToPlaylistDialogViewModel(Lists(2, withSmart: true), 1);
        Assert.Equal(2, vm.Playlists.Count);
        Assert.DoesNotContain(vm.Playlists, p => p.IsSmartPlaylist);
    }

    // ── View ─────────────────────────────────────────────────────────────

    [AvaloniaFact]
    public void Opens_InPillHost_WithPillButtons_RoundedRows_AndFilledFields()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var d = Open(playlists: 2);
            try
            {
                Assert.True(PumpUntil(() => CardSettledOpen(d.Host)), "open animation never settled");
                Assert.Equal(new CornerRadius(30), d.Host.CornerRadius);
                Assert.True(Settled(d, create: false));

                // List side: the accent pill to create, quiet Cancel, rounded filled rows.
                var createNew = d.Find<Button>("CreateNewButton");
                PillDialogHostTests.AssertSolidAccent(createNew);
                Assert.Same(d.Vm.ShowCreateCommand, createNew.Command);
                var all = d.Win.GetVisualDescendants().ToList();
                var cancel = all.OfType<Button>().Single(b => b.Command == d.Vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(PillFill, AccentTestHarness.ColorOf(cancel.Background));
                var rows = all.OfType<Button>().Where(b => b.Classes.Contains("atp-row")).ToList();
                Assert.Equal(2, rows.Count);
                foreach (var row in rows)
                {
                    Assert.Equal(new CornerRadius(16), row.CornerRadius);
                    Assert.Equal(PillFill, AccentTestHarness.ColorOf(row.Background));
                    Assert.Equal(0, row.BorderThickness.Left);
                }
                Assert.False(d.Find<Border>("EmptyState").IsVisible);

                // Create side: filled pill fields (accent ring only on the focused one), Back
                // quiet, Create solid accent and disabled until there is a name.
                d.Vm.ShowCreateCommand.Execute(null);
                Assert.True(PumpUntil(() => Settled(d, create: true)), "the create pane never settled");
                var name = d.Find<TextBox>("NameTextBox");
                Assert.True(PumpUntil(() => name.IsFocused), "the name field should take the caret");
                AssertPillField(name);
                AssertPillField(d.Find<TextBox>("DescriptionTextBox"));
                all = d.Win.GetVisualDescendants().ToList();
                var back = all.OfType<Button>().Single(b => b.Command == d.Vm.CancelCreateCommand);
                Assert.Contains("pill-secondary", back.Classes);
                var create = all.OfType<Button>().Single(b => b.Command == d.Vm.ConfirmCreateCommand);
                PillDialogHostTests.AssertSolidAccent(create);
                Assert.False(create.IsEffectivelyEnabled);
                name.Text = "Gym";
                PumpUntil(() => create.IsEffectivelyEnabled, 500);
                Assert.True(create.IsEffectivelyEnabled);
                // No white-outlined boxes anywhere in the card.
                Assert.DoesNotContain(all.OfType<TextBox>(), t => t.Classes.Contains("crisp-input"));
            }
            finally { CloseQuietly(d.Win); }
        });
    }

    [AvaloniaFact]
    public void EmptyState_IsOneQuietWell_UnderTheCreateButton()
    {
        EnsureAppStyles();
        var d = Open(playlists: 0, tracks: 1);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(d.Host)));
            var empty = d.Find<Border>("EmptyState");
            Assert.True(empty.IsVisible);
            Assert.Contains("pill-well", empty.Classes);
            Assert.False(d.Find<ScrollViewer>("PlaylistScroller").IsEffectivelyVisible);
            Assert.True(d.Find<Button>("CreateNewButton").IsEffectivelyVisible);
        }
        finally { CloseQuietly(d.Win); }
    }

    [AvaloniaFact]
    public void Swap_ToCreateAndBack_CrossFades_EasesHeight_WithoutJumps()
    {
        EnsureAppStyles();
        var d = Open(playlists: 5);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(d.Host)));
            PumpUntil(() => false, 50);
            var listHeight = d.PaneHost.Bounds.Height;
            Assert.True(listHeight > 0);

            // Forward: both sides on screen at first, the list no longer clickable.
            d.Vm.ShowCreateCommand.Execute(null);
            Assert.True(d.Win.IsPaneSwapping);
            Assert.All(d.ListSide, c => Assert.False(c.IsHitTestVisible));
            Assert.All(d.CreateSide, c => Assert.True(c.IsVisible));
            Assert.True(d.Find<Control>("ListPane").IsVisible);

            var heights = new List<double>();
            var fades = new List<double>();
            var createPane = d.Find<Control>("CreatePane");
            Assert.True(PumpUntil(() =>
            {
                heights.Add(d.PaneHost.Bounds.Height);
                fades.Add(createPane.Opacity);
                return Settled(d, create: true);
            }), "the swap to the create pane never settled");
            var createHeight = d.PaneHost.Bounds.Height;
            Assert.True(Math.Abs(listHeight - createHeight) > 4, $"list {listHeight} vs form {createHeight}: the swap needs two different heights to prove the ease");
            // Animated, not snapped: the form faded through in-between values and the area's
            // height passed through values between the two panes'.
            Assert.Contains(fades, o => o > 0.02 && o < 0.98);
            var lo = Math.Min(listHeight, createHeight) - 0.5;
            var hi = Math.Max(listHeight, createHeight) + 0.5;
            Assert.All(heights, h => Assert.InRange(h, lo, hi));
            Assert.Contains(heights, h => h > lo + 2 && h < hi - 2);
            Assert.True(PumpUntil(() => d.Find<TextBox>("NameTextBox").IsFocused));

            // Back: the mirror image, ending at the list's own height again.
            d.Vm.CancelCreateCommand.Execute(null);
            Assert.True(d.Win.IsPaneSwapping);
            heights.Clear();
            Assert.True(PumpUntil(() =>
            {
                heights.Add(d.PaneHost.Bounds.Height);
                return Settled(d, create: false);
            }), "the swap back to the list never settled");
            Assert.All(heights, h => Assert.InRange(h, lo, hi));
            Assert.Contains(heights, h => h > lo + 2 && h < hi - 2);
            PumpUntil(() => false, 30);
            Assert.Equal(listHeight, d.PaneHost.Bounds.Height, 0.5);
        }
        finally { CloseQuietly(d.Win); }
    }

    [AvaloniaFact]
    public void RapidToggling_NeverLeavesAPaneHalfVisible()
    {
        EnsureAppStyles();
        var d = Open(playlists: 3);
        try
        {
            Assert.True(PumpUntil(() => CardSettledOpen(d.Host)));
            PumpUntil(() => false, 50);
            var listHeight = d.PaneHost.Bounds.Height;

            // Turn around mid-flight several times, at different points of the swap.
            d.Vm.ShowCreateCommand.Execute(null);
            PumpUntil(() => false, 40);
            d.Vm.CancelCreateCommand.Execute(null);
            PumpUntil(() => false, 15);
            d.Vm.ShowCreateCommand.Execute(null);
            d.Vm.CancelCreateCommand.Execute(null);
            Assert.True(PumpUntil(() => Settled(d, create: false)), "list side never settled after toggling");
            PumpUntil(() => false, 300); // a stale settle must not fire afterwards
            Assert.True(Settled(d, create: false));
            Assert.Equal(listHeight, d.PaneHost.Bounds.Height, 0.5);

            // And ending on the form.
            d.Vm.ShowCreateCommand.Execute(null);
            PumpUntil(() => false, 60);
            d.Vm.CancelCreateCommand.Execute(null);
            PumpUntil(() => false, 20);
            d.Vm.ShowCreateCommand.Execute(null);
            Assert.True(PumpUntil(() => Settled(d, create: true)), "create side never settled after toggling");
            PumpUntil(() => false, 300);
            Assert.True(Settled(d, create: true));
        }
        finally { CloseQuietly(d.Win); }
    }

    [AvaloniaFact]
    public void EnterInName_Creates_Once_BlankDoesNothing()
    {
        EnsureAppStyles();
        var d = Open();
        var closed = 0;
        d.Win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(d.Host)));
        d.Vm.ShowCreateCommand.Execute(null);
        Assert.True(PumpUntil(() => Settled(d, create: true)));
        var name = d.Find<TextBox>("NameTextBox");
        Assert.True(PumpUntil(() => name.IsFocused));

        // Blank: Enter does nothing, the dialog stays.
        d.Vm.NewPlaylistName = "   ";
        d.Win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        PumpUntil(() => false, 60);
        Assert.False(d.Host.IsClosing);
        Assert.Equal(0, d.Caller.Created);

        d.Vm.NewPlaylistName = "  Gym  ";
        d.Vm.NewPlaylistDescription = " Lift ";
        d.Win.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        Assert.True(d.Host.IsClosing);
        Assert.Equal(1, d.Caller.Created);
        Assert.Equal(("Gym", "Lift"), (d.Caller.Name, d.Caller.Description));
        // A second Enter / Create while it animates out rides along.
        if (closed == 0)
        {
            d.Win.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            d.Vm.ConfirmCreateCommand.Execute(null);
        }
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(1, d.Caller.Created);
        Assert.Equal(0, d.Caller.Selected);
    }

    [AvaloniaFact]
    public void PickingARow_ReachesCaller_Once_AndClosesAnimated()
    {
        EnsureAppStyles();
        var d = Open(playlists: 3);
        var closed = 0;
        d.Win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(d.Host)));
        var rows = d.Win.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("atp-row")).ToList();
        Assert.Equal(3, rows.Count);

        rows[1].Command!.Execute(rows[1].CommandParameter);
        Assert.True(d.Host.IsClosing);
        Assert.True(d.Win.IsVisible);
        // A second click (the other half of a double click, another row) is ignored.
        rows[2].Command!.Execute(rows[2].CommandParameter);
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(1, d.Caller.Selected);
        Assert.Equal(d.Vm.Playlists[1].PlaylistId, d.Caller.SelectedId);
        Assert.Equal(0, d.Caller.Created);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Escape_ClosesLikeCancel_FromEitherPane_ExactlyOnce(bool fromCreate)
    {
        EnsureAppStyles();
        var d = Open();
        var closed = 0;
        d.Win.Closed += (_, _) => closed++;
        Assert.True(PumpUntil(() => CardSettledOpen(d.Host)));
        if (fromCreate)
        {
            d.Vm.ShowCreateCommand.Execute(null);
            Assert.True(PumpUntil(() => Settled(d, create: true)));
            d.Vm.NewPlaylistName = "Typed but cancelled";
        }

        d.Win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        Assert.True(d.Host.IsClosing);
        if (closed == 0)
        {
            d.Win.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Escape });
            d.Win.Close();
        }
        Assert.True(PumpUntil(() => closed > 0, 2000));
        PumpUntil(() => false, 250);
        Assert.Equal(1, closed);
        Assert.Equal(0, d.Caller.Created);
        Assert.Equal(0, d.Caller.Selected);
    }

    // ── "Make sure the icons match" ──────────────────────────────────────

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static readonly XNamespace Av = "https://github.com/avaloniaui";

    private static XElement NewMenuRow(XDocument mainWindow, string command) =>
        mainWindow.Descendants(Av + "MenuItem").Single(m => (string?)m.Attribute("Command") == $"{{Binding {command}}}");

    private static XElement RowIcon(XElement row) =>
        row.Element(Av + "MenuItem.Icon")!.Elements().Single();

    [AvaloniaFact]
    public void NewMenu_AndCreateDialogHeaders_ShowTheSameGlyphs()
    {
        EnsureAppStyles();
        var app = Application.Current!;
        Assert.True(app.TryFindResource("IconMaskPlaylist", out var playlistMask));
        Assert.True(app.TryFindResource("SmartPlaylistIcon", out var smartGeometry));

        // The Playlists page "+ New" menu: New Playlist uses the shared IconMaskPlaylist brush,
        // New Smart Playlist the SmartPlaylistIcon geometry; every row's glyph is 14 px.
        var main = XDocument.Load(Path.Combine(RepoRoot(), "src", "Noctis", "Views", "MainWindow.axaml"));
        var newRow = RowIcon(NewMenuRow(main, "PageCreatePlaylistCommand"));
        Assert.Equal("IconMaskPlaylist", (string?)newRow.Descendants(Av + "StaticResource").Single().Attribute("ResourceKey"));
        var smartRow = RowIcon(NewMenuRow(main, "PageCreateSmartPlaylistCommand"));
        Assert.Equal("{StaticResource SmartPlaylistIcon}", (string?)smartRow.Attribute("Data"));
        var menu = NewMenuRow(main, "PageCreatePlaylistCommand").Parent!;
        foreach (var row in menu.Elements(Av + "MenuItem"))
        {
            var icon = RowIcon(row);
            Assert.Equal("14", (string?)icon.Attribute("Width"));
            Assert.Equal("14", (string?)icon.Attribute("Height"));
        }

        // The track context menu's Add to Playlist shows the same PNG the brush holds.
        var icons = XDocument.Load(Path.Combine(RepoRoot(), "src", "Noctis.UI", "Assets", "Icons.axaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        var maskUri = (string?)icons.Descendants(Av + "ImageBrush").Single(b => (string?)b.Attribute(x + "Key") == "IconMaskPlaylist").Attribute("Source");
        var builder = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Noctis", "Helpers", "TrackContextMenuBuilder.cs"));
        Assert.Contains($"AddToPlaylist.Icon = CreatePngIcon(\"{maskUri}\")", builder);

        // The dialogs: the same resource instances in their header tiles.
        var plain = new CreatePlaylistDialog { DataContext = new CreatePlaylistDialogViewModel() };
        var smart = new CreateSmartPlaylistDialog { DataContext = new CreateSmartPlaylistDialogViewModel(new FakeLibraryService()) };
        var add = new AddToPlaylistDialog { DataContext = new AddToPlaylistDialogViewModel(Lists(1), 1) };
        try
        {
            plain.Show();
            smart.Show();
            add.Show();
            Assert.Same(playlistMask, plain.FindControl<Border>("HeaderIcon")!.OpacityMask);
            Assert.Same(playlistMask, add.FindControl<Border>("HeaderIcon")!.OpacityMask);
            Assert.Same(smartGeometry, smart.FindControl<PathIcon>("HeaderIcon")!.Data);
            // Nothing in the plain dialog's header still draws the bulleted-list geometry.
            Assert.True(app.TryFindResource("PlaylistsIcon", out var bulleted));
            Assert.DoesNotContain(plain.GetVisualDescendants().OfType<PathIcon>(), p => ReferenceEquals(p.Data, bulleted));
        }
        finally
        {
            CloseQuietly(plain);
            CloseQuietly(smart);
            CloseQuietly(add);
        }
    }
}

/// <summary>The strings that follow the UI language (Loc is process-wide, so these join the
/// Localization collection and never interleave with a culture switch).</summary>
[Collection("Localization")]
public class AddToPlaylistDialogLocalizationTests : IDisposable
{
    public AddToPlaylistDialogLocalizationTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

    [AvaloniaFact]
    public void Vm_DialogTitle_FollowsTheUiLanguage()
    {
        // Was: hard-coded "Add to Playlist" / "Create New Playlist" while the keys were translated.
        Loc.Instance.SetCulture("tr");
        var vm = new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem>(), 1);
        Assert.Equal(Loc.T("AddToPlaylist.AddPlaylist"), vm.DialogTitle);
        Assert.NotEqual("Add to Playlist", vm.DialogTitle);
        vm.ShowCreateCommand.Execute(null);
        Assert.Equal(Loc.T("AddToPlaylist.CreateNewPlaylist"), vm.DialogTitle);
    }

    [AvaloniaFact]
    public void Vm_SongCount_ReadsProperly()
    {
        Assert.Equal("Adding 1 song", new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem>(), 1).TrackCountText);
        Assert.Equal("Adding 3 songs", new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem>(), 3).TrackCountText);
        Assert.Equal("Adding 1,234 songs", new AddToPlaylistDialogViewModel(new ObservableCollection<PlaylistNavItem>(), 1234).TrackCountText);
    }

    [AvaloniaFact]
    public void View_ShowsTheSongCount_UnderTheTitle()
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var win = new AddToPlaylistDialog { DataContext = new AddToPlaylistDialogViewModel(AddToPlaylistDialogTests.Lists(1), 3) };
        try
        {
            win.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Adding 3 songs", win.FindControl<TextBlock>("TrackCountLabel")!.Text);
            Assert.Equal("Add to Playlist", win.FindControl<TextBlock>("ListTitle")!.Text);
        }
        finally
        {
            win.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }
}
