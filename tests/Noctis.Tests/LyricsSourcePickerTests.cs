using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Issue #113: Search Lyrics with a source picker. Any source can be searched by name, "Auto"
/// marks the answer the automatic lookup would pick, and a picked answer is shown and kept the
/// way "Try alternate" keeps one — never over the user's own sidecar.
/// </summary>
public class LyricsSourcePickerTests
{
    // ── Harness ──

    private sealed class StubLrcLib : ILrcLibService
    {
        public Func<Task<LrcLibResult?>> GetImpl = () => Task.FromResult<LrcLibResult?>(null);
        public int Calls;

        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Calls);
            return GetImpl();
        }

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

    private sealed class SettingsPersistence : TestPersistenceService
    {
        public AppSettings Settings { get; } = new() { NetEaseEnabled = false };
        public override Task<AppSettings> LoadSettingsAsync() => Task.FromResult(Settings);
    }

    /// <summary>An extra source whose answer the test controls; records every ask and its token.</summary>
    private sealed class StubSource
    {
        public Func<CancellationToken, Task<LrcLibResult?>> Impl = _ => Task.FromResult<LrcLibResult?>(null);
        public int Calls;
        public CancellationToken LastToken;

        public OnlineLyricsSource As(string name, Func<AppSettings, bool> enabled) => new(name, enabled, (_, _, _, ct) =>
        {
            Interlocked.Increment(ref Calls);
            LastToken = ct;
            return Impl(ct);
        });
    }

    private const string Elrc = "[00:01.00]<00:01.00>word <00:01.50>timed<00:02.00>";
    private const string Lrc = "[00:01.00]line timed";

    private static LrcLibResult Result(string? synced = null, string? plain = null) => new()
    {
        TrackName = "Test Song",
        ArtistName = "Test Artist",
        Duration = 200,
        SyncedLyrics = synced,
        PlainLyrics = plain ?? (synced == null ? null : LyricsTextHelper.StripTimestamps(synced)),
    };

    private static (LyricsViewModel Vm, Track Track, SettingsPersistence Persistence) Mount(
        StubLrcLib lrcLib, params OnlineLyricsSource[] extras)
    {
        var persistence = new SettingsPersistence();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), new FakeLibraryService(),
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsViewModel(player, lrcLib, new StubNetEase(), new StubMetadata(), persistence, new FakeLibraryService())
        {
            ExtraLyricsSources = extras,
            // Trash = delete, so a replaced .elrc is gone and nothing reaches the real recycle bin.
            TrashSidecarFile = path => { File.Delete(path); return true; },
        };
        var track = new Track
        {
            Title = "Test Song",
            Artist = "Test Artist",
            Duration = TimeSpan.FromSeconds(200),
            FilePath = Path.Combine(Path.GetTempPath(), $"noctis-113-{Guid.NewGuid():N}.mp3"),
        };
        player.CurrentTrack = track;
        return (vm, track, persistence);
    }

    private static async Task PumpUntilAsync(Func<bool> done, int budgetMs = 5000)
    {
        var deadline = Environment.TickCount64 + budgetMs;
        while (Environment.TickCount64 < deadline && !done())
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static string Sidecar(Track track, string ext) => Path.ChangeExtension(track.FilePath, ext);

    private static void Cleanup(Track track)
    {
        foreach (var ext in new[] { ".lrc", ".elrc" })
        {
            var path = Sidecar(track, ext);
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            AppWrittenSidecarRegistry.Default.Remove(path);
        }
    }

    // ── Source picker ──

    [AvaloniaFact]
    public async Task SearchSources_AsksANamedSourceEvenWhenSwitchedOff_AndReportsFailuresPerSource()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromException<LrcLibResult?>(new LyricsProviderException("LRCLIB", new TimeoutException())) };
        var musixmatch = new StubSource { Impl = _ => Task.FromResult<LrcLibResult?>(Result(synced: Elrc)) };
        var (vm, _, _) = Mount(lrcLib, musixmatch.As("Musixmatch", s => s.MusixmatchEnabled));

        var hits = await vm.SearchSourcesAsync(new[] { "LRCLIB", "Musixmatch" }, "Test Artist", "Test Song", 200, "", CancellationToken.None);

        Assert.Equal(new[] { "LRCLIB", "Musixmatch" }, hits.Select(h => h.Source));
        Assert.True(hits[0].Errored);
        Assert.Equal(Elrc, hits[1].Result?.SyncedLyrics);
        Assert.Equal(new[] { "LRCLIB", "NetEase", "Musixmatch" }, vm.AllSourceNames());
        Assert.Equal(new[] { "LRCLIB" }, vm.AutoSourceNames(new AppSettings { NetEaseEnabled = false }));
    }

    [AvaloniaFact]
    public async Task PickerAuto_MarksAndPreselectsTheBestAnswer()
    {
        var track = new Track { Title = "Test Song", Artist = "Test Artist" };
        IReadOnlyList<string>? asked = null;
        var vm = new LyricsSearchViewModel(track, new[] { "LRCLIB", "Kugou" },
            () => Task.FromResult<IReadOnlyList<string>>(new[] { "LRCLIB", "Kugou" }),
            (sources, _, _, _) =>
            {
                asked = sources;
                return Task.FromResult<IReadOnlyList<LyricsSourceHit>>(new[]
                {
                    new LyricsSourceHit("LRCLIB", Result(synced: Lrc), false),
                    new LyricsSourceHit("Kugou", Result(synced: Elrc), false),
                });
            },
            (_, _) => { });

        await vm.SearchAsync();

        Assert.Equal(new[] { "LRCLIB", "Kugou" }, asked);
        Assert.Equal(2, vm.Results.Count);
        Assert.Equal("Kugou", vm.SelectedResult?.Source);
        Assert.True(vm.SelectedResult?.IsBest);
        Assert.False(vm.Results[0].IsBest);
        Assert.Equal("word timed", vm.PreviewText.Trim());
        Assert.True(vm.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void PickerSingleSource_AsksOnlyThatSource_AndApplyHandsBackItsAnswer()
    {
        var track = new Track { Title = "Test Song", Artist = "Test Artist" };
        IReadOnlyList<string>? asked = null;
        (LrcLibResult Result, string Source)? applied = null;
        var vm = new LyricsSearchViewModel(track, new[] { "LRCLIB", "Kugou" },
            () => Task.FromResult<IReadOnlyList<string>>(new[] { "LRCLIB", "Kugou" }),
            (sources, _, _, _) =>
            {
                asked = sources;
                return Task.FromResult<IReadOnlyList<LyricsSourceHit>>(new[] { new LyricsSourceHit(sources[0], Result(plain: "plain words"), false) });
            },
            (result, source) => applied = (result, source));
        var closed = false;
        vm.Closed += (_, _) => closed = true;

        vm.SelectedSource = "Kugou"; // picking a source searches it (the stub answers synchronously)

        Assert.Equal(new[] { "Kugou" }, asked);
        Assert.False(vm.SelectedResult?.IsBest); // only Auto marks a best answer
        vm.ApplyCommand.Execute(null);
        Assert.Equal("Kugou", applied?.Source);
        Assert.Equal("plain words", applied?.Result.PlainLyrics);
        Assert.True(closed);
    }

    [AvaloniaFact]
    public async Task PickerAuto_WithEverySourceSwitchedOff_SaysSo()
    {
        var vm = new LyricsSearchViewModel(new Track { Title = "T" }, new[] { "LRCLIB" },
            () => Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>()),
            (_, _, _, _) => throw new InvalidOperationException("must not search"),
            (_, _) => { });

        await vm.SearchAsync();

        Assert.Empty(vm.Results);
        Assert.False(string.IsNullOrEmpty(vm.StatusText));
        Assert.False(vm.ApplyCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task ApplySearchedLyrics_ShowsThePick_AndASwitchBackToLineSyncedDropsTheAppsElrc()
    {
        var lrcLib = new StubLrcLib { GetImpl = () => Task.FromResult<LrcLibResult?>(Result(synced: Lrc)) };
        var (vm, track, _) = Mount(lrcLib);

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricsSourceName == "LRCLIB" && File.Exists(Sidecar(track, ".lrc")));

            vm.ApplySearchedLyrics(track, Result(synced: Elrc), "Kugou");
            Assert.Equal("Kugou", vm.LyricsSourceName);
            Assert.False(vm.HasAlternateLyrics);
            await PumpUntilAsync(() => File.Exists(Sidecar(track, ".elrc")));
            Assert.True(File.Exists(Sidecar(track, ".elrc")));
            Assert.Equal("[00:01.00]word timed", File.ReadAllText(Sidecar(track, ".lrc")).Trim());

            vm.ApplySearchedLyrics(track, Result(synced: Lrc), "LRCLIB");
            await PumpUntilAsync(() => !File.Exists(Sidecar(track, ".elrc")));
            Assert.False(File.Exists(Sidecar(track, ".elrc")));
            Assert.Equal(Lrc, File.ReadAllText(Sidecar(track, ".lrc")).Trim());
        }
        finally { Cleanup(track); }
    }

    [AvaloniaFact]
    public async Task ApplySearchedLyrics_NeverOverwritesTheUsersOwnLrc()
    {
        var (vm, track, _) = Mount(new StubLrcLib());
        var lrcPath = Sidecar(track, ".lrc");
        File.WriteAllText(lrcPath, "[00:05.00]my own timing");

        try
        {
            vm.SearchLyricsForTrack(track);
            await PumpUntilAsync(() => vm.LyricLines.Any(l => l.Text == "my own timing"));

            vm.ApplySearchedLyrics(track, Result(synced: Elrc), "Kugou");
            Assert.Equal("Kugou", vm.LyricsSourceName); // shown for this session
            await Task.Delay(200);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal("[00:05.00]my own timing", File.ReadAllText(lrcPath));
            Assert.False(File.Exists(Sidecar(track, ".elrc"))); // an .elrc would out-rank the user's .lrc
        }
        finally { Cleanup(track); }
    }

    // ── Dialog ──

    [AvaloniaFact]
    public async Task Dialog_Mounts_WithThePickerTheResultsAndThePreview()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("DismissIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
            });

        var searches = 0;
        var vm = new LyricsSearchViewModel(new Track { Title = "Test Song", Artist = "Test Artist" }, new[] { "LRCLIB", "Kugou" },
            () => Task.FromResult<IReadOnlyList<string>>(new[] { "LRCLIB", "Kugou" }),
            (_, _, _, _) =>
            {
                searches++;
                return Task.FromResult<IReadOnlyList<LyricsSourceHit>>(new[]
                {
                    new LyricsSourceHit("LRCLIB", null, true),
                    new LyricsSourceHit("Kugou", Result(synced: Elrc), false),
                });
            },
            (_, _) => { });
        await vm.SearchAsync();

        var window = new LyricsSearchDialog(vm) { Width = 1000, Height = 800 };
        window.Show();
        try
        {
            window.UpdateLayout();
            var picker = window.GetVisualDescendants().OfType<ComboBox>().Single();
            var list = window.GetVisualDescendants().OfType<ListBox>().Single();

            Assert.Equal(3, picker.ItemCount); // Auto + two sources
            Assert.Equal(2, list.ItemCount);
            Assert.Same(vm.SelectedResult, list.SelectedItem);
            Assert.Contains(window.GetVisualDescendants().OfType<SelectableTextBlock>(), t => t.Text?.Contains("word timed") == true);
            Assert.Equal(vm.AutoLabel, picker.SelectedItem);
            Assert.Equal(1, searches); // binding the picker must not start a second search
        }
        finally { window.Close(); }
    }
}
