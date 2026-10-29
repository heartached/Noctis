using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Helpers;
using Noctis.Plugins;
using Noctis.Plugins.ABLoop.Looping;
using Noctis.Plugins.ABLoop.Views;
using Noctis.Plugins.AlbumRoulette.Views;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;
using Noctis.Plugins.TrueShuffle;
using Noctis.Plugins.YouTubeImport;
using Noctis.Services.Plugins;
using Noctis.Views;
using Xunit;
using static Noctis.Tests.AlbumTownPluginTests;

namespace Noctis.Tests;

/// <summary>
/// Plugin windows wear Noctis's look (owner 10-29): PluginViewWindow adds PluginViewTheme, so in
/// every official plugin's window no square CheckBox is left (each is the Settings switch), and
/// menus, flyouts and drop-down lists are frosted glass drawn in the window's overlay layer (a
/// native popup window cannot be blurred). Album Town's "Choose songs" picker sits over the town
/// blurred and dimmed, like PillDialogHost.
/// </summary>
public class PluginViewThemeTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    public void Dispose() => _box.Dispose();

    private static void Pump(int ms = 60)
    {
        var sw = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        } while (sw.ElapsedMilliseconds < ms);
    }

    private static Window Themed(Control view, double width = 1000, double height = 680)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        var window = new Window { Width = width, Height = height, Content = view, RequestedThemeVariant = ThemeVariant.Dark };
        PluginViewTheme.Apply(window);
        window.Show();
        Pump();
        return window;
    }

    /// <summary>Every CheckBox under <paramref name="root"/> is drawn as the round switch.</summary>
    private static void AssertNoSquareCheckBox(Visual root, string where)
    {
        foreach (var box in root.GetVisualDescendants().OfType<CheckBox>())
        {
            var track = box.GetVisualDescendants().OfType<Border>().SingleOrDefault(b => b.Name == "SwitchTrack");
            Assert.True(track is not null, $"{where}: a square CheckBox ({AutomationProperties.GetName(box)})");
            Assert.Equal(new CornerRadius(13), track!.CornerRadius);
            Assert.Equal(52, track.Width);
        }
    }

    /// <summary>The popup content under <paramref name="popupContent"/> is frosted glass in the window itself.</summary>
    private static void AssertFrosted(Control popupContent, Window window, string where)
    {
        Assert.Same(window, TopLevel.GetTopLevel(popupContent)); // the overlay layer, not a native popup window
        var glass = popupContent as GlassPanel ?? popupContent.GetVisualDescendants().OfType<GlassPanel>().FirstOrDefault();
        Assert.True(glass is not null, $"{where}: no GlassPanel");
        Assert.False(glass!.UseAppGlass);
        Assert.True(glass.IsGlassActive, $"{where}: the glass is off");
        Assert.True(glass.BlurRadius > 0, $"{where}: no blur");
    }

    [AvaloniaFact]
    public void PluginViewWindow_AppliesTheTheme()
    {
        _box.Approve(("dev.noctis.plugins.trueshuffle", new[] { "playback.control", "library.read", "menu.commands", "notifications" }));
        var host = _box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/plugin.json")));
        var dir = Path.Combine(_box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        var loaded = host.AddInProcess(dir, manifest, () => new TrueShufflePlugin());
        Control? shown = null;
        host.ViewPresenter = (entry, content) => { shown = content; return PluginViewWindow.Present(entry, content); };
        Assert.True(Assert.Single(loaded.Views).Open());
        var window = Assert.IsType<PluginViewWindow>(TopLevel.GetTopLevel(shown!));
        try
        {
            Assert.True(PluginViewTheme.IsApplied(window));
            Assert.Contains(PluginViewTheme.WindowClass, window.Classes);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void TrueShuffle_PillListsAndButtons_AndAFrostedDropDown()
    {
        _box.Approve(("dev.noctis.plugins.trueshuffle", new[] { "playback.control", "library.read", "menu.commands", "notifications" }));
        var host = _box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/plugin.json")));
        var dir = Path.Combine(_box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        var plugin = new TrueShufflePlugin();
        host.AddInProcess(dir, manifest, () => plugin);
        var view = plugin.CreateView();
        var window = Themed(view);
        try
        {
            AssertNoSquareCheckBox(window, "True Shuffle");
            var lists = view.GetVisualDescendants().OfType<ComboBox>().ToList();
            Assert.Equal(2, lists.Count);
            Assert.All(lists, c => Assert.Contains("pill-field", c.Classes));
            Assert.All(lists, c => Assert.Equal(new CornerRadius(999), c.CornerRadius));
            var buttons = view.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(new[] { "pill-primary", "pill-secondary", "pill-secondary" },
                buttons.Select(b => b.Classes.First(c => c.StartsWith("pill-"))));
            Assert.All(buttons, b => Assert.Equal(new CornerRadius(999), b.CornerRadius));

            var mode = lists[0];
            mode.IsDropDownOpen = true;
            Pump();
            var popup = mode.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "PART_Popup");
            Assert.True(popup.IsOpen);
            Assert.True(popup.ShouldUseOverlayLayer);
            AssertFrosted((Control)popup.Child!, window, "True Shuffle mode list");
            mode.IsDropDownOpen = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AlbumTown_RoomMenuIsFrosted_AndChooseSongsIsAPopUpOverTheBlurredTown_WithSwitches()
    {
        var host = new FakeTownHost();
        for (var i = 0; i < 12; i++)
        {
            var id = Guid.NewGuid().ToString("D");
            for (var n = 0; n < 3; n++)
                host.Tracks.Add(T($"Song {i}-{n}", album: $"Album {i}", artist: $"Artist {i}", plays: i + n, albumId: id, number: n + 1, id: $"t{i}-{n}"));
        }
        host.PlaylistList.Add(new PlaylistInfo("pl0", "Mix", null, host.Tracks.Take(9).Select(t => t.Id).ToArray()));
        var view = new AlbumTownView(host, new Random(9));
        var town = AlbumGrouping.Group(host.Tracks, out var byFile, out var byTrack);
        view.SetTown(town, byFile, Array.Empty<string>());
        view.SetApartments(host.Tracks, host.PlaylistList, host.LoadApartments(), byTrack);
        var window = Themed(view, 1400, 900);
        try
        {
            var room = view.Building.TryAdd(RoomKind.Playlist, "pl0", 0, 0)!;
            view.OpenRoomMenu(room);
            Pump(250);
            var menu = Assert.IsType<ContextMenu>(view.RoomMenuShown);
            Assert.True(menu.IsOpen);
            AssertFrosted(menu, window, "Album Town room menu");
            menu.Close();
            Pump(250);

            view.ShowPicker(room);
            Pump();
            Assert.True(view.IsPickerOpen);
            var backdrop = view.PickerLayer.Children.OfType<ContentControl>().Single(c => c.Classes.Contains("plugin-backdrop"));
            Assert.Equal(view.PickerLayer.Bounds.Size, backdrop.Bounds.Size); // the whole town behind the card
            var glass = backdrop.GetVisualDescendants().OfType<GlassPanel>().Single();
            Assert.True(glass.IsGlassActive && glass.BlurRadius > 0);
            var card = view.PickerLayer.Children.OfType<Border>().Single(b => b.Classes.Contains("plugin-dialog"));
            Assert.True(card.IsEffectivelyVisible);
            Assert.NotEmpty(card.GetVisualDescendants().OfType<CheckBox>());
            AssertNoSquareCheckBox(window, "Choose songs");

            // A click on the dimmed town closes it.
            window.MouseDown(new Point(6, view.PickerLayer.TranslatePoint(new Point(0, 6), window)!.Value.Y), Avalonia.Input.MouseButton.Left);
            window.MouseUp(new Point(6, view.PickerLayer.TranslatePoint(new Point(0, 6), window)!.Value.Y), Avalonia.Input.MouseButton.Left);
            Pump();
            Assert.False(view.IsPickerOpen);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void AlbumRoulette_ChipMenusAreFrosted()
    {
        var host = new AlbumRoulettePluginTests.FakeRouletteHost();
        var view = new RouletteView(host, new Random(3), () => new DateTime(2026, 10, 29, 12, 0, 0));
        var window = Themed(view, 440, 700);
        try
        {
            view.SetAlbums(AlbumRoulettePluginTests.Library());
            Pump();
            AssertNoSquareCheckBox(window, "Album Roulette");
            var genre = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Genre");
            genre.Flyout!.ShowAt(genre);
            Pump(250);
            var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
            AssertFrosted(presenter, window, "Album Roulette genre menu");
            genre.Flyout.Hide();
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ABLoop_And_YouTube_HaveNoSquareCheckBoxes_AndYouTubesFormatListIsFrosted()
    {
        var sim = new ABLoopPluginTests.SimPlayer();
        using var session = new LoopSession(sim, () => sim.Now, useTimer: false);
        var loop = Themed(new ABLoopView(session), ABLoopView.WindowWidth, ABLoopView.WindowHeight);
        try { AssertNoSquareCheckBox(loop, "A-B Loop"); }
        finally { loop.Close(); }

        var youtube = YouTubeImportPluginTests.NewView(Path.Combine(_box.Root, "yt"));
        var window = Themed(youtube);
        try
        {
            AssertNoSquareCheckBox(window, "Add from YouTube");
            youtube.FormatBox.IsDropDownOpen = true;
            Pump();
            var popup = youtube.FormatBox.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "PART_Popup");
            AssertFrosted((Control)popup.Child!, window, "Add from YouTube format list");
            youtube.FormatBox.IsDropDownOpen = false;
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public void ACheckBox_IsTheSwitch_AndStillTicks()
    {
        var box = new CheckBox { Content = "Show names" };
        var window = Themed(new StackPanel { Children = { box } });
        try
        {
            AssertNoSquareCheckBox(window, "plain");
            var knob = box.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "SwitchKnob");
            var off = knob.Margin.Left;
            box.IsChecked = true;
            Pump(300);
            Assert.True(knob.Margin.Left > off + 20); // the knob slid to the right
        }
        finally { window.Close(); }
    }
}
