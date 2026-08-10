using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;
using Xunit.Abstractions;

namespace Noctis.Tests;

/// <summary>
/// The mini player's "…" menu items must actually run their commands. The menu is a
/// popup with light dismiss disabled, and the window closes it from a tunnelled
/// PointerPressed handler — presses inside the popup route through that handler too,
/// so it has to leave them alone or every menu item is dead.
/// </summary>
// Building a SettingsViewModel pushes the "use embedded artwork" setting into
// MetadataService's static mirror, so this class can't run beside the tests that
// deliberately flip it.
[Collection("MetadataServiceStatics")]
public class MiniPlayerMenuClickTests
{
    private readonly ITestOutputHelper _output;

    public MiniPlayerMenuClickTests(ITestOutputHelper output) => _output = output;

    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    /// <summary>The window's XAML pulls icon geometries from the app-level dictionary,
    /// which the headless test application doesn't load.</summary>
    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://Noctis/Assets/Icons.axaml"),
        });
    }

    private static MiniPlayerViewModel MakeViewModel()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    private static Button VisibleButton(Visual root, Func<Button, bool> match) =>
        root.GetVisualDescendants().OfType<Button>().First(b => b.IsEffectivelyVisible && match(b));

    private static bool HasText(Button b, string text, bool exact = true) =>
        b.GetVisualDescendants().OfType<TextBlock>()
            .Any(t => exact ? t.Text == text : t.Text?.StartsWith(text, StringComparison.Ordinal) == true);

    /// <summary>Runs frames so a just-opened popup is laid out, composited (Button's
    /// release-time hit test reads the composition tree) and past its fade-in.</summary>
    private static void Pump(int frames = 30)
    {
        for (var i = 0; i < frames; i++)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Frame pump that also lets wall-clock time pass, so time-based transitions
    /// (the menu's 0.18s fade) actually progress.</summary>
    private static async Task PumpFor(int ms)
    {
        var end = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < end)
        {
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(8);
        }
    }

    /// <summary>Raises a real left-button press/release pair on <paramref name="target"/>,
    /// so the event travels the same route platform input builds.</summary>
    private static bool Click(Button target)
    {
        var root = (Visual?)target.GetVisualRoot() ?? target;
        var pointer = new Pointer(1, PointerType.Mouse, true);
        var point = target.Bounds.Width > 0
            ? target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), root) ?? default
            : default;

        target.RaiseEvent(new PointerPressedEventArgs(
            target, pointer, root, point, 0,
            new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
            KeyModifiers.None));
        Dispatcher.UIThread.RunJobs();
        var reachedButton = target.IsPressed;

        target.RaiseEvent(new PointerReleasedEventArgs(
            target, pointer, root, point, 0,
            new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased),
            KeyModifiers.None, MouseButton.Left));
        Dispatcher.UIThread.RunJobs();
        return reachedButton;
    }

    /// <summary>A press on the menu's own chrome (padding between items) must not reach
    /// the window's drag handler — the popup's input bubbles through the window, and the
    /// card would otherwise be dragged out from under the open menu.</summary>
    [AvaloniaFact]
    public async Task PressOnMenuChrome_DoesNotReachTheWindowDragHandler()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var more = VisibleButton(win, b => ToolTip.GetTip(b) as string == "More");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await PumpFor(400);   // let the open fade finish

            var card = win.FindControl<Border>("MenuCard")!;
            var glass = win.FindControl<Panel>("RootPanel")!;

            Assert.False(win.ShouldBeginWindowDrag(card),
                "a press on the menu card would have dragged the window");
            Assert.True(win.ShouldBeginWindowDrag(glass),
                "presses on the window's own glass must still drag it");

            // Pressing outside the menu must still dismiss it (with the fade-out).
            Assert.Equal(1, card.Opacity);
            glass.RaiseEvent(new PointerPressedEventArgs(
                glass, new Pointer(2, PointerType.Mouse, true), glass, new Point(4, 4), 0,
                new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed),
                KeyModifiers.None));
            await PumpFor(80);
            Assert.True(card.Opacity < 1, "an outside press no longer closes the menu");
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>Repeat lives in the transport row of every form (Bar, Card, LargeIcon,
    /// Lyrics) and nowhere else — the "…" menu must not carry a duplicate entry.</summary>
    [AvaloniaFact]
    public void RepeatIsInTheTransportRowOnly_NotInTheMenu()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        try
        {
            var more = VisibleButton(win, b => ToolTip.GetTip(b) as string == "More");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();

            // Every form's transport row is in the tree (only one is visible at a time),
            // so this covers Bar, Card, LargeIcon and Lyrics at once.
            var repeatButtons = win.GetVisualDescendants().OfType<Button>()
                .Where(b => ToolTip.GetTip(b) as string == "Repeat")
                .ToList();
            Assert.Equal(4, repeatButtons.Count);
            Assert.All(repeatButtons, b => Assert.NotNull(b.Command));

            var card = win.FindControl<Border>("MenuCard")!;
            var labels = card.GetVisualDescendants().OfType<TextBlock>()
                .Select(t => t.Text ?? "").ToList();
            Assert.DoesNotContain(labels, t => t.StartsWith("Repeat", StringComparison.Ordinal));
            _output.WriteLine("menu items: " + string.Join(" | ", labels));
        }
        finally
        {
            win.Close();
        }
    }

    /// <summary>The menu card paints itself from the app's theme (the same keys the app's
    /// right-click menus use). A mistyped DynamicResource key resolves to nothing and shows
    /// up as an invisible border or an unpainted card, so check them against the real app's
    /// styles — a private App instance, to leave the shared headless one alone.</summary>
    [AvaloniaTheory]
    [InlineData("AppSidebarBackground")]
    [InlineData("SystemControlForegroundBaseHighBrush")]
    [InlineData("SystemControlForegroundBaseLowBrush")]
    [InlineData("SystemControlBackgroundListLowBrush")]
    [InlineData("SystemControlBackgroundListMediumBrush")]
    public void MenuChromeBrushes_ResolveInBothThemeVariants(string key)
    {
        var app = new Noctis.App();
        app.Initialize();

        foreach (var variant in new[] { ThemeVariant.Dark, ThemeVariant.Light })
        {
            var found = app.Resources.TryGetResource(key, variant, out var value)
                        || app.Styles.TryGetResource(key, variant, out value);
            Assert.True(found && value is IBrush,
                $"'{key}' does not resolve to a brush in the {variant} theme");
        }
    }

    [AvaloniaFact]
    public void EveryMoreMenuItem_RunsItsCommand()
    {
        EnsureAppResources();
        var vm = MakeViewModel();
        var win = new MiniPlayerWindow { DataContext = vm };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        MiniPlayerForm? requestedForm = null;
        vm.FormResizeRequested += f => requestedForm = f;

        try
        {
            var popup = win.FindControl<Avalonia.Controls.Primitives.Popup>("MorePopup")!;
            var card = win.FindControl<Border>("MenuCard")!;

            void ClickMenuItem(string label)
            {
                // Re-open the menu for each item, the way a user does.
                var more = VisibleButton(win, b => ToolTip.GetTip(b) as string == "More");
                more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Pump();
                Assert.True(popup.IsOpen, "the … button did not open the menu");

                var item = VisibleButton(card, b => HasText(b, label, exact: false));
                var reached = Click(item);
                _output.WriteLine($"clicked '{label}': pressReachedButton={reached} drawer={vm.Drawer}");
                Assert.True(reached, $"the '{label}' item never received the press");
            }

            ClickMenuItem("Search");
            Assert.Equal(MiniDrawer.Search, vm.Drawer);

            ClickMenuItem("Queue");
            Assert.Equal(MiniDrawer.Queue, vm.Drawer);

            ClickMenuItem("Volume");
            Assert.Equal(MiniDrawer.Volume, vm.Drawer);

            // Last: it resizes the window into the lyrics form, which hides the item.
            ClickMenuItem("Lyrics");
            Assert.Equal(MiniPlayerForm.Lyrics, requestedForm);
        }
        finally
        {
            win.Close();
        }
    }
}
