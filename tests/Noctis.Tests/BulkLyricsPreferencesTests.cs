using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #203: the whole-library Find Lyrics follows Settings ▸ Lyrics. Its text named lyrics files
/// "next to your songs" for a user who saves lyrics inside the audio files, and offered to upgrade
/// line-synced lyrics while the preferred type was line-synced. A run already saved where Settings
/// says (the shared <see cref="LyricsWriter"/>) and stopped at the preferred type; the text now says so.
/// </summary>
public class BulkLyricsPreferencesTests : IDisposable
{
    private const string Elrc = "[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>";
    private const string Lrc = "[00:01.00]Hello world";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "bulk203-" + Guid.NewGuid().ToString("N"));

    public BulkLyricsPreferencesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class NoopService : ILyricsBulkService
    {
        public Task<LyricsBulkSummary> FetchAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct)
            => Task.FromResult(new LyricsBulkSummary(0, 0, 0, 0, 0));
        public Task<int> RemoveAsync(IReadOnlyList<Track> tracks, IProgress<LyricsBulkProgress>? progress, CancellationToken ct) => Task.FromResult(0);
    }

    private static AppSettings Prefs(LyricsSaveTarget target, LyricsPreferredType type) => new()
    {
        LyricsSaveTarget = target.ToString(),
        LyricsPreferredType = type.ToString(),
    };

    private static BulkLyricsViewModel Dialog(AppSettings prefs, bool remove = false) =>
        new(new[] { new Track { Title = "a" } }, new NoopService(), remove, library: !remove, settings: prefs);

    private static string L(string key) => Noctis.Localization.Loc.T(key);

    [AvaloniaFact]
    public void Embedded_SaysInsideYourMusicFiles_NotNextToYourSongs()
    {
        var vm = Dialog(Prefs(LyricsSaveTarget.Embedded, LyricsPreferredType.WordSynced));
        Assert.Contains(L("BulkLyrics.SaveEmbedded"), vm.SubtitleText);
        Assert.DoesNotContain(L("BulkLyrics.SaveSidecar"), vm.SubtitleText);
        Assert.DoesNotContain("next to", vm.SubtitleText);
    }

    [AvaloniaTheory]
    [InlineData(LyricsSaveTarget.Sidecar, "BulkLyrics.SaveSidecar")]
    [InlineData(LyricsSaveTarget.Embedded, "BulkLyrics.SaveEmbedded")]
    [InlineData(LyricsSaveTarget.Both, "BulkLyrics.SaveBoth")]
    public void Subtitle_NamesWhereSettingsSavesLyrics(LyricsSaveTarget target, string key)
    {
        var vm = Dialog(Prefs(target, LyricsPreferredType.WordSynced));
        Assert.Contains(L(key), vm.SubtitleText);
    }

    [AvaloniaTheory]
    [InlineData(LyricsPreferredType.WordSynced, "BulkLyrics.FetchWord", true)]
    [InlineData(LyricsPreferredType.LineSynced, "BulkLyrics.FetchLine", false)]
    [InlineData(LyricsPreferredType.Plain, "BulkLyrics.FetchPlain", false)]
    public void Subtitle_AndUpgradeOption_FollowThePreferredType(LyricsPreferredType type, string key, bool canUpgrade)
    {
        var vm = Dialog(Prefs(LyricsSaveTarget.Sidecar, type));
        Assert.StartsWith(L(key), vm.SubtitleText);
        // Line-synced → word-synced is the only upgrade the option adds; with line-synced or plain
        // preferred there is nothing above it to upgrade to, so the option is not offered.
        Assert.Equal(canUpgrade, vm.CanUpgradeLineSynced);
        Assert.DoesNotContain("plain or line-synced", L("BulkLyrics.UpgradeLine"));
    }

    [AvaloniaFact]
    public void Remove_KeepsItsOwnSubtitle_AndNoUpgradeOption()
    {
        var vm = Dialog(Prefs(LyricsSaveTarget.Embedded, LyricsPreferredType.WordSynced), remove: true);
        Assert.Equal(L("BulkLyrics.SubtitleRemove"), vm.SubtitleText);
        Assert.False(vm.CanUpgradeLineSynced);
    }

    // ── What a run does (the behaviour the text now describes) ──

    private (LyricsBulkService Service, LyricsSaveTargetTests.RecordingMetadata Metadata) Bulk(AppSettings settings, LyricsSaveTarget target,
        Func<LrcLibResult?> answer, Action? onAsk = null)
    {
        var metadata = new LyricsSaveTargetTests.RecordingMetadata();
        var writer = new LyricsWriter(metadata, null, new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json")), Path.Combine(_dir, "cache"))
        {
            SaveTarget = () => target,
            TrashFile = _ => true,
        };
        var source = new LyricsBulkSource("A", _ => true, (a, t, d, c) => { onAsk?.Invoke(); return Task.FromResult(answer()); });
        var service = new LyricsBulkService(null!, writer, new FakeLibraryService(), () => settings,
            () => new[] { source }, Path.Combine(_dir, "ledger.json"))
        {
            Delay = (_, _) => Task.CompletedTask,
        };
        return (service, metadata);
    }

    private Track NewTrack(string name)
    {
        var audio = Path.Combine(_dir, name + ".flac");
        File.WriteAllText(audio, "x");
        return new Track { Title = name, Artist = "Artist", FilePath = audio, SourceType = SourceType.Local, Duration = TimeSpan.FromMinutes(3) };
    }

    [Fact]
    public async Task EmbeddedTarget_WritesTheFoundLyricsIntoTheAudioFile_AndNoLyricsFile()
    {
        var settings = Prefs(LyricsSaveTarget.Embedded, LyricsPreferredType.WordSynced);
        var (service, metadata) = Bulk(settings, LyricsSaveTarget.Embedded, () => new LrcLibResult { SyncedLyrics = Elrc, PlainLyrics = "Hello world" });
        var t = NewTrack("embed");

        var s = await service.FetchAsync(new[] { t }, new LyricsBulkOptions(), null, CancellationToken.None);
        // No tag writer: the write runs on the thread pool.
        for (var i = 0; i < 100 && metadata.LyricsWrites.Count == 0; i++) await Task.Delay(20);

        Assert.Equal(1, s.WordSynced);
        var write = Assert.Single(metadata.LyricsWrites);
        Assert.Equal(t.FilePath, write.Path);
        Assert.Equal(Elrc, write.Synced);
        Assert.False(File.Exists(Path.ChangeExtension(t.FilePath!, ".lrc")));
        Assert.False(File.Exists(Path.ChangeExtension(t.FilePath!, ".elrc")));
    }

    [Fact]
    public async Task LineSyncedPreferred_NeverLooksUpALineSyncedSong_EvenWithUpgradeTicked()
    {
        var settings = Prefs(LyricsSaveTarget.Sidecar, LyricsPreferredType.LineSynced);
        var asked = 0;
        var (service, _) = Bulk(settings, LyricsSaveTarget.Sidecar, () => new LrcLibResult { SyncedLyrics = Elrc }, () => asked++);
        var t = NewTrack("line");
        t.SyncedLyrics = Lrc;

        var s = await service.FetchAsync(new[] { t }, new LyricsBulkOptions(Upgrade: true), null, CancellationToken.None);

        Assert.Equal(0, asked);
        Assert.Equal((1, 0), (s.Skipped, s.Upgraded));
        Assert.False(File.Exists(Path.ChangeExtension(t.FilePath!, ".elrc")));
    }
}
