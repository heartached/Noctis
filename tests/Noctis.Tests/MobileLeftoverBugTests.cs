using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>Phone bugs a 10-20 bug hunt found and left: each pinned by the repro it was fixed on.</summary>
public class MobileLeftoverBugTests
{
    /// <summary>
    /// settings.json in memory, with reads that can be held back (a cold start: the library load
    /// keeps the first reads waiting while the Settings sheet is already up) and a log of every
    /// save. Saves and reads go through a copy, as the real file does.
    /// </summary>
    internal sealed class HeldSettingsPersistence : TestPersistenceService
    {
        private readonly object _lock = new();
        private string _json;
        private TaskCompletionSource? _held;

        public HeldSettingsPersistence(AppSettings? settings = null) => _json = JsonSerializer.Serialize(settings ?? new AppSettings());

        public int Saves;

        /// <summary>Optional pause inside each read, after the file was read (the gap a racing save lands in).</summary>
        public Func<Task>? AfterRead { get; set; }

        public void HoldReads() { lock (_lock) _held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously); }

        public void ReleaseReads()
        {
            TaskCompletionSource? held;
            lock (_lock) { held = _held; _held = null; }
            held?.TrySetResult();
        }

        public AppSettings Saved { get { lock (_lock) return JsonSerializer.Deserialize<AppSettings>(_json)!; } }

        public override async Task<AppSettings> LoadSettingsAsync()
        {
            Task? wait;
            lock (_lock) wait = _held?.Task;
            if (wait != null) await wait;
            AppSettings copy;
            lock (_lock) copy = JsonSerializer.Deserialize<AppSettings>(_json)!;
            if (AfterRead != null) await AfterRead();
            return copy;
        }

        public override Task SaveSettingsAsync(AppSettings settings)
        {
            lock (_lock)
            {
                _json = JsonSerializer.Serialize(settings);
                Saves++;
            }
            return Task.CompletedTask;
        }
    }

    private static ShellViewModel MakeShell(HeldSettingsPersistence persistence)
    {
        var library = new FakeLibraryService();
        var player = new FakeAudioPlayer();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, new FakeHistoryLog(), marshal: a => a());
        return new ShellViewModel(
            new LibraryViewModel(library, persistence, new MobileFixtures.NoPicker(), new FakeHistoryLog(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, new FakeTrackFiles(), persistence, work => Task.FromResult(work())))
        {
            Marshal = a => a(),
        };
    }

    // ── Bug 2: switches flipped in the first seconds of a cold start ──

    /// <summary>
    /// Settings is up while a cold start still loads the library (the shell shows first). A switch
    /// flipped then was put back when the start read the saved settings, and its save was skipped
    /// ("not loaded yet"): Album Colours, the mini player's progress, the lyrics switches and Scan
    /// on Startup all came back as they were.
    /// </summary>
    [Fact]
    public async Task SwitchesFlippedBeforeTheStartReadTheSettings_KeepTheUsersValue_AndAreSaved()
    {
        var persistence = new HeldSettingsPersistence(new AppSettings
        {
            ScanOnStartup = true, MobilePlayerBarProgressFill = false, MobileArtworkPageColors = true,
            LyricsShowTranslations = true, LrcLibEnabled = true, LyricsMinLineOpacity = 20,
        });
        var shell = MakeShell(persistence);
        persistence.HoldReads();
        var start = shell.InitializeAsync();

        shell.Library.ScanOnStartup = false;
        shell.ShowMiniProgressFill = true;
        shell.ArtworkPageColors = false;
        shell.Lyrics.ShowTranslation = false;
        shell.Lyrics.FindLyricsOnline = false;
        shell.Lyrics.MinLineOpacity = 35;

        persistence.ReleaseReads();
        await start.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await shell.PendingPlayerBarSave;
        await shell.Lyrics.PendingSave;
        await Until(() => !persistence.Saved.ScanOnStartup);

        Assert.False(shell.Library.ScanOnStartup);
        Assert.True(shell.ShowMiniProgressFill);
        Assert.False(shell.ArtworkPageColors);
        Assert.False(shell.Lyrics.ShowTranslation);
        Assert.False(shell.Lyrics.FindLyricsOnline);
        Assert.Equal(35, shell.Lyrics.MinLineOpacity);

        var saved = persistence.Saved;
        Assert.False(saved.ScanOnStartup);
        Assert.True(saved.MobilePlayerBarProgressFill);
        Assert.False(saved.MobileArtworkPageColors);
        Assert.False(saved.LyricsShowTranslations);
        Assert.False(saved.LrcLibEnabled);
        Assert.Equal(35, saved.LyricsMinLineOpacity);
    }

    /// <summary>Nothing flipped: the start applies the saved values and writes nothing back.</summary>
    [Fact]
    public async Task ColdStartWithNoFlip_AppliesTheSavedValues_AndSavesNothing()
    {
        var persistence = new HeldSettingsPersistence(new AppSettings
        {
            ScanOnStartup = false, MobilePlayerBarProgressFill = true, MobileArtworkPageColors = false,
            LyricsShowTranslations = false, LrcLibEnabled = false,
        });
        var shell = MakeShell(persistence);

        await shell.InitializeAsync().WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        await shell.PendingPlayerBarSave;
        await shell.Lyrics.PendingSave;

        Assert.False(shell.Library.ScanOnStartup);
        Assert.True(shell.ShowMiniProgressFill);
        Assert.False(shell.ArtworkPageColors);
        Assert.False(shell.Lyrics.ShowTranslation);
        Assert.False(shell.Lyrics.FindLyricsOnline);
        Assert.Equal(0, persistence.Saves);
    }

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var start = Environment.TickCount64;
        while (!condition())
        {
            if (Environment.TickCount64 - start > timeoutMs) throw new TimeoutException("condition never became true");
            await Task.Delay(10);
        }
    }
}
