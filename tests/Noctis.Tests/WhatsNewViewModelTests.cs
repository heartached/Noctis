using System.Diagnostics;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › About › What's new: loading / cache / unavailable states of the dialog's view
/// model (a fake notes source, no network), and the "new" dot on the About button.
/// </summary>
public class WhatsNewViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "whatsnew-" + Guid.NewGuid().ToString("N"));

    public WhatsNewViewModelTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best effort */ }
    }

    internal static string Body(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WhatsNew", name + ".md"));

    internal sealed class FakeSource : IReleaseNotesSource
    {
        public Func<string, Task<ReleaseNotesDocument?>> Handler { get; set; } = _ => Task.FromResult<ReleaseNotesDocument?>(null);
        public List<string> Calls { get; } = new();

        public Task<ReleaseNotesDocument?> FetchAsync(string tag, CancellationToken ct = default)
        {
            Calls.Add(tag);
            return Handler(tag);
        }
    }

    private static readonly DateTimeOffset Now = new(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Published = new(2026, 11, 25, 5, 0, 50, TimeSpan.Zero);

    private WhatsNewCache Cache => new(_root);

    private WhatsNewViewModel Make(FakeSource source, string version = "1.7.2") =>
        new(version, source, Cache, () => Now);

    private void SeedCache(string tag, string body, DateTimeOffset fetched) =>
        Cache.Save(new WhatsNewCacheEntry { Tag = tag, PublishedAt = Published, Body = body, FetchedUtc = fetched });

    [Fact]
    public async Task FirstOpen_ShowsLoading_ThenTheNotes_AndCachesThem()
    {
        var gate = new TaskCompletionSource<ReleaseNotesDocument?>();
        var source = new FakeSource { Handler = _ => gate.Task };
        var vm = Make(source);

        Assert.True(vm.IsLoading);
        Assert.False(vm.IsUnavailable); // never flashes before the load starts

        var load = vm.LoadAsync();
        Assert.True(vm.IsLoading);
        Assert.False(vm.IsLoaded);
        Assert.False(vm.IsUnavailable);

        gate.SetResult(new ReleaseNotesDocument("v1.7.2", Published, Body("v1.7.2")));
        await load;

        Assert.False(vm.IsLoading);
        Assert.True(vm.IsLoaded);
        Assert.False(vm.IsUnavailable);
        Assert.Equal(new[] { "v1.7.2" }, source.Calls);
        Assert.Equal(new[] { ReleaseNoteKind.Added, ReleaseNoteKind.Improved, ReleaseNoteKind.Fixed }, vm.Sections.Select(s => s.Kind));
        Assert.True(vm.HasSubtitle);
        Assert.Contains("2026", vm.Subtitle);
        Assert.Equal("Noctis 1.7.2", vm.TitleVersion);

        var cached = Cache.Load("v1.7.2");
        Assert.NotNull(cached);
        Assert.Equal(Body("v1.7.2"), cached!.Body);
        Assert.Equal(Now, cached.FetchedUtc);
    }

    [Fact]
    public async Task FreshCache_ShowsAtOnce_WithoutFetching()
    {
        SeedCache("v1.7.2", Body("v1.7.2"), Now - TimeSpan.FromHours(2));
        var source = new FakeSource();
        var vm = Make(source);

        await vm.LoadAsync();

        Assert.True(vm.IsLoaded);
        Assert.Empty(source.Calls);
        Assert.Equal(3, vm.Sections.Count);
    }

    [Fact]
    public async Task DayOldCache_ShowsAtOnce_ThenRefreshesBehind()
    {
        SeedCache("v1.7.2", Body("plugin-albumroulette-v1.0.2"), Now - TimeSpan.FromDays(2));
        var gate = new TaskCompletionSource<ReleaseNotesDocument?>();
        var source = new FakeSource { Handler = _ => gate.Task };
        var vm = Make(source);

        var load = vm.LoadAsync();
        // The cached copy is up before the fetch answers.
        Assert.False(vm.IsLoading);
        Assert.True(vm.IsLoaded);
        Assert.Equal(new[] { ReleaseNoteKind.Added }, vm.Sections.Select(s => s.Kind));
        Assert.Single(source.Calls);

        gate.SetResult(new ReleaseNotesDocument("v1.7.2", Published, Body("v1.7.2")));
        await load;
        Assert.Equal(3, vm.Sections.Count);
        Assert.Equal(Now, Cache.Load("v1.7.2")!.FetchedUtc);
    }

    [Fact]
    public async Task NoReleaseForThisVersion_IsUnavailable()
    {
        var source = new FakeSource { Handler = _ => Task.FromResult<ReleaseNotesDocument?>(null) };
        var vm = Make(source, "1.7.3");

        await vm.LoadAsync();

        Assert.Equal(new[] { "v1.7.3" }, source.Calls);
        Assert.False(vm.IsLoading);
        Assert.False(vm.IsLoaded);
        Assert.True(vm.IsUnavailable);
        Assert.Empty(vm.Sections);
        Assert.False(vm.HasSubtitle);
        Assert.False(File.Exists(Cache.FilePath));
    }

    [Fact]
    public async Task Offline_WithACache_KeepsShowingIt()
    {
        SeedCache("v1.7.2", Body("v1.7.2"), Now - TimeSpan.FromDays(3));
        var source = new FakeSource { Handler = _ => throw new HttpRequestException("offline") };
        var vm = Make(source);

        await vm.LoadAsync();

        Assert.Single(source.Calls);
        Assert.True(vm.IsLoaded);
        Assert.False(vm.IsUnavailable);
        Assert.Equal(3, vm.Sections.Count);
    }

    [Fact]
    public async Task Offline_WithoutACache_OrACacheOfAnotherVersion_IsUnavailable()
    {
        SeedCache("v1.7.1", Body("v1.7.2"), Now);
        var source = new FakeSource { Handler = _ => Task.FromException<ReleaseNotesDocument?>(new HttpRequestException("offline")) };
        var vm = Make(source);

        await vm.LoadAsync();

        Assert.True(vm.IsUnavailable);
        Assert.Empty(vm.Sections);
    }

    [Fact]
    public async Task AnEmptyBody_IsUnavailable()
    {
        var source = new FakeSource { Handler = _ => Task.FromResult<ReleaseNotesDocument?>(new("v1.7.2", Published, "")) };
        var vm = Make(source);
        await vm.LoadAsync();
        Assert.True(vm.IsUnavailable);
    }

    [Fact]
    public async Task Links_GoThroughTheUrlSeam()
    {
        SeedCache("v1.7.2", Body("v1.7.2"), Now);
        var vm = Make(new FakeSource());
        var opened = new List<string>();
        vm.OpenUrl = opened.Add;
        await vm.LoadAsync();

        vm.OpenOnGitHubCommand.Execute(null);
        var issue = vm.Sections.SelectMany(s => s.Items).SelectMany(i => i.Issues).First(l => l.Number == 262);
        Assert.Equal("#262", issue.Text);
        issue.Open.Execute(null);

        Assert.Equal(new[]
        {
            "https://github.com/heartached/Noctis/releases/tag/v1.7.2",
            "https://github.com/heartached/Noctis/issues/262",
        }, opened);
    }

    // ── The "new" dot on About's What's new button ─────────────────────

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    internal static async Task<SettingsViewModel> LoadSettings(string root, string running)
    {
        var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory())
        {
            RunningVersion = running,
        };
        await vm.LoadAsync();
        return vm;
    }

    private string SettingsFile => Path.Combine(_root, "settings.json");

    private static bool WaitFor(Func<bool> condition, int ms = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    private bool FileSaysSeen(string version) =>
        File.Exists(SettingsFile) && File.ReadAllText(SettingsFile).Contains($"\"whatsNewSeenVersion\": \"{version}\"");

    [Fact]
    public async Task Dot_FreshProfile_NeverShows()
    {
        var vm = await LoadSettings(_root, "1.7.3");
        Assert.False(vm.ShowWhatsNew);
        Assert.True(WaitFor(() => FileSaysSeen("1.7.3")), File.Exists(SettingsFile) ? File.ReadAllText(SettingsFile) : "no settings.json");
    }

    [Fact]
    public async Task Dot_AfterAnUpdate_ShowsUntilOpened_ThenStaysOff()
    {
        // Settings last saved by 1.7.2 (no seen version yet: the field is new), now running 1.7.3.
        File.WriteAllText(SettingsFile, "{ \"writtenByVersion\": \"1.7.2\" }");
        var vm = await LoadSettings(_root, "1.7.3");
        Assert.True(vm.ShowWhatsNew);

        WhatsNewViewModel? shown = null;
        vm.ShowWhatsNewDialog = d => { shown = d; return Task.CompletedTask; };
        vm.WhatsNewSource = new FakeSource();
        await vm.OpenWhatsNewCommand.ExecuteAsync(null);

        Assert.NotNull(shown);
        Assert.Equal("1.7.3", shown!.Version);
        Assert.False(vm.ShowWhatsNew);
        Assert.True(WaitFor(() => FileSaysSeen("1.7.3")));

        // Next launch of the same version: no dot.
        var again = await LoadSettings(_root, "1.7.3");
        Assert.False(again.ShowWhatsNew);
    }

    [Fact]
    public async Task Dot_SeenAnOlderVersion_ShowsAgainOnTheNextUpdate()
    {
        File.WriteAllText(SettingsFile, "{ \"writtenByVersion\": \"1.7.3\", \"whatsNewSeenVersion\": \"1.7.3\" }");
        Assert.False((await LoadSettings(_root, "1.7.3")).ShowWhatsNew);
        Assert.True((await LoadSettings(_root, "1.7.4")).ShowWhatsNew);
    }
}
