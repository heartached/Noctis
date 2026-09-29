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
/// Audit S30: opening one playlist straight from another reuses the same PlaylistView
/// (the DataTemplate recycles it, no detach fires), and the Ctrl-selection made in the
/// first playlist survived. Rows for shared songs came up ctrl-selected in the second
/// playlist and the next right-click pushed the old songs into its menu actions.
/// </summary>
public class PlaylistViewSelectionSwapTests
{
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
    { Id = Guid.NewGuid(), Title = title, Artist = "Artist", AlbumArtist = "Artist", Album = title, AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + title + ".mp3" };

    [AvaloniaFact]
    public async Task SwitchingPlaylistInPlace_DropsPreviousCtrlSelection()
    {
        EnsureAppStyles();
        var shared = T("Shared");
        var onlyA = T("Only A");
        var onlyB = T("Only B");
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(new[] { shared, onlyA, onlyB });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var plA = new Playlist { Id = Guid.NewGuid(), Name = "A", TrackIds = new() { shared.Id, onlyA.Id } };
        var plB = new Playlist { Id = Guid.NewGuid(), Name = "B", TrackIds = new() { shared.Id, onlyB.Id } };
        sidebar.Playlists.Add(plA);
        sidebar.Playlists.Add(plB);
        var vmA = new PlaylistViewModel(plA, player, lib, persistence, sidebar);
        var vmB = new PlaylistViewModel(plB, player, lib, persistence, sidebar);

        var view = new PlaylistView { DataContext = vmA };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        var list = view.FindControl<ListBox>("TrackList")!;
        await PumpUntil(() => list.ItemCount == 2);

        // Ctrl+A in playlist A selects both of its songs.
        win.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        Pump();
        Assert.Equal(2, vmA.SelectedCount);

        // Open playlist B in the same view instance, as the recycled DataTemplate does.
        view.DataContext = vmB;
        await PumpUntil(() => list.GetRealizedContainers().Any(c => c.DataContext == onlyB));

        Assert.Equal(0, vmA.SelectedCount);
        Assert.Empty(vmA.CtrlSelectedTracks);
        var rows = list.GetRealizedContainers().OfType<ListBoxItem>().ToList();
        Assert.DoesNotContain(rows, r => r.Classes.Contains("ctrl-selected"));

        // Right-clicking a B song must not hand A's selection to B's menu actions.
        var row = rows.First(r => r.DataContext == onlyB);
        row.RaiseEvent(new ContextRequestedEventArgs { RoutedEvent = Control.ContextRequestedEvent, Source = row });
        Pump();
        Assert.Empty(vmB.CtrlSelectedTracks);

        row.ContextMenu?.Close();
        win.Close();
    }
}
