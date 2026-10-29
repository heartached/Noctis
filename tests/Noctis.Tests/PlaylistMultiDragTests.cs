using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #125: Ctrl-select several songs in a playlist, then drag one of them — the whole
/// selection should move. The press that starts the drag is a plain (no Ctrl) press, and it
/// used to clear the selection before the drag began, so only the grabbed song moved.
/// Driven through the real window input path, as a mouse would.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class PlaylistMultiDragTests
{
    public PlaylistMultiDragTests() => ArtistCredit.ResetToDefaults();

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int n = 4)
    {
        for (var i = 0; i < n; i++) { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); }
    }

    private static async Task PumpUntil(Func<bool> condition, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !condition())
        {
            Pump(1);
            await Task.Delay(5);
        }
        Pump();
    }

    private static Track T(string title) => new()
    { Id = Guid.NewGuid(), Title = title, Artist = "A", AlbumArtist = "A", Album = "Album " + title, AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + title + ".mp3" };

    private static async Task<(Window Win, ListBox List, PlaylistViewModel Vm)> Mount(params string[] titles)
    {
        EnsureAppStyles();
        var tracks = titles.Select(T).ToList();
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(tracks);
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var pl = new Playlist { Id = Guid.NewGuid(), Name = "P", TrackIds = tracks.Select(t => t.Id).ToList() };
        sidebar.Playlists.Add(pl);
        var vm = new PlaylistViewModel(pl, player, lib, persistence, sidebar);

        var view = new PlaylistView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        var list = view.FindControl<ListBox>("TrackList")!;
        await PumpUntil(() => list.GetRealizedContainers().Count() >= titles.Length);
        return (win, list, vm);
    }

    private static ListBoxItem Row(ListBox list, string title)
        => list.GetRealizedContainers().OfType<ListBoxItem>().First(c => ((Track)c.DataContext!).Title == title);

    /// <summary>A point on the row's index cell (not on a link or button).</summary>
    private static Point Grip(Window win, ListBoxItem row)
        => row.TranslatePoint(new Point(17, row.Bounds.Height - 20), win)!.Value;

    private static void CtrlClick(Window win, Point p)
    {
        win.MouseMove(p, TestKeys.Primary);
        win.MouseDown(p, MouseButton.Left, TestKeys.Primary);
        win.MouseUp(p, MouseButton.Left, TestKeys.Primary);
        Pump();
    }

    private static string Order(PlaylistViewModel vm) => string.Join(",", vm.Tracks.Select(t => t.Title));

    [AvaloniaFact]
    public async Task DraggingOneSelectedRow_MovesTheWholeSelection()
    {
        var (win, list, vm) = await Mount("a", "b", "c", "d", "e");
        CtrlClick(win, Grip(win, Row(list, "a")));
        CtrlClick(win, Grip(win, Row(list, "b")));
        Assert.Equal(2, vm.CtrlSelectedTracks.Count);

        // Plain press on "b", drag it below "e", release.
        var start = Grip(win, Row(list, "b"));
        var end = Grip(win, Row(list, "e")) + new Point(0, 30);
        win.MouseMove(start, RawInputModifiers.None);
        win.MouseDown(start, MouseButton.Left, RawInputModifiers.None);
        Pump();
        for (var i = 1; i <= 12; i++)
        {
            win.MouseMove(start + (end - start) * (i / 12.0), RawInputModifiers.LeftMouseButton);
            Pump(1);
        }
        // The card says it carries both songs.
        var pill = win.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PlaylistDragCount");
        Assert.True(pill.IsVisible);
        Assert.Equal("2 songs", ((TextBlock)pill.Child!).Text);
        win.MouseUp(end, MouseButton.Left, RawInputModifiers.None);

        await PumpUntil(() => Order(vm) != "a,b,c,d,e");
        await PumpUntil(() => Order(vm) == "c,d,e,a,b", 1500);

        Assert.Equal("c,d,e,a,b", Order(vm));
        win.Close();
    }

    /// <summary>A plain click on a row still clears the selection (now on release, so a
    /// press that turns into a drag can carry the selection).</summary>
    [AvaloniaFact]
    public async Task PlainClickOnASelectedRow_StillClearsTheSelection()
    {
        var (win, list, vm) = await Mount("a", "b", "c");
        CtrlClick(win, Grip(win, Row(list, "a")));
        CtrlClick(win, Grip(win, Row(list, "b")));
        Assert.Equal(2, vm.CtrlSelectedTracks.Count);

        var p = Grip(win, Row(list, "b"));
        win.MouseMove(p, RawInputModifiers.None);
        win.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        win.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        Pump();

        Assert.Empty(vm.CtrlSelectedTracks);
        Assert.DoesNotContain(list.GetRealizedContainers(), c => c.Classes.Contains("ctrl-selected"));
        Assert.Equal("a,b,c", Order(vm));
        win.Close();
    }

    /// <summary>GitHub #125: with the setting off (the window's plain-track-links class) the
    /// row's artist and album names drop the accent; switching it back restores it.</summary>
    [AvaloniaFact]
    public async Task PlainTrackLinks_TakeTheArtistAndAlbumOffTheAccent()
    {
        var accent = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Colors.Red);
        Application.Current!.Resources["AccentTextExactBrush"] = accent;
        var (win, list, _) = await Mount("a", "b");
        var names = Row(list, "a").GetVisualDescendants().OfType<Noctis.Controls.HighlightTextBlock>()
            .Where(t => t.Classes.Contains("accent-link")).ToList();
        Assert.Equal(2, names.Count); // artist + album
        Assert.All(names, t => Assert.Same(accent, t.Foreground));

        win.Classes.Add("plain-track-links");
        Pump();
        Assert.True(win.TryFindResource("SystemControlForegroundBaseMediumBrush", win.ActualThemeVariant, out var plain));
        Assert.All(names, t => Assert.Same(plain, t.Foreground));

        win.Classes.Remove("plain-track-links");
        Pump();
        Assert.All(names, t => Assert.Same(accent, t.Foreground));
        win.Close();
    }

    /// <summary>A plain click on a row OUTSIDE the selection clears it right away, as before.</summary>
    [AvaloniaFact]
    public async Task PlainClickOnAnUnselectedRow_ClearsTheSelection()
    {
        var (win, list, vm) = await Mount("a", "b", "c");
        CtrlClick(win, Grip(win, Row(list, "a")));
        CtrlClick(win, Grip(win, Row(list, "b")));

        var p = Grip(win, Row(list, "c"));
        win.MouseMove(p, RawInputModifiers.None);
        win.MouseDown(p, MouseButton.Left, RawInputModifiers.None);
        win.MouseUp(p, MouseButton.Left, RawInputModifiers.None);
        Pump();

        Assert.Empty(vm.CtrlSelectedTracks);
        win.Close();
    }
}
