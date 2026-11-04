using System.ComponentModel;
using Noctis.Mobile.Services;
using Noctis.Mobile.Services.Account;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings → Listening History and Pick Up on Computer, as the rest of the phone sees them: the
/// three saved choices (read at start, whether or not Settings was ever opened) and what they
/// switch off. <see cref="ListeningHistoryViewModel"/> shows and changes them.
/// </summary>
public sealed partial class ShellViewModel
{
    /// <summary>The Keep History choices as days (0 = forever), in the order Settings lists them.</summary>
    public static readonly int[] KeepHistoryChoices = { 0, 365, 90, 30 };

    /// <summary>Plays of the computer's songs are queued for it (Share with Computer). On by default.</summary>
    public bool ShareHistoryWithComputer { get; private set; } = true;

    /// <summary>The phone tells the computer where it is (Pick Up on Computer). Off by default.</summary>
    public bool PickUpOnComputer { get; private set; }

    /// <summary>Days of plays kept; 0 keeps them all.</summary>
    public int KeepHistoryDays { get; private set; }

    /// <summary>How much of a song (percent) must be heard before it counts as a play (play count,
    /// Last Played, Listening History): the computer's "Count a play after", the same
    /// <see cref="AppSettings.PlayCountThresholdPercent"/> in the phone's own settings file. 0, the
    /// computer's default, counts a song as soon as it plays. Scrobbling keeps its own Last.fm rule
    /// (half the song or four minutes: PhoneScrobbler) and is not affected.</summary>
    public int PlayCountThresholdPercent { get; private set; }

    /// <summary>Raised after any of the four changed, on the thread that changed it.</summary>
    public event EventHandler? HistoryPrefsChanged;

    private bool _historyPrefsLoaded;
    // Choices made before the saved ones were read (Settings opens while a cold start still loads):
    // the load keeps the user's value, as the player-bar switches do.
    private readonly HashSet<string> _historySetBeforeLoad = new();
    private Task _historySaveChain = Task.CompletedTask;

    /// <summary>The tail of the history-setting saves; tests await it.</summary>
    internal Task PendingHistorySave => _historySaveChain;

    public void SetShareHistoryWithComputer(bool value)
    {
        if (ShareHistoryWithComputer == value) return;
        ShareHistoryWithComputer = value;
        SaveHistoryPref(nameof(ShareHistoryWithComputer), s => s.MobileShareHistory = value);
    }

    public void SetPlayCountThresholdPercent(int percent)
    {
        percent = AppSettings.SnapPlayCountThreshold(percent);
        if (PlayCountThresholdPercent == percent) return;
        PlayCountThresholdPercent = percent;
        SaveHistoryPref(nameof(PlayCountThresholdPercent), s => s.PlayCountThresholdPercent = percent);
    }

    public void SetPickUpOnComputer(bool value)
    {
        if (PickUpOnComputer == value) return;
        PickUpOnComputer = value;
        SaveHistoryPref(nameof(PickUpOnComputer), s => s.MobilePickUpOnComputer = value);
        // Turned on: tell the computer now, rather than at the next interval.
        if (value) PickUp.Flush();
    }

    public void SetKeepHistoryDays(int days)
    {
        if (KeepHistoryDays == days) return;
        KeepHistoryDays = days;
        SaveHistoryPref(nameof(KeepHistoryDays), s => s.MobileKeepHistoryDays = days);
        PruneHistory();
    }

    /// <summary>Drops plays older than Keep History, off the UI thread.</summary>
    internal Task PruneHistory()
    {
        var days = KeepHistoryDays;
        if (days <= 0 || Library.History is not { } history) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                if (history.PruneOlderThan(DateTime.UtcNow.AddDays(-days)) > 0) Marshal(Library.RefreshRecents);
            }
            catch (Exception ex)
            {
                DebugLog.Write("Settings", $"Pruning the play log failed: {ex.Message}");
            }
        });
    }

    private void SaveHistoryPref(string name, Action<AppSettings> apply)
    {
        if (!_historyPrefsLoaded) _historySetBeforeLoad.Add(name);
        HistoryPrefsChanged?.Invoke(this, EventArgs.Empty);
        if (!_historyPrefsLoaded) return;
        // One update (load-modify-save in one step), chained so a quick on-off lands in order, and
        // only this field: another phone save landing in between is not undone.
        _historySaveChain = _historySaveChain.ContinueWith(async _ =>
        {
            try { await Library.Persistence.UpdateSettingsAsync(apply); }
            catch (Exception ex) { DebugLog.Write("Settings", $"History setting save failed: {ex.Message}"); }
        }, TaskScheduler.Default).Unwrap();
    }

    internal async Task LoadHistoryPrefsAsync()
    {
        try
        {
            var settings = await Library.Persistence.LoadSettingsAsync();
            if (!_historySetBeforeLoad.Contains(nameof(ShareHistoryWithComputer))) ShareHistoryWithComputer = settings.MobileShareHistory;
            if (!_historySetBeforeLoad.Contains(nameof(PickUpOnComputer))) PickUpOnComputer = settings.MobilePickUpOnComputer;
            if (!_historySetBeforeLoad.Contains(nameof(KeepHistoryDays))) KeepHistoryDays = settings.MobileKeepHistoryDays;
            if (!_historySetBeforeLoad.Contains(nameof(PlayCountThresholdPercent)))
                PlayCountThresholdPercent = AppSettings.SnapPlayCountThreshold(settings.PlayCountThresholdPercent);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Settings", $"History settings load failed: {ex.Message}");
        }
        _historyPrefsLoaded = true;
        // What was chosen before the load finished is saved now.
        if (_historySetBeforeLoad.Count > 0)
        {
            var share = ShareHistoryWithComputer;
            var pick = PickUpOnComputer;
            var days = KeepHistoryDays;
            var threshold = PlayCountThresholdPercent;
            var chosen = _historySetBeforeLoad.ToList();
            _historySetBeforeLoad.Clear();
            foreach (var name in chosen)
                SaveHistoryPref(name, name switch
                {
                    nameof(ShareHistoryWithComputer) => s => s.MobileShareHistory = share,
                    nameof(PickUpOnComputer) => s => s.MobilePickUpOnComputer = pick,
                    nameof(PlayCountThresholdPercent) => s => s.PlayCountThresholdPercent = threshold,
                    _ => s => s.MobileKeepHistoryDays = days,
                });
        }
        HistoryPrefsChanged?.Invoke(this, EventArgs.Empty);
        await PruneHistory();
    }

    // ── Pick Up on Computer ──

    private PickUpPublisher? _pickUp;

    /// <summary>Sends the phone's queue and place to the computer while Pick Up on Computer is on.</summary>
    internal PickUpPublisher PickUp => _pickUp ??= new PickUpPublisher(
        () => PickUpOnComputer && Account is { IsSignedIn: true },
        CaptureQueueShare,
        share => Account!.SavePlayQueueAsync(share),
        post: Marshal);

    /// <summary>The playing desktop song first, then the desktop songs queued after it; null when
    /// the song playing is the phone's own (the computer has no such song to continue).</summary>
    private PlayQueueShare? CaptureQueueShare()
    {
        if (Account is not { } account || Player.CurrentTrack is not { } current || account.DesktopIdOf(current) is not { } currentId) return null;
        var ids = new List<Guid> { currentId };
        foreach (var t in Player.UpNext)
        {
            if (ids.Count >= PhoneHandoff.MaxTracks) break;
            if (account.DesktopIdOf(t) is { } id) ids.Add(id);
        }
        return new PlayQueueShare(ids, currentId, (long)Math.Max(0, Player.Position.TotalMilliseconds));
    }

    private void OnPlayerChangedForPickUp(PropertyChangedEventArgs e)
    {
        if (!PickUpOnComputer) return;
        if (e.PropertyName is nameof(NowPlayingViewModel.Position)) { if (Player.IsPlaying) PickUp.Tick(); }
        else if (e.PropertyName is nameof(NowPlayingViewModel.CurrentTrack) or nameof(NowPlayingViewModel.IsPlaying)) PickUp.Flush();
    }

    private void OnAccountSyncProgress(object? sender, NoctisSyncProgress progress)
    {
        if (progress.Stage == NoctisSyncStage.Done && PickUpOnComputer) Marshal(PickUp.Flush);
    }
}
