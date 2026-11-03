using System.Diagnostics;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-24, "Search Artist Info" (GitHub #141): the scrollbar sat on the result cards, a
/// hovered card did not light up, "Michael Jackson" listed (and seemed to pick) a 226-fan
/// namesake above the real artist, and a one-album account read "1 albums".
/// </summary>
public class ArtistInfoSearchDialogTests
{
    /// <summary>Deezer's real answer for "Michael Jackson" (search/artist, limit 25, 10-24), in
    /// its order: id, name, fans, albums.</summary>
    private static readonly (long Id, string Name, long Fans, long Albums)[] MichaelJackson =
    {
        (373427811, "Michael Jackson", 226, 1),
        (259, "Michael Jackson", 13220411, 38),
        (261065, "TK", 1344443, 102),
        (8792094, "Michael Jackson & Justin Timberlake", 483, 0),
        (1304765, "Michael Lee Jackson", 1045, 5),
        (1493370, "Michael Jackson & Siedah Garret", 138, 0),
        (148258532, "Michael Jackson Moore", 22, 1),
        (5632673, "Michael Jackson Montgomery", 124, 1),
        (420017182, "Michael Jackson do Bi", 0, 4),
        (1043816, "Michael Jackson Karaoke's band", 337, 1),
        (4986602, "Michael Joseph Jackson", 6624, 0),
        (263162741, "Michael Jackson also known as Emjay", 1, 1),
        (374060, "Simply The Best Sax: The Hits Of Michael Jackson", 1521, 1),
        (6008688, "Michel Jackson", 49, 1),
        (4969823, "Jackson Michael", 27, 1),
        (167498, "Cash Cash", 392240, 82),
        (262255, "Michael Jackson Tribute", 9362, 0),
        (419638072, "Wyatt Michael Jackson", 0, 1),
        (1278001, "David Michael Jackson", 182, 26),
        (374943371, "Ryan Michael Jackson", 1, 1),
        (75416522, "Michael R. Jackson", 52, 3),
        (54075492, "Tony Iommi, Ian Gillan, WhoCares, Steve Morris & Michael Lee Jackson", 84, 0),
        (4678848, "Michael Kurt Jackson", 140, 0),
        (4176331, "Made famous by Michael Jackson", 419, 0),
        (1300311, "Michael Gregory Jackson", 224, 6),
    };

    private static IReadOnlyList<DeezerArtistPhotos.SearchHit> Hits() =>
        MichaelJackson.Select(a => new DeezerArtistPhotos.SearchHit(a.Id, a.Name, null, null, a.Fans, a.Albums)).ToList();

    // ── Order and preselection ──

    /// <summary>Deezer put the 226-fan namesake first and "TK" third; the picker lists the
    /// same-name accounts first by fans, the rest in Deezer's order.</summary>
    [Fact]
    public void OrderForPicker_ExactNameByFansFirst_RestInDeezersOrder()
    {
        var ordered = ArtistInfoSearchViewModel.OrderForPicker(Hits(), "Michael Jackson");

        Assert.Equal(new long[] { 259, 373427811, 261065, 8792094, 1304765 }, ordered.Take(5).Select(h => h.Id));
        Assert.Equal(MichaelJackson.Length, ordered.Count);
        // Case and accents fold as in the automatic lookup.
        Assert.Equal(259, ArtistInfoSearchViewModel.OrderForPicker(Hits(), "michael jackson")[0].Id);
    }

    [AvaloniaFact]
    public async Task Picker_WithNoPin_SelectsNothing()
    {
        var vm = Picker(currentId: null);

        await vm.Search();

        Assert.Equal(259, vm.Results[0].Hit.Id);
        Assert.Null(vm.SelectedResult);
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Picker_PinnedNamesake_IsTheOnePreselected()
    {
        var vm = Picker(currentId: 373427811);

        await vm.Search();

        Assert.Same(vm.Results.Single(r => r.IsCurrent), vm.SelectedResult);
        Assert.Equal(373427811, vm.SelectedResult!.Hit.Id);
    }

    /// <summary>The automatic lookup takes the most-fanned same-name account (13M fans), not
    /// Deezer's first hit (226 fans), with or without library titles to check against.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FindAsync_MichaelJackson_TakesTheRealArtist(bool withLibraryTitles)
    {
        var topRequests = new List<string>();
        var handler = new Handler(r =>
        {
            var url = r.RequestUri!.ToString();
            if (url.StartsWith(DeezerArtistPhotos.SearchUrl, StringComparison.Ordinal))
            {
                // FindAsync asks for 10; the photo URLs only need to be real-looking.
                var items = MichaelJackson.Take(DeezerArtistPhotos.SearchLimit).Select(a =>
                    $$"""{ "id": {{a.Id}}, "name": "{{a.Name}}", "nb_fan": {{a.Fans}}, "nb_album": {{a.Albums}}, "picture_xl": "https://images.example/{{a.Id}}.jpg" }""");
                return Json("{ \"data\": [" + string.Join(",", items) + "] }");
            }
            topRequests.Add(url);
            if (url.StartsWith($"{DeezerArtistPhotos.ArtistUrl}/259/top", StringComparison.Ordinal))
                return Json("""{ "data": [ { "title": "Billie Jean" }, { "title": "Thriller" } ] }""");
            if (url.StartsWith($"{DeezerArtistPhotos.ArtistUrl}/373427811/top", StringComparison.Ordinal))
                return Json("""{ "data": [ { "title": "Run It Up! (Remix)" } ] }"""); // measured 10-24
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        var deezer = new DeezerArtistPhotos(new HttpClient(handler));
        Func<IReadOnlySet<string>>? titles = withLibraryTitles
            ? () => new HashSet<string> { DeezerArtistPhotos.TitleKey("Thriller"), DeezerArtistPhotos.TitleKey("Beat It") }
            : null;

        var lookup = await deezer.FindAsync("Michael Jackson", titles);

        Assert.Equal("https://images.example/259.jpg", lookup.Match?.ImageUrl);
        Assert.Equal(13220411, lookup.Match?.Fans);
        Assert.Equal(withLibraryTitles ? 2 : 0, topRequests.Count);
    }

    // ── Row text ──

    [AvaloniaFact]
    public void RowDetail_SaysOneAlbumAndOneFan()
    {
        var one = new ArtistInfoSearchRow(new DeezerArtistPhotos.SearchHit(1, "A", null, null, 1, 1), false);
        var many = new ArtistInfoSearchRow(new DeezerArtistPhotos.SearchHit(2, "B", null, null, 1500, 38), false);

        Assert.Equal("1 fan · 1 album", one.Detail);
        Assert.Equal(
            $"{1500.ToString("N0", System.Globalization.CultureInfo.CurrentCulture)} fans · 38 albums",
            many.Detail);
    }

    // ── The dialog ──

    /// <summary>The cards stop short of the vertical scrollbar (its own lane, as Find online),
    /// and a hovered card lifts to the fields' pointer-over tone while the others stay put.</summary>
    [AvaloniaFact]
    public void Dialog_RowsLeaveTheScrollbarLane_AndLightUpOnHover()
    {
        EnsureAppStyles();
        var vm = Picker(currentId: null);
        var win = new ArtistInfoSearchDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        try
        {
            Settle(win);
            Assert.True(PumpUntil(() => Cards(win).Count > 2), "results never showed");
            var list = win.GetVisualDescendants().OfType<ListBox>().Single(l => l.Classes.Contains("ai-results"));
            Assert.Null(list.SelectedItem);
            Assert.DoesNotContain(list.GetVisualDescendants().OfType<ListBoxItem>(), i => i.IsSelected);

            var bar = list.GetVisualDescendants().OfType<ScrollBar>()
                .Single(s => s.Orientation == Orientation.Vertical && s.IsEffectivelyVisible);
            var barLeft = bar.TranslatePoint(new Point(0, 0), list)!.Value.X;
            foreach (var card in Cards(win))
            {
                var right = card.TranslatePoint(new Point(card.Bounds.Width, 0), list)!.Value.X;
                Assert.True(right <= barLeft + 0.5, $"a card runs under the scrollbar (card right {right}, bar left {barLeft})");
            }

            var cards = Cards(win);
            Assert.Equal(Color.Parse("#1CFFFFFF"), ColorOf(cards[1].Background));
            var p = cards[1].TranslatePoint(new Point(cards[1].Bounds.Width / 2, cards[1].Bounds.Height / 2), win)!.Value;
            win.MouseMove(p);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Color.Parse("#26FFFFFF"), ColorOf(cards[1].Background));
            Assert.Equal(Color.Parse("#1CFFFFFF"), ColorOf(cards[0].Background));
        }
        finally { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    // ── Harness ──

    private static ArtistInfoSearchViewModel Picker(long? currentId) =>
        new("Michael Jackson", currentId,
            (_, _) => Task.FromResult(Hits()),
            thumbnail: null,
            _ => Task.CompletedTask);

    private static List<Border> Cards(Window win) =>
        win.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("ai-row") && b.IsEffectivelyVisible).ToList();

    private static Color ColorOf(IBrush? brush) => Assert.IsAssignableFrom<ISolidColorBrush>(brush).Color;

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

    private static void Settle(Window win)
    {
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999), "open animation never settled");
        for (var i = 0; i < 4; i++) { Dispatcher.UIThread.RunJobs(); win.UpdateLayout(); }
    }

    private static HttpResponseMessage Json(string json)
        => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
