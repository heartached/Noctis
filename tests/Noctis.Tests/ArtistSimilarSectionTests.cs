using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Luwi (Discord 10-04): the artist page Overview's "Similar Artists" section folds to
/// its header like Home's sections. One setting for every artist page, expanded by
/// default; a folded section skips the Deezer fetch until it is opened.
/// </summary>
public class ArtistSimilarSectionTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

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

    /// <summary>Counts every request and answers 404, so a fetch is visible but finds nothing.</summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private SettingsViewModel CreateSettings() => new(
        new PersistenceService(_root), new FakeLibraryService(), new NoOpPlayHistoryService());

    private static PlayerViewModel Player(FakeLibraryService lib)
        => new(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());

    private static void PumpUntil(Func<bool> done, double seconds = 10)
    {
        var sw = Stopwatch.StartNew();
        while (!done() && sw.Elapsed < TimeSpan.FromSeconds(seconds))
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    [Fact]
    public void FreshInstall_IsExpanded()
    {
        Assert.True(new AppSettings().ArtistSimilarExpanded);
        Assert.True(CreateSettings().ArtistSimilarExpanded);
    }

    [AvaloniaFact]
    public async Task Toggle_FlipsTheSection_IsOneSettingForEveryArtist_AndSurvivesRestart()
    {
        var settings = CreateSettings();
        await settings.LoadAsync();
        var lib = new FakeLibraryService();
        var player = Player(lib);

        var first = new ArtistDetailViewModel("A", lib, player, settings: settings);
        Assert.True(first.IsOverviewSimilarExpanded);
        first.ToggleSimilarSectionCommand.Execute(null);
        Assert.False(first.IsOverviewSimilarExpanded);
        Assert.False(settings.ArtistSimilarExpanded);                   // written through
        first.IsActive = false;                                          // kept in history

        var second = new ArtistDetailViewModel("B", lib, player, settings: settings);
        Assert.False(second.IsOverviewSimilarExpanded);                  // opens folded
        second.ToggleSimilarSectionCommand.Execute(null);
        Assert.True(settings.ArtistSimilarExpanded);
        first.IsActive = true;                                           // Back to the first artist
        Assert.True(first.IsOverviewSimilarExpanded);

        second.ToggleSimilarSectionCommand.Execute(null);
        await settings.SaveAsync();
        var reloaded = CreateSettings();
        await reloaded.LoadAsync();
        Assert.False(reloaded.ArtistSimilarExpanded);
        Assert.False(new ArtistDetailViewModel("C", lib, player, settings: reloaded).IsOverviewSimilarExpanded);
    }

    [AvaloniaFact]
    public void Folded_SkipsTheFetch_UntilOpened()
    {
        var handler = new CountingHandler();
        var similar = new SimilarArtistsService(new HttpClient(handler), new PersistenceService(_root));
        var settings = CreateSettings();
        settings.ArtistSimilarExpanded = false;
        var lib = new FakeLibraryService();

        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, Player(lib), similar: similar, settings: settings);
        PumpUntil(() => handler.Requests > 0, seconds: 0.5); // give a stray fetch time to show up
        Assert.Equal(0, handler.Requests);
        Assert.False(vm.SimilarLoaded);
        Assert.True(vm.ShowOverviewSimilar);                             // header stays, to unfold it

        vm.ToggleSimilarSectionCommand.Execute(null);
        PumpUntil(() => vm.SimilarLoaded);
        Assert.True(vm.SimilarLoaded);
        Assert.True(handler.Requests > 0);
        Assert.False(vm.ShowOverviewSimilar);                            // Deezer had nothing: section goes
    }

    [AvaloniaFact]
    public void MountedPage_FoldedHidesTheTiles_AndOpeningLoadsThem()
    {
        EnsureAppStyles();
        var handler = new CountingHandler();
        var persistence = new PersistenceService(_root);
        var similar = new SimilarArtistsService(new HttpClient(handler), persistence);
        var settings = CreateSettings();
        settings.ArtistSimilarExpanded = false;
        var lib = new FakeLibraryService();

        var vm = new ArtistDetailViewModel("Chase Atlantic", lib, Player(lib), similar: similar, settings: settings);
        // A fresh cache entry, so opening the section loads tiles without the network.
        var cache = new SimilarArtistsCache
        {
            Artists = Enumerable.Range(1, 10).Select(i => new SimilarArtist { DeezerId = i, Name = $"Band {i}" }).ToList(),
            FetchedAtUtc = DateTime.UtcNow,
            Picker = SimilarArtistsService.PickerVersion,
        };
        File.WriteAllText(Path.Combine(persistence.DataDirectory, "artist_info", $"{vm.Artist.Id}.similar.json"),
            JsonSerializer.Serialize(cache));

        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        var toggle = view.FindControl<Button>("SimilarSectionToggle")!;
        var body = view.FindControl<CollapsibleContent>("SimilarSectionBody")!;
        Assert.True(toggle.IsEffectivelyVisible);                        // folded header still shows
        Assert.DoesNotContain("expanded", toggle.Classes);               // chevron points right
        Assert.False(body.IsVisible);
        Assert.DoesNotContain(view.GetVisualDescendants().OfType<Button>(), b => b.Classes.Contains("similar-tile"));

        toggle.Command!.Execute(null);
        PumpUntil(() => vm.HasOverviewSimilar);
        win.UpdateLayout();
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("expanded", toggle.Classes);
        Assert.True(body.IsVisible);
        var tiles = body.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("similar-tile"));
        Assert.Equal(ArtistDetailViewModel.MaxOverviewSimilar, tiles);
        Assert.True(settings.ArtistSimilarExpanded);
        Assert.Equal(0, handler.Requests);                               // served from the cache
        win.Close();
    }
}
