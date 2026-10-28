using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.Mobile.ViewModels;

/// <summary>One day of "This Week": its bar's height (0 to <see cref="HistoryBar.MaxHeight"/>), its weekday's letter, and whether it is today.</summary>
public sealed record HistoryBar(double Height, string Label, bool IsToday, string Name)
{
    public const double MaxHeight = 120;
    private const double MinHeight = 6;

    public static IReadOnlyList<HistoryBar> Of(ListeningWeek week)
    {
        var max = week.DayTicks.Max();
        return week.Days.Select((day, i) => new HistoryBar(
            max == 0 ? MinHeight : Math.Max(MinHeight, MaxHeight * week.DayTicks[i] / max),
            day.ToString("ddd", System.Globalization.CultureInfo.CurrentCulture)[..1].ToUpperInvariant(),
            i == week.Days.Count - 1,
            $"{day:dddd}: {ListeningWeekCalculator.FormatTotal(week.DayTicks[i])}")).ToList();
    }
}

/// <summary>A song in Recently Played with how long ago it last played ("Now", "12 min", "1 h").</summary>
public sealed record HistoryRow(Track Track, string When);

/// <summary>
/// Settings → Listening History: this week's listening from the phone's play log, the last few
/// plays, how long plays are kept, whether they are shared with the computer and Clear; and the
/// Pick Up on Computer switch the main sheet shows. The choices live on <see cref="ShellViewModel"/>
/// (the player and the sync read them without Settings open); this is their face.
/// </summary>
public sealed partial class ListeningHistoryViewModel : ObservableObject
{
    private const int PreviewRows = 3;
    private const int ShowAllRows = 200;

    private readonly ShellViewModel _shell;
    private bool _syncing;

    public ListeningHistoryViewModel(ShellViewModel shell)
    {
        _shell = shell;
        SyncFromShell();
        Refresh();
        // Weakly: the shell outlives a Settings sheet.
        var weak = new WeakReference<ListeningHistoryViewModel>(this);
        EventHandler? prefs = null, played = null;
        prefs = (_, _) =>
        {
            if (weak.TryGetTarget(out var vm)) vm.SyncFromShell();
            else _shell.HistoryPrefsChanged -= prefs;
        };
        played = (_, _) =>
        {
            if (weak.TryGetTarget(out var vm)) vm.Refresh();
            else _shell.Player.PlayRecorded -= played;
        };
        _shell.HistoryPrefsChanged += prefs;
        _shell.Player.PlayRecorded += played;
    }

    /// <summary>The clock behind "12 min" and "this week"; tests pin it.</summary>
    internal Func<DateTime> UtcNow { get; init; } = () => DateTime.UtcNow;

    /// <summary>The zone whose midnights split the days; tests pin it.</summary>
    internal TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>The main sheet's row value: "6 h this week", "12 min this week", "" without plays.</summary>
    public string Summary
    {
        get => _summary;
        private set => SetProperty(ref _summary, value);
    }
    private string _summary = string.Empty;

    [ObservableProperty] private string _weekTotal = "0 min";
    [ObservableProperty] private string _weekPlays = string.Empty;
    [ObservableProperty] private IReadOnlyList<HistoryBar> _bars = Array.Empty<HistoryBar>();

    /// <summary>The last <see cref="PreviewRows"/> different songs played, newest first.</summary>
    public BulkObservableCollection<HistoryRow> Recent { get; } = new();

    [ObservableProperty] private bool _hasRecent;

    /// <summary>Pick Up on Computer (the main sheet's switch binds here).</summary>
    [ObservableProperty] private bool _pickUpOnComputer;

    [ObservableProperty] private bool _shareWithComputer = true;

    [ObservableProperty] private string _keepText = "Forever";

    /// <summary>Share with Computer and Pick Up need the computer's account; without one the rows hide.</summary>
    public bool HasAccount => _shell.HasAccount;

    partial void OnPickUpOnComputerChanged(bool value)
    {
        if (!_syncing) _shell.SetPickUpOnComputer(value);
    }

    partial void OnShareWithComputerChanged(bool value)
    {
        if (!_syncing) _shell.SetShareHistoryWithComputer(value);
    }

    private void SyncFromShell()
    {
        _syncing = true;
        try
        {
            PickUpOnComputer = _shell.PickUpOnComputer;
            ShareWithComputer = _shell.ShareHistoryWithComputer;
            KeepText = KeepLabel(_shell.KeepHistoryDays);
        }
        finally
        {
            _syncing = false;
        }
    }

    public static string KeepLabel(int days) => days switch
    {
        0 => "Forever",
        365 => "1 Year",
        _ => $"{days} Days",
    };

    /// <summary>Reads the play log again (a song started, the pane opened, plays were cleared).</summary>
    public void Refresh()
    {
        var events = _shell.Library.History?.Events ?? Array.Empty<PlayHistoryEvent>();
        var now = UtcNow();
        var library = _shell.Library.Service;
        var week = ListeningWeekCalculator.Compute(events, id => library.GetTrackById(id)?.Duration, now, Zone);
        Summary = ListeningWeekCalculator.FormatSummary(week);
        WeekTotal = ListeningWeekCalculator.FormatTotal(week.TotalTicks);
        WeekPlays = week.Plays == 1 ? "1 play" : $"{week.Plays} plays";
        Bars = HistoryBar.Of(week);

        var rows = RecentPlays(events, PreviewRows).Select(r => new HistoryRow(r.Track, ListeningWeekCalculator.FormatAgo(r.PlayedUtc, now))).ToList();
        MobileLibrary.ReplaceIfChanged(Recent, rows);
        HasRecent = Recent.Count > 0;
    }

    /// <summary>The newest-first plays of songs still in the library, each song once.</summary>
    private IEnumerable<(Track Track, DateTime PlayedUtc)> RecentPlays(IReadOnlyList<PlayHistoryEvent> events, int max)
    {
        var seen = new HashSet<Guid>();
        var count = 0;
        for (var i = events.Count - 1; i >= 0 && count < max; i--)
        {
            var e = events[i];
            if (_shell.Library.Service.GetTrackById(e.TrackId) is not { } track || !seen.Add(track.Id)) continue;
            count++;
            yield return (track, e.PlayedAtUtc);
        }
    }

    [RelayCommand]
    private void ShowAll()
    {
        var events = _shell.Library.History?.Events ?? Array.Empty<PlayHistoryEvent>();
        _shell.Navigate(new SongListPageViewModel(_shell, "Recently Played",
            () => RecentPlays(_shell.Library.History?.Events ?? events, ShowAllRows).Select(r => r.Track).ToList()));
    }

    /// <summary>Keep History: a glass action sheet with the four choices.</summary>
    [RelayCommand]
    private void ChooseKeep()
    {
        var actions = ShellViewModel.KeepHistoryChoices
            .Select(days => new PromptAction(KeepLabel(days), () => _shell.SetKeepHistoryDays(days)))
            .ToList();
        _shell.ShowPrompt(new PromptViewModel(_shell, "Keep History", "Plays older than this are removed from this phone.", actions));
    }

    [RelayCommand]
    private void AskClear()
    {
        _shell.ShowPrompt(new PromptViewModel(_shell, "Clear Listening History?",
            "This removes every play from this phone. Plays already sent to Last.fm stay there.",
            new[] { new PromptAction("Clear Listening History", _ => ClearAsync(), isDestructive: true) }));
    }

    /// <summary>Forgets every play on the phone and writes the empty log; the Library's shelf and
    /// On Repeat (built from the log) and this page re-read it.</summary>
    internal async Task ClearAsync()
    {
        if (_shell.Library.History is { } history)
        {
            try { await history.ClearAsync(); }
            catch (Exception ex) { DebugLog.Write("Settings", $"Clearing the play log failed: {ex.Message}"); }
        }
        _shell.Library.RefreshRecents();
        Refresh();
    }
}
