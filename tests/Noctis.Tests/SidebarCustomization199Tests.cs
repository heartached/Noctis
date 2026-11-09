using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #199: Settings › General › Sidebar lets the user hide sidebar pages (never Settings),
/// reorder them and pick the start page, which falls back to Home when hidden or unavailable.
/// </summary>
public class SidebarCustomization199Tests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", "sidebar199-" + Guid.NewGuid().ToString("N"));

    public SidebarCustomization199Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static List<string> Keys(SidebarViewModel vm) => vm.NavItems.Select(i => i.Key).ToList();

    // ── Ordering / visibility rules ──────────────────────────

    [Fact]
    public void MergePageOrder_Empty_IsTheBuiltInOrder()
    {
        Assert.Equal(SidebarViewModel.DefaultPageOrder, SidebarViewModel.MergePageOrder(null));
        Assert.Equal(SidebarViewModel.DefaultPageOrder, SidebarViewModel.MergePageOrder(Array.Empty<string>()));
    }

    [Fact]
    public void MergePageOrder_KeepsUserOrder_DropsUnknownAndRepeats_InsertsMissingAfterTheirPredecessor()
    {
        var merged = SidebarViewModel.MergePageOrder(new[] { "albums", "bogus", "home", "albums", "settings", "songs" });

        Assert.Equal(SidebarViewModel.DefaultPageOrder.Count, merged.Count);
        Assert.Equal(merged.Count, merged.Distinct().Count());
        Assert.DoesNotContain("bogus", merged);
        // User order kept for the saved keys.
        Assert.True(merged.IndexOf("albums") < merged.IndexOf("home"));
        Assert.True(merged.IndexOf("home") < merged.IndexOf("settings"));
        Assert.True(merged.IndexOf("settings") < merged.IndexOf("songs"));
        // Artists (missing) follows Albums, its built-in predecessor.
        Assert.Equal(merged.IndexOf("albums") + 1, merged.IndexOf("artists"));
    }

    [Fact]
    public void VisiblePages_SkipsHiddenAndAbsent_ButNeverSettings()
    {
        var present = new[] { "home", "songs", "albums", "settings" };
        var visible = SidebarViewModel.VisiblePages(present, new[] { "settings", "albums" }, new[] { "songs", "settings" });

        // Home and Songs (not in the saved list, nothing before them placed) go first.
        Assert.Equal(new[] { "home", "settings", "albums" }, visible);
    }

    [Theory]
    [InlineData("albums", "", "albums")]
    [InlineData("albums", "albums", "home")]
    [InlineData("settings", "", "home")]
    [InlineData("bogus", "", "home")]
    [InlineData("", "", "home")]
    [InlineData("inbox", "", "home")] // not available
    public void ResolveStartPage_FallsBackToHome(string key, string hidden, string expected)
    {
        var hiddenKeys = hidden.Length == 0 ? Array.Empty<string>() : new[] { hidden };
        Assert.Equal(expected, SidebarViewModel.ResolveStartPage(key, hiddenKeys, k => k != "inbox"));
    }

    // ── Sidebar rows ─────────────────────────────────────────

    /// <summary>A full saved order, as the Settings card writes it.</summary>
    private static readonly string[] UserOrder =
    {
        "albums", "home", "inbox", "songs", "artists", "folders", "playlists",
        "visualizer", "lyricsstudio", "server", "cd", "settings",
    };

    [Fact]
    public void Sidebar_AppliesOrderAndHidden_InPlace_AndKeepsOptionalPagesInTheirUserSlot()
    {
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService());
        var albumsItem = vm.NavItems.First(i => i.Key == "albums");
        var layoutChanges = 0;
        vm.NavItemsLayoutChanged += () => layoutChanges++;

        vm.ApplyUserLayout(UserOrder, new[] { "visualizer", "settings", "artists" });

        var keys = Keys(vm);
        Assert.Equal(new[] { "albums", "home", "songs" }, keys.Take(3));
        Assert.DoesNotContain("visualizer", keys);
        Assert.DoesNotContain("artists", keys);
        Assert.Contains("settings", keys); // never hidden
        Assert.Same(albumsItem, vm.NavItems[0]);
        Assert.True(layoutChanges > 0);

        // Inbox turns on: it lands in its user slot (after Home), not its built-in one.
        vm.SetInboxEnabled(true);
        keys = Keys(vm);
        Assert.Equal(new[] { "albums", "home", "inbox", "songs" }, keys.Take(4));

        // A hidden page stays hidden when its feature toggles, yet still counts as present.
        vm.SetVisualizerSectionVisible(false);
        vm.SetVisualizerSectionVisible(true);
        Assert.DoesNotContain("visualizer", Keys(vm));
        Assert.True(vm.IsVisualizerSectionVisible);

        // Hiding the Inbox entry doesn't make the page "gone" (MainWindow would kick the user off it).
        vm.ApplyUserLayout(UserOrder, new[] { "inbox" });
        Assert.DoesNotContain("inbox", Keys(vm));
        Assert.True(vm.IsInboxSectionVisible);

        // Back to defaults: the built-in order again.
        vm.ApplyUserLayout(null, null);
        Assert.Equal(SidebarViewModel.DefaultPageOrder.Where(k => k is not ("server" or "cd")), Keys(vm));
    }

    [Fact]
    public void Sidebar_ReportsAvailabilityChanges()
    {
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService());
        var seen = new List<(string, bool)>();
        vm.PageAvailabilityChanged += (k, on) => seen.Add((k, on));

        vm.SetServerSectionVisible(true);
        vm.SetServerSectionVisible(true);
        vm.SetVisualizerSectionVisible(false);

        Assert.Equal(new[] { ("server", true), ("visualizer", false) }, seen);
        Assert.True(vm.IsPagePresent("server"));
        Assert.False(vm.IsPagePresent("cd"));
    }

    // ── Settings card + persistence ──────────────────────────

    [AvaloniaFact]
    public async Task Settings_HideMoveAndStartPage_SurviveReload_AndResetRestoresDefaults()
    {
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal(SidebarViewModel.DefaultPageOrder, vm.SidebarPageOrderKeys);
        Assert.Empty(vm.SidebarHiddenPageKeys);
        Assert.Equal("home", vm.GetDefaultPageKey());
        // Unavailable optional pages are not offered as a start page; Settings never is.
        Assert.DoesNotContain(vm.StartPageOptions, o => o.Key is "settings" or "server" or "cd" or "inbox");

        var layoutEvents = 0;
        vm.SidebarLayoutChanged += (_, _) => layoutEvents++;

        var albums = vm.SidebarPages.First(p => p.Key == "albums");
        vm.MoveSidebarPageUpCommand.Execute(albums);
        vm.MoveSidebarPageUpCommand.Execute(albums);
        Assert.Equal(new[] { "albums", "home", "songs" }, vm.SidebarPageOrderKeys.Take(3));
        Assert.False(albums.CanMoveUp);

        vm.StartPageChoice = vm.StartPageOptions.First(o => o.Key == "albums");
        Assert.Equal("albums", vm.StartPage);
        Assert.Equal("albums", vm.GetDefaultPageKey());

        vm.SidebarPages.First(p => p.Key == "lyricsstudio").IsShown = false;
        Assert.Equal(new[] { "lyricsstudio" }, vm.SidebarHiddenPageKeys);
        Assert.DoesNotContain(vm.StartPageOptions, o => o.Key == "lyricsstudio");
        Assert.True(layoutEvents >= 3);

        // Settings cannot be hidden.
        Assert.False(vm.SidebarPages.First(p => p.Key == "settings").CanHide);

        await vm.SaveAsync();
        var saved = await new PersistenceService(_root).LoadSettingsAsync();
        Assert.Equal("albums", saved.SidebarPageOrder[0]);
        Assert.Equal(new[] { "lyricsstudio" }, saved.SidebarHiddenPages);
        Assert.Equal("albums", saved.StartPage);

        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.Equal(vm.SidebarPageOrderKeys, reloaded.SidebarPageOrderKeys);
        Assert.Equal(new[] { "lyricsstudio" }, reloaded.SidebarHiddenPageKeys);
        Assert.Equal("albums", reloaded.GetDefaultPageKey());

        // Hiding the start page falls back to Home.
        reloaded.SidebarPages.First(p => p.Key == "albums").IsShown = false;
        Assert.Equal("home", reloaded.StartPage);
        Assert.Equal("home", reloaded.GetDefaultPageKey());

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal(SidebarViewModel.DefaultPageOrder, reloaded.SidebarPageOrderKeys);
        Assert.Empty(reloaded.SidebarHiddenPageKeys);
        Assert.Equal("home", reloaded.StartPage);
    }

    [AvaloniaFact]
    public async Task Settings_StartPageOnAPageWhoseFeatureIsOff_OpensHome_AndMovesStepOverUnlistedRows()
    {
        await new PersistenceService(_root).SaveSettingsAsync(new AppSettings { StartPage = "server" });
        var vm = CreateSettings();
        await vm.LoadAsync();
        Assert.Equal("server", vm.StartPage);
        Assert.Equal("home", vm.GetDefaultPageKey()); // no server configured

        vm.SetSidebarPageAvailable("server", true);
        Assert.Equal("server", vm.GetDefaultPageKey());

        // Lyrics Studio's next row down is Server (now listed), then Audio CD (not listed).
        var studio = vm.SidebarPages.First(p => p.Key == "lyricsstudio");
        vm.MoveSidebarPageDownCommand.Execute(studio);
        var keys = vm.SidebarPageOrderKeys.ToList();
        Assert.Equal(keys.IndexOf("server") + 1, keys.IndexOf("lyricsstudio"));
        // Next down is Audio CD (not listed): the move steps over it, past Settings.
        vm.MoveSidebarPageDownCommand.Execute(studio);
        keys = vm.SidebarPageOrderKeys.ToList();
        Assert.Equal(new[] { "server", "cd", "settings", "lyricsstudio" }, keys.TakeLast(4));
        Assert.False(studio.CanMoveDown);
    }
}
