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
/// Discord (Luwi / 1v1ctus, 2026-10-03): in a playlist the artist and album links, the
/// heart, the art play overlay and the "..." menu did nothing when clicked. Clicks are
/// sent through the real window input path (press + release), as a mouse would.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class PlaylistRowButtonClickTests
{
    public PlaylistRowButtonClickTests() => ArtistCredit.ResetToDefaults();

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
    { Id = Guid.NewGuid(), Title = title, Artist = "Rihanna; Drake", AlbumArtist = "Rihanna", Album = "ANTI", AlbumId = Guid.NewGuid(), Duration = TimeSpan.FromSeconds(200), FilePath = "C:/m/" + title + ".mp3" };

    private async Task<(Window Win, PlaylistView View, ListBoxItem Row, Track Track, PlaylistViewModel Vm)> Mount()
    {
        EnsureAppStyles();
        var work = T("Work");
        var other = T("Desperado");
        var lib = new FakeLibraryService();
        lib.TrackList.AddRange(new[] { work, other });
        var persistence = new TestPersistenceService();
        var player = new PlayerViewModel(new FakeAudioPlayer(), lib, persistence, new FakeAnimatedCoverService());
        var sidebar = new SidebarViewModel(persistence, lib);
        var pl = new Playlist { Id = Guid.NewGuid(), Name = "P", TrackIds = new() { work.Id, other.Id } };
        sidebar.Playlists.Add(pl);
        var vm = new PlaylistViewModel(pl, player, lib, persistence, sidebar);
        vm.SetViewArtistAction(name => OpenedArtists.Add(name));

        var view = new PlaylistView { DataContext = vm };
        var win = new Window { Width = 1400, Height = 900, Content = view };
        win.Show();
        var list = view.FindControl<ListBox>("TrackList")!;
        await PumpUntil(() => list.GetRealizedContainers().Any(c => c.DataContext == work));
        var row = list.GetRealizedContainers().OfType<ListBoxItem>().First(c => c.DataContext == work);
        return (win, view, row, work, vm);
    }

    private readonly System.Collections.Generic.List<string> _opened = new();
    private System.Collections.Generic.List<string> OpenedArtists => _opened;

    private static void Click(Window win, Control target)
        => ClickAt(win, target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), win)!.Value);

    private static void ClickAt(Window win, Point center)
    {
        win.MouseMove(center, RawInputModifiers.None);
        win.MouseDown(center, MouseButton.Left, RawInputModifiers.None);
        win.MouseUp(center, MouseButton.Left, RawInputModifiers.None);
        Pump();
    }

    [AvaloniaFact]
    public async Task HeartButton_Click_TogglesFavorite()
    {
        var (win, _, row, track, _) = await Mount();
        var heart = row.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("heart-btn"));
        Assert.False(track.IsFavorite);

        Click(win, heart);

        Assert.True(track.IsFavorite);
        win.Close();
    }

    [AvaloniaFact]
    public async Task OptionsButton_Click_OpensMenu()
    {
        var (win, _, row, _, _) = await Mount();
        var dots = row.GetVisualDescendants().OfType<Button>().Last();

        Click(win, dots);

        Assert.True(dots.ContextMenu?.IsOpen == true);
        dots.ContextMenu!.Close();
        win.Close();
    }

    /// <summary>A click on one name of "Rihanna, Drake" opens that artist, not the first one.</summary>
    [AvaloniaTheory]
    [InlineData("Rihanna")]
    [InlineData("Drake")]
    public async Task ArtistLink_Click_OpensTheNameUnderThePointer(string name)
    {
        var (win, _, row, _, _) = await Mount();
        var credit = row.GetVisualDescendants().OfType<Noctis.Controls.HighlightTextBlock>()
            .First(t => t.DisplayText == "Rihanna, Drake");
        var index = credit.DisplayText.IndexOf(name, StringComparison.Ordinal) + 2;
        var charBox = credit.TextLayout.HitTestTextPosition(index);
        var point = credit.TranslatePoint(new Point(charBox.X + 1, credit.Bounds.Height / 2), win)!.Value;

        ClickAt(win, point);

        Assert.Equal(new[] { name }, OpenedArtists);
        win.Close();
    }
}
