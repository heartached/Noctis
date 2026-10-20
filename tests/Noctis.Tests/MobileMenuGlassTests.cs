using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Make sure it's applied to ALL of the options menus and ALL the pop-ups that have liquid glass,
/// not just some of them" (owner 10-14): every pop-up on the phone is the one glass card
/// (MenuGlass) with the ⋯ menu's pop. These tests fail when a plain Fluent pop-up (MenuFlyout,
/// Flyout, ContextMenu, Popup) comes back, when a new glass surface is drawn with its own
/// settings, or when a pop-up card's glass drifts from the menu's.
/// </summary>
public class MobileMenuGlassTests
{
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

    private static IEnumerable<string> MobileSources() =>
        Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Noctis.Mobile"), "*.*", SearchOption.AllDirectories)
            .Where(f => (f.EndsWith(".cs") || f.EndsWith(".axaml")) && !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar)
                && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar));

    // XML and C# comments out, so a remark about the old menus never trips the scan.
    private static string Code(string text) =>
        Regex.Replace(Regex.Replace(text, "<!--.*?-->", "", RegexOptions.Singleline), @"//.*", "");

    /// <summary>No Fluent pop-up anywhere in the phone app: each draws its own grey card at the
    /// screen's edge and is unknown to the shell's Back (the quality note's Back took the whole
    /// album page down with it).</summary>
    [Fact]
    public void ThePhoneApp_HasNoPlainFluentPopUps()
    {
        var banned = new Regex(@"\bMenuFlyout\b|<Flyout\b|\.Flyout>|\bnew Flyout\b|\bContextMenu\b|\bContextFlyout\b|<Popup\b|\bnew Popup\b|\bFlyoutBase\b|\.ShowAt\(");
        var hits = MobileSources()
            .SelectMany(f => Code(File.ReadAllText(f)).Split('\n').Select(line => (f, line)))
            .Where(x => banned.IsMatch(x.line))
            .Select(x => $"{Path.GetFileName(x.f)}: {x.line.Trim()}")
            .ToList();
        Assert.True(hits.Count == 0, "Show it on the glass pop menu (ShellViewModel.ShowOptions) instead:\n" + string.Join("\n", hits));
    }

    /// <summary>Every glass surface in the phone's XAML is either the floating chrome (the "liquid"
    /// and "header-glass" styles, which keep their outline) or MenuGlass itself: a new pop-up card
    /// cannot be a GlassPanel with its own settings.</summary>
    [Fact]
    public void EveryGlassPanel_IsTheChrome_OrTheMenuGlass()
    {
        var tag = new Regex(@"<controls:GlassPanel(?=[\s>])[^>]*>", RegexOptions.Singleline);
        var stray = new List<string>();
        foreach (var file in MobileSources().Where(f => f.EndsWith(".axaml") && !f.EndsWith("MenuGlass.axaml")))
            foreach (Match m in tag.Matches(Code(File.ReadAllText(file))))
                if (!Regex.IsMatch(m.Value, @"Classes=""[^""]*\b(liquid|header-glass)\b"))
                    stray.Add($"{Path.GetFileName(file)}: {Regex.Replace(m.Value, @"\s+", " ")}");
        Assert.True(stray.Count == 0, "A pop-up card is views:MenuGlass:\n" + string.Join("\n", stray));
    }

    private static readonly AvaloniaProperty[] GlassLook =
    {
        GlassPanel.CornerRadiusProperty, GlassPanel.UseAppGlassProperty, GlassPanel.IsGlassActiveProperty,
        GlassPanel.BlurRadiusProperty, GlassPanel.SaturationProperty, GlassPanel.RefractionProperty,
        GlassPanel.RefractionAmountProperty, GlassPanel.SpecularProperty, GlassPanel.SpecularStyleProperty,
        GlassPanel.BackdropLiftProperty, GlassPanel.EdgeThicknessProperty, GlassPanel.EdgeBrushProperty,
    };

    /// <summary>The ⋯ menu's card and the Add to Playlist sheet's are both MenuGlass, neither
    /// overriding any of its settings; and MenuGlass is the approved look (no outline, the top
    /// catch-light, a light blur, the white-veil lift, the sheen).</summary>
    [AvaloniaFact]
    public void ThePopUpCards_AreTheMenuGlass_Unchanged()
    {
        var reference = new MenuGlass();
        Assert.True(reference.EdgeThickness <= 0 || reference.EdgeBrush is null, "no stroke round the card");
        Assert.Equal(GlassSpecularStyle.TopCatch, reference.SpecularStyle);
        Assert.True(reference.BlurRadius <= 10);
        Assert.Equal(1, reference.BackdropLift);
        Assert.True(reference.Resources.ThemeDictionaries.Values.OfType<ResourceDictionary>()
            .All(d => d.ContainsKey("MenuGlassSheen") && d.ContainsKey("MenuGlassShadow")));

        var cards = new Dictionary<string, GlassPanel>
        {
            ["PopMenu"] = new PopMenu().FindControl<GlassPanel>("Glass")!,
            ["ContextSheet"] = new ContextSheet().FindControl<GlassPanel>("SheetGlass")!,
            ["AlbumNoteSheet"] = new AlbumNoteSheet().FindControl<GlassPanel>("NoteGlass")!,
        };
        foreach (var (owner, card) in cards)
        {
            Assert.IsType<MenuGlass>(card);
            foreach (var p in GlassLook)
                Assert.True(Equals(reference.GetValue(p), card.GetValue(p)), $"{owner}'s card overrides {p.Name}");
        }
    }

    /// <summary>
    /// Bug (10-14, emulator): over a dark brown cover the light tab bar and mini player turned peach
    /// (#F3D6C3 sampled under the tab bar): the glass lifted dark content to its luminance floor by
    /// scaling its colour up. The floating chrome now lifts it under a white veil, as the menus do,
    /// keeping its outline (owner: the tab bar and mini player keep their rim).
    /// </summary>
    [AvaloniaFact]
    public void TheChrome_LiftsDarkContentUnderAWhiteVeil_AndKeepsItsRim()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("Tone") });
        rig.Shell.Player.PlayTracks(rig.Library.TrackList, 0);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        foreach (var name in new[] { "TabCapsule", "MiniBar", "SearchBubble" })
        {
            var glass = view.FindControl<GlassPanel>(name)!;
            Assert.Equal(1, glass.BackdropLift);
            Assert.True(glass.EdgeThickness > 0, $"{name} keeps its rim");
        }
        var header = view.GetVisualDescendants().OfType<GlassPanel>().First(g => g.Classes.Contains("header-glass"));
        Assert.Equal(1, header.BackdropLift);
        window.Close();

        // The light chrome's floor (ShellView's GlassBarMinLuminance) over the cover's dark brown.
        const float floor = 0.25f, r = 0.12f, g = 0.05f, b = 0.02f;
        static float Chroma((float R, float G, float B) c) => Math.Max(c.R, Math.Max(c.G, c.B)) - Math.Min(c.R, Math.Min(c.G, c.B));
        var scaled = GlassLens.Tone(r, g, b, floor, 1, lift: 0);
        var veiled = GlassLens.Tone(r, g, b, floor, 1, lift: 1);
        Assert.True(Chroma(scaled) > 0.4f, $"before: a vivid peach-orange ({scaled})");
        Assert.True(Chroma(veiled) < Chroma(scaled) / 3, $"after: a soft tint ({veiled} vs {scaled})");
    }

    // ---- The menus themselves -------------------------------------------------------------

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(16);
        }
    }

    private static void Tap(Window window, Visual target)
    {
        window.UpdateLayout();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
    }

    private static (MobileFixtures.Rig rig, Window window, ShellView view, SongListPageViewModel list) OpenSongs()
    {
        var songs = new[] { MobileFixtures.Song("Bravo", favourite: true), MobileFixtures.Song("Alpha") };
        var rig = MobileFixtures.MakeRig(songs);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        var list = new SongListPageViewModel(rig.Shell, "Songs", () => rig.Library.TrackList);
        rig.Shell.Navigate(list);
        MobileFixtures.Land(window);
        return (rig, window, view, list);
    }

    /// <summary>The Songs list's sort button pops the glass menu out of itself (not a Fluent
    /// MenuFlyout at the screen's edge): the orders with the one in use ticked, a group band, then
    /// Ascending / Descending; a pick sorts the list and closes the menu.</summary>
    [AvaloniaFact]
    public void TheSortButton_PopsTheGlassMenu_WithTheOrderInUseTicked()
    {
        var (rig, window, view, list) = OpenSongs();
        using var _ = rig;
        var menu = view.FindControl<PopMenu>("Menu")!;
        var sort = MobileFixtures.Named<Button>(MobileFixtures.PageView(view, list), "ListSortButton");
        Tap(window, sort);
        Pump(30);

        Assert.True(menu.IsVisible);
        Assert.Same(sort, menu.Anchor);
        var options = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.Equal(new[] { "Default Order", "Title", "Artist", "Album", "Date Added", "Release Year", "Time", "Most Played",
            "Recently Played", "Ascending", "Descending" }, options.Items.Select(i => i.Label));
        Assert.Equal(new[] { "Default Order", "Ascending" }, options.Items.Where(i => i.IsChecked).Select(i => i.Label));
        Assert.Equal(new[] { "Ascending" }, options.Items.Where(i => i.StartsGroup).Select(i => i.Label));
        Assert.Equal(1, MobileFixtures.Named<GlassPanel>(menu, "Glass").Fade, 3);
        var labels = menu.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible).Select(t => t.Text).ToList();
        Assert.Contains("Recently Played", labels);

        var title = menu.GetVisualDescendants().OfType<Button>().First(b => AutomationNameOf(b) == "Title");
        Tap(window, title);
        Pump(20);
        Assert.Null(rig.Shell.PopMenu);
        Assert.False(menu.IsVisible);
        Assert.True(list.IsSorted);
        Assert.Equal(new[] { "Alpha", "Bravo" }, list.Songs.Select(t => t.Title));
        window.Close();
    }

    private static string? AutomationNameOf(Control c) => Avalonia.Automation.AutomationProperties.GetName(c);

    /// <summary>The filter button: the same glass menu, Favorites ticked once on, Show All after
    /// a band while a filter is on.</summary>
    [AvaloniaFact]
    public void TheFilterButton_PopsTheGlassMenu_AndFilters()
    {
        var (rig, window, view, list) = OpenSongs();
        using var _ = rig;
        Tap(window, MobileFixtures.Named<Button>(MobileFixtures.PageView(view, list), "ListFilterButton"));
        Pump(20);
        var options = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        var favourites = Assert.Single(options.Items, i => i.Label == "Favorites");
        Assert.False(favourites.IsChecked);
        favourites.PickCommand.Execute(null);
        Assert.Null(rig.Shell.PopMenu);
        Assert.True(list.OnlyFavourites);
        Assert.Equal(new[] { "Bravo" }, list.Songs.Select(t => t.Title));

        Tap(window, MobileFixtures.Named<Button>(MobileFixtures.PageView(view, list), "ListFilterButton"));
        Pump(20);
        options = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.True(Assert.Single(options.Items, i => i.Label == "Favorites").IsChecked);
        var showAll = Assert.Single(options.Items, i => i.Label == "Show All");
        Assert.True(showAll.StartsGroup);
        showAll.PickCommand.Execute(null);
        Assert.False(list.IsFiltered);
        window.Close();
    }

    /// <summary>Bug (10-14, emulator): Back with the album's quality note open took the album page
    /// down too: the Fluent Flyout was unknown to the shell's Back. The note is the glass menu
    /// now, and Back closes it alone; a tap off it closes it without reaching the page.</summary>
    [AvaloniaFact]
    public void TheQualityNote_IsTheGlassMenu_AndBackClosesOnlyIt()
    {
        var a = MobileFixtures.Song("A1");
        a.Codec = "FLAC";
        a.BitsPerSample = 24;
        a.SampleRate = 96000;
        var album = MobileFixtures.MakeAlbum("Record", "Band", a);
        using var rig = MobileFixtures.MakeRig(new[] { a }, new[] { album });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenAlbumCommand.Execute(album);
        MobileFixtures.Land(window);
        var page = Assert.IsType<AlbumPageViewModel>(rig.Shell.CurrentPage);
        var badge = MobileFixtures.Named<Button>(view, "QualityBadge");
        Tap(window, badge);
        Pump(20);

        var menu = view.FindControl<PopMenu>("Menu")!;
        var note = Assert.IsType<OptionsMenuViewModel>(rig.Shell.PopMenu);
        Assert.Same(badge, menu.Anchor);
        Assert.Equal(page.QualityBadge, note.Title);
        Assert.Equal(page.QualityDescription, note.Note);
        Assert.Contains(page.QualityDescription, menu.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text));

        Assert.True(rig.Shell.TryHandleBack());
        Assert.Null(rig.Shell.PopMenu);
        Assert.Same(page, rig.Shell.CurrentPage);
        window.Close();
    }

    /// <summary>The Add to Playlist sheet is the menu's glass and pop too: it rises with the card
    /// faded in, and on close it fades away with the item still in it, hiding after the motion.</summary>
    [AvaloniaFact]
    public void TheAddToPlaylistSheet_PopsUpOnTheMenuGlass_AndPopsBackWithItsContent()
    {
        var song = MobileFixtures.Song("Tone");
        using var rig = MobileFixtures.MakeRig(new[] { song });
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        rig.Shell.OpenTrackSheetCommand.Execute(song);
        rig.Shell.Sheet!.ShowPlaylistsCommand.Execute(null);
        Pump(30);

        var sheet = view.FindControl<ContextSheet>("Sheet")!;
        var glass = sheet.FindControl<GlassPanel>("SheetGlass")!;
        var pop = sheet.FindControl<Panel>("SheetPop")!;
        Assert.True(sheet.IsVisible);
        Assert.IsType<MenuGlass>(glass);
        Assert.Equal(1, glass.Fade, 3);
        Assert.Equal(1, pop.RenderTransform?.Value.M11 ?? 1, 3);

        rig.Shell.CloseSheet();
        Assert.False(sheet.IsHitTestVisible);
        Assert.True(sheet.IsVisible);
        Assert.NotNull(sheet.FindControl<ContentControl>("SheetContent")!.Content);   // never shrinks away empty
        Pump((int)(PopMenu.PopCloseMs / 16) + 8);
        Assert.False(sheet.IsVisible);
        window.Close();
    }
}
