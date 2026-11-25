using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The phone's cold start read settings.json once for the language, theme, background and Library
/// rows (AndroidApp.StartAsync), then seven more times in ShellViewModel.InitializeAsync (player bar,
/// list sorts, recent searches, background again, history, lyrics, ReplayGain). It is read once now
/// and handed along; a setting changed in the seconds before the start finishes is still kept.
/// </summary>
public class MobileStartupSettingsReadTests
{
    /// <summary>Counts every read of the settings; reads hand back what the last save wrote.</summary>
    private sealed class CountingPersistence : TestPersistenceService, IPersistenceService
    {
        public int Loads;
        public AppSettings Saved = new()
        {
            MobilePlayerBarProgressFill = false,
            LyricsShowTranslations = false,
            MobileShareHistory = false,
            ReplayGainMode = "Off",
        };
        public override Task<AppSettings> LoadSettingsAsync()
        {
            Interlocked.Increment(ref Loads);
            var copy = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(Saved))!;
            return Task.FromResult(copy);
        }
        /// <summary>A save's own load-change-save is not a read of the start: not counted.</summary>
        public Task UpdateSettingsAsync(System.Action<AppSettings> change)
        {
            change(Saved);
            return Task.CompletedTask;
        }
        public override Task SaveSettingsAsync(AppSettings settings)
        {
            Saved = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(System.Text.Json.JsonSerializer.Serialize(settings))!;
            return Task.CompletedTask;
        }
    }

    private static ShellViewModel Shell(CountingPersistence persistence)
    {
        var library = new FakeLibraryService();
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, new FakeHistoryLog(), marshal: a => a());
        return new ShellViewModel(
            new LibraryViewModel(library, persistence, new MobileFixtures.NoPicker(), new FakeHistoryLog(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())))
        {
            TintFactory = () => new PageTint(_ => null, work => Task.FromResult(work())),
            ArtworkAccentSource = _ => null,
            RunArtworkAccent = work => Task.FromResult(work()),
        };
    }

    [Fact]
    public async Task ColdStart_ReadsTheSettingsOnce_AndKeepsWhatWasChangedBeforeItFinished()
    {
        using var persistence = new CountingPersistence();
        var shell = Shell(persistence);

        // AndroidApp.StartAsync: one read, applied to what paints first.
        var settings = await persistence.LoadSettingsAsync();
        shell.Background.ApplySettings(settings);
        shell.Library.ApplySettings(settings);
        // While the library loads (seconds), the owner flips a few switches to on (the saved values
        // are off; off first, so each is a change whatever the default).
        shell.ShowMiniProgressFill = false;
        shell.ShowMiniProgressFill = true;
        shell.Lyrics.ShowTranslation = false;
        shell.Lyrics.ShowTranslation = true;
        shell.SetShareHistoryWithComputer(false);
        shell.SetShareHistoryWithComputer(true);

        await shell.InitializeAsync(settings);

        Assert.Equal(1, persistence.Loads);
        Assert.True(shell.ShowMiniProgressFill);
        Assert.True(shell.Lyrics.ShowTranslation);
        Assert.True(shell.ShareHistoryWithComputer);
        Assert.Equal(ReplayGainLevel.ParseMode("Off"), shell.Player.ReplayGainMode);
    }
}
