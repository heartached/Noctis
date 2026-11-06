using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Localization;

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
            Loc.Instance.Culture.DateTimeFormat.GetShortestDayName(day.DayOfWeek)[..1].ToUpper(Loc.Instance.Culture),
            i == week.Days.Count - 1,
            Loc.T("Mobile.History.DayName", day.ToString("dddd", Loc.Instance.Culture), ListeningHistoryViewModel.FormatTotal(week.DayTicks[i])))).ToList();
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

    [ObservableProperty] private string _weekTotal = FormatTotal(0);
    [ObservableProperty] private string _weekPlays = string.Empty;
    [ObservableProperty] private IReadOnlyList<HistoryBar> _bars = Array.Empty<HistoryBar>();

    /// <summary>The last <see cref="PreviewRows"/> different songs played, newest first.</summary>
    public BulkObservableCollection<HistoryRow> Recent { get; } = new();

    [ObservableProperty] private bool _hasRecent;

    /// <summary>Pick Up on Computer (the main sheet's switch binds here).</summary>
    [ObservableProperty] private bool _pickUpOnComputer;

    [ObservableProperty] private bool _shareWithComputer = true;

    [ObservableProperty] private string _keepText = KeepLabel(0);

    /// <summary>"Count a Play After": Immediately, or the share of a song that must be heard.</summary>
    [ObservableProperty] private string _countText = CountLabel(0);

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
            CountText = CountLabel(_shell.PlayCountThresholdPercent);
        }
        finally
        {
            _syncing = false;
        }
    }

    /// <summary>The UI language changed: the values and this week's numbers re-read.</summary>
    public void Relabel()
    {
        SyncFromShell();
        Refresh();
        OnPropertyChanged(string.Empty);
    }

    public static string KeepLabel(int days) => days switch
    {
        0 => Loc.T("Mobile.History.Forever"),
        365 => Loc.T("Mobile.History.OneYear"),
        _ => Loc.Plural("Mobile.History.Days", days),
    };

    public static string CountLabel(int percent) => percent <= 0 ? Loc.T("Mobile.History.Immediately") : $"{percent}%";

    // The Core formatters (ListeningWeekCalculator) in the phone's language.

    /// <summary>"6 h 12 min", "12 min", "40 s", "0 min".</summary>
    public static string FormatTotal(long ticks)
    {
        var t = TimeSpan.FromTicks(Math.Max(0, ticks));
        if (t.TotalMinutes < 1) return t.TotalSeconds < 1 ? Loc.T("Mobile.Time.Minutes", 0) : Loc.T("Mobile.Time.Seconds", (int)t.TotalSeconds);
        var h = (int)t.TotalHours;
        return h == 0 ? Loc.T("Mobile.Time.Minutes", t.Minutes)
            : t.Minutes == 0 ? Loc.T("Mobile.Time.Hours", h)
            : Loc.T("Mobile.Time.HoursMinutes", h, t.Minutes);
    }

    /// <summary>The Settings row's value: "6 h this week", "12 min this week"; "" without plays.</summary>
    public static string FormatSummary(ListeningWeek week)
    {
        if (week.Plays == 0) return string.Empty;
        var t = TimeSpan.FromTicks(week.TotalTicks);
        return t.TotalHours >= 1
            ? Loc.T("Mobile.History.WeekHours", (int)t.TotalHours)
            : Loc.T("Mobile.History.WeekMinutes", Math.Max(1, (int)t.TotalMinutes));
    }

    /// <summary>"Now", "12 min", "1 h", "3 d", "4 mo": how long ago a play was.</summary>
    public static string FormatAgo(DateTime playedUtc, DateTime nowUtc)
    {
        var ago = AsUtc(nowUtc) - AsUtc(playedUtc);
        if (ago < TimeSpan.FromMinutes(1)) return Loc.T("Mobile.Time.Now");
        if (ago < TimeSpan.FromHours(1)) return Loc.T("Mobile.Time.Minutes", (int)ago.TotalMinutes);
        if (ago < TimeSpan.FromDays(1)) return Loc.T("Mobile.Time.Hours", (int)ago.TotalHours);
        if (ago < TimeSpan.FromDays(60)) return Loc.T("Mobile.Time.Days", (int)ago.TotalDays);
        return Loc.T("Mobile.Time.Months", (int)(ago.TotalDays / 30));
    }

    private static DateTime AsUtc(DateTime d) => d.Kind == DateTimeKind.Utc ? d : DateTime.SpecifyKind(d, DateTimeKind.Utc);

    /// <summary>Reads the play log again (a song started, the pane opened, plays were cleared).</summary>
    public void Refresh()
    {
        var events = _shell.Library.History?.Events ?? Array.Empty<PlayHistoryEvent>();
        var now = UtcNow();
        var library = _shell.Library.Service;
        var week = ListeningWeekCalculator.Compute(events, id => library.GetTrackById(id)?.Duration, now, Zone);
        Summary = FormatSummary(week);
        WeekTotal = FormatTotal(week.TotalTicks);
        WeekPlays = Loc.Plural("Mobile.Count.Plays", week.Plays);
        Bars = HistoryBar.Of(week);

        var rows = RecentPlays(events, PreviewRows).Select(r => new HistoryRow(r.Track, FormatAgo(r.PlayedUtc, now))).ToList();
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
        _shell.Navigate(new SongListPageViewModel(_shell, "Mobile.RecentlyPlayed",
            () => RecentPlays(_shell.Library.History?.Events ?? events, ShowAllRows).Select(r => r.Track).ToList()));
    }

    /// <summary>Keep History: a glass action sheet with the four choices.</summary>
    [RelayCommand]
    private void ChooseKeep()
    {
        var actions = ShellViewModel.KeepHistoryChoices
            .Select(days => new PromptAction(KeepLabel(days), () => _shell.SetKeepHistoryDays(days)))
            .ToList();
        _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.History.Keep"), Loc.T("Mobile.History.KeepDetail"), actions));
    }

    /// <summary>Count a Play After: a glass action sheet with the computer's choices.</summary>
    [RelayCommand]
    private void ChooseCount()
    {
        var actions = AppSettings.PlayCountThresholdChoices
            .Select(p => new PromptAction(CountLabel(p), () => _shell.SetPlayCountThresholdPercent(p)))
            .ToList();
        _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.History.CountAfter"),
            Loc.T("Mobile.History.CountAfterDetail"), actions));
    }

    [RelayCommand]
    private void AskClear()
    {
        _shell.ShowPrompt(new PromptViewModel(_shell, Loc.T("Mobile.History.ClearQuestion"),
            Loc.T("Mobile.History.ClearDetail"),
            new[] { new PromptAction(Loc.T("Mobile.History.ClearName"), _ => ClearAsync(), isDestructive: true) }));
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
