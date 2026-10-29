using System.Diagnostics;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Plugins;
using Noctis.Plugins.AlbumRoulette.Roulette;
using Noctis.Plugins.AlbumRoulette.Views;
using Noctis.Plugins.AlbumTown.Town;
using Noctis.Plugins.AlbumTown.Views;
using Noctis.Plugins.TrueShuffle;
using Noctis.Services.Plugins;
using Noctis.Views;
using Xunit;
using static Noctis.Tests.AlbumTownPluginTests;

namespace Noctis.Tests;

/// <summary>
/// Every pop-up in a plugin window shows its rows (10-29: Album Roulette's Genre chip opened an
/// empty frosted card). Each is opened with real input in a real <see cref="PluginViewWindow"/>
/// (so PluginViewTheme is on), then every row in view must be visible, opaque, carry non-empty
/// text in a colour that is not the card's, and sit inside the card.
/// </summary>
public class PluginPopupItemsTests : IDisposable
{
    private readonly PluginSandbox _box = new();
    private readonly List<Window> _windows = new();
    private int _hosts;

    public void Dispose()
    {
        foreach (var w in _windows) w.Close();
        _box.Dispose();
    }

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

    private sealed class ViewOf(Func<Control> create) : IPluginViewProvider
    {
        public string Name => "View";
        public Control CreateView() => create();
    }

    /// <summary>Shows <paramref name="view"/> the way Noctis does: in a PluginViewWindow.</summary>
    private PluginViewWindow Open(Control view, double width, double height)
    {
        AddToPlaylistDialogTests.EnsureAppStyles();
        Noctis.Helpers.MenuOpenAnimation.ForgetLastPress();
        var id = "dev.test.popups" + _hosts++;
        _box.Approve((id, Array.Empty<string>()));
        var host = _box.NewHost();
        var script = new ScriptedPlugin { OnInit = h => h.RegisterView(new ViewOf(() => view)) };
        var plugin = _box.AddInProcess(host, script, PluginSandbox.Manifest(id: id, apiVersion: "1.5"));
        var window = new PluginViewWindow(plugin.Views.Single(), view) { Width = width, Height = height, RequestedThemeVariant = ThemeVariant.Dark };
        _windows.Add(window);
        window.Show();
        Pump();
        return window;
    }

    private static void Click(Window window, Control target)
    {
        var p = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseMove(p);
        window.MouseDown(p, MouseButton.Left);
        window.MouseUp(p, MouseButton.Left);
    }

    private static double EffectiveOpacity(Visual v)
    {
        var o = 1.0;
        for (Visual? x = v; x is not null && x is not TopLevel; x = x.GetVisualParent()) o *= x.Opacity;
        return o;
    }

    private static Color? ColorOf(IBrush? brush) => brush is ISolidColorBrush s ? s.Color : null;

    private static Color? BackgroundOf(Control card) => card switch
    {
        GlassPanel g => ColorOf(g.Background),
        Border b => ColorOf(b.Background),
        TemplatedControl t => ColorOf(t.Background),
        _ => null,
    };

    /// <summary>
    /// <paramref name="rows"/> are the pop-up's rows inside <paramref name="card"/>: exactly
    /// <paramref name="expected"/>, and each one in view drawn with readable text inside the card.
    /// Rows (partly) scrolled out of a long list's viewport only need to exist.
    /// </summary>
    private static void AssertRowsShown(Control card, IReadOnlyList<Control> rows, int expected, string where)
    {
        Assert.True(card.IsEffectivelyVisible, $"{where}: the card is hidden");
        Assert.True(expected > 0, $"{where}: nothing expected");
        Assert.True(rows.Count == expected, $"{where}: {rows.Count} rows, expected {expected}");
        var cardBounds = new Rect(card.Bounds.Size).Inflate(0.5);
        var cardColor = BackgroundOf(card) ?? BackgroundOf(card.GetVisualDescendants().OfType<GlassPanel>().FirstOrDefault() as Control ?? card);
        var shown = 0;
        foreach (var row in rows)
        {
            if (row.FindAncestorOfType<ScrollViewer>() is { } viewer
                && row.TranslatePoint(new Point(0, 0), viewer) is { } inViewer
                && (inViewer.Y < -0.5 || inViewer.Y + row.Bounds.Height > viewer.Bounds.Height + 0.5))
                continue; // (partly) scrolled out of a long list: the viewer clips it
            shown++;
            var name = $"{where}: row {shown}";
            Assert.True(row.IsEffectivelyVisible, $"{name} is hidden");
            Assert.True(row.Bounds.Width > 20 && row.Bounds.Height > 10, $"{name} is {row.Bounds}");
            var box = new Rect(row.TranslatePoint(new Point(0, 0), card)!.Value, row.Bounds.Size);
            Assert.True(cardBounds.Contains(box), $"{name} {box} is outside the card {cardBounds}");
            Assert.True(EffectiveOpacity(row) > 0.99, $"{name} is drawn at opacity {EffectiveOpacity(row)}");
            var texts = row.GetVisualDescendants().OfType<TextBlock>()
                .Where(t => t.IsEffectivelyVisible && !string.IsNullOrWhiteSpace(t.Text)).ToList();
            Assert.True(texts.Count > 0, $"{name} has no text");
            foreach (var t in texts)
            {
                var c = ColorOf(t.Foreground);
                Assert.True(c is { A: > 0 }, $"{name}: \"{t.Text}\" has no colour");
                Assert.True(c != cardColor, $"{name}: \"{t.Text}\" is the card's colour");
                Assert.True(t.Bounds.Width > 0 && t.Bounds.Height > 0, $"{name}: \"{t.Text}\" has no size");
            }
        }
        Assert.True(shown >= Math.Min(expected, 4), $"{where}: only {shown} of {expected} rows in view");
    }

    private static IReadOnlyList<Control> MenuRows(ItemsControl menu)
        => menu.GetRealizedContainers().OfType<MenuItem>().Cast<Control>().ToList();

    // ── Album Roulette: the Genre and Decade chips (MenuFlyouts filled when they open) ──

    private static IReadOnlyList<RouletteAlbum> ManyGenres(int count)
        => AlbumCatalog.Group(Enumerable.Range(0, count).Select(i =>
            AlbumRoulettePluginTests.T($"Song {i}", album: $"Album {i}", artist: $"Artist {i}", year: 1960 + i * 2, genre: $"Genre {i:00}")).ToArray());

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void AlbumRoulette_GenreAndDecadeMenus_ShowEveryRow_EveryTimeTheyOpen(bool bigLibrary)
    {
        var host = new AlbumRoulettePluginTests.FakeRouletteHost();
        var view = new RouletteView(host, new Random(3), () => new DateTime(2026, 10, 29, 12, 0, 0));
        var window = Open(view, 440, 700);
        var albums = bigLibrary ? ManyGenres(30) : AlbumRoulettePluginTests.Library();
        view.SetAlbums(albums);
        Pump();

        foreach (var (name, expected) in new[]
                 {
                     ("Genre", 1 + RouletteFilter.Genres(albums).Count),
                     ("Decade", 1 + RouletteFilter.Decades(albums).Count),
                 })
        {
            var chip = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == name);
            for (var open = 1; open <= 2; open++)
            {
                Click(window, chip);
                Pump(600);
                var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
                Assert.Same(window, TopLevel.GetTopLevel(presenter)); // still frosted in the window
                AssertRowsShown(presenter, MenuRows(presenter), expected, $"Album Roulette {name} (open {open})");
                Assert.Contains(MenuRows(presenter).OfType<MenuItem>(), m => m.IsChecked); // the current choice is ticked
                chip.Flyout!.Hide();
                Pump(300);
            }
        }
    }

    [AvaloniaFact]
    public void AlbumRoulette_ClickingAGenre_RelabelsTheChip()
    {
        var host = new AlbumRoulettePluginTests.FakeRouletteHost();
        var view = new RouletteView(host, new Random(3), () => new DateTime(2026, 10, 29, 12, 0, 0));
        var window = Open(view, 440, 700);
        view.SetAlbums(AlbumRoulettePluginTests.Library());
        Pump();
        var chip = view.GetVisualDescendants().OfType<Button>().Single(b => AutomationProperties.GetName(b) == "Genre");
        Click(window, chip);
        Pump(600);
        var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
        var rock = MenuRows(presenter).OfType<MenuItem>().Single(m => (m.Header as string) == "Rock");
        Click(window, rock);
        Pump(400);
        Assert.Contains(chip.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Rock");
    }

    /// <summary>A third-party plugin's MenuFlyout that starts empty and fills itself in Opening.</summary>
    [AvaloniaFact]
    public void AnyPluginMenuFlyout_FilledInOpening_ShowsItsRows()
    {
        var menu = new MenuFlyout();
        menu.Opening += (_, _) =>
        {
            menu.Items.Clear();
            for (var i = 0; i < 4; i++) menu.Items.Add(new MenuItem { Header = "Item " + i });
        };
        var button = new Button
        {
            Content = "Open", Flyout = menu,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        var window = Open(button, 440, 500);
        for (var open = 1; open <= 2; open++)
        {
            Click(window, button);
            Pump(600);
            var presenter = window.GetVisualDescendants().OfType<MenuFlyoutPresenter>().Single();
            AssertRowsShown(presenter, MenuRows(presenter), 4, $"plugin MenuFlyout (open {open})");
            menu.Hide();
            Pump(300);
        }
    }

    // ── Drop-down lists ──

    private static void AssertDropDownShown(Window window, ComboBox box, string where)
    {
        Assert.True(box.ItemCount > 0, $"{where}: no items to show");
        Click(window, box);
        Pump(300);
        Assert.True(box.IsDropDownOpen, $"{where}: did not open");
        var popup = box.GetVisualDescendants().OfType<Popup>().Single(p => p.Name == "PART_Popup");
        var card = (Control)popup.Child!;
        Assert.Same(window, TopLevel.GetTopLevel(card)); // frosted in the window
        AssertRowsShown(card, box.GetRealizedContainers().OfType<ComboBoxItem>().Cast<Control>().ToList(), box.ItemCount, where);
        box.IsDropDownOpen = false;
        Pump();
    }

    [AvaloniaFact]
    public void TrueShuffle_ModeAndSourceLists_ShowEveryRow()
    {
        _box.Approve(("dev.noctis.plugins.trueshuffle", new[] { "playback.control", "library.read", "menu.commands", "notifications" }));
        var host = _box.NewHost(library: new FakeLibraryService(), appVersion: "1.6.6");
        var manifest = PluginManifest.Parse(File.ReadAllText(PluginSandbox.RepoFile("plugins/Noctis.Plugins.TrueShuffle/plugin.json")));
        var dir = Path.Combine(_box.PluginsDir, manifest.Id);
        Directory.CreateDirectory(dir);
        var plugin = new TrueShufflePlugin();
        host.AddInProcess(dir, manifest, () => plugin);
        var view = plugin.CreateView();
        var window = Open(view, 520, 640);
        var lists = view.GetVisualDescendants().OfType<ComboBox>().ToList();
        Assert.Equal(2, lists.Count);
        AssertDropDownShown(window, lists[0], "True Shuffle mode");
        AssertDropDownShown(window, lists[1], "True Shuffle source");
    }

    [AvaloniaFact]
    public void YouTube_FormatList_ShowsEveryRow()
    {
        var view = YouTubeImportPluginTests.NewView(Path.Combine(_box.Root, "yt"));
        var window = Open(view, 1000, 680);
        AssertDropDownShown(window, view.FormatBox, "Add from YouTube format");
    }

    // ── Album Town: the room menu, the Choose songs picker and the plaza search ──

    [AvaloniaFact]
    public void AlbumTown_RoomMenu_Picker_AndPlazaSearch_ShowTheirRows()
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
        var window = Open(view, 1400, 900);

        var room = view.Building.TryAdd(RoomKind.Playlist, "pl0", 0, 0)!;
        view.OpenRoomMenu(room);
        Pump(600);
        var menu = Assert.IsType<ContextMenu>(view.RoomMenuShown);
        Assert.True(menu.IsOpen);
        Assert.Same(window, TopLevel.GetTopLevel(menu));
        AssertRowsShown(menu, MenuRows(menu), menu.Items.OfType<MenuItem>().Count(), "Album Town room menu");
        menu.Close();
        Pump(300);

        view.ShowPicker(room);
        Pump(300);
        var card = view.PickerLayer.Children.OfType<Border>().Single(b => b.Classes.Contains("plugin-dialog"));
        var boxes = card.GetVisualDescendants().OfType<CheckBox>().Cast<Control>().ToList();
        AssertRowsShown(card, boxes, Math.Max(1, boxes.Count), "Album Town Choose songs");
        view.HidePicker();
        Pump();

        // Plaza search: typing drops the matches under the box.
        var search = view.GetVisualDescendants().OfType<TextBox>().Single(t => AutomationProperties.GetName(t) == "Call an album to the plaza");
        Click(window, search);
        window.KeyTextInput("Album");
        Pump(300);
        var rows = view.GetVisualDescendants().OfType<Button>()
            .Where(b => AutomationProperties.GetName(b)?.StartsWith("Call Album", StringComparison.Ordinal) == true)
            .Cast<Control>().ToList();
        var list = rows.FirstOrDefault()?.FindAncestorOfType<StackPanel>()?.FindAncestorOfType<Border>();
        Assert.NotNull(list);
        AssertRowsShown(list!, rows, Math.Max(1, rows.Count), "Album Town plaza search");
    }
}
