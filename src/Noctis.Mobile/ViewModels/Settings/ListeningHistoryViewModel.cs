using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Localization;

namespace Noctis.Mobile.ViewModels;

/// <summary>One day of "This Week": its bar's height (0 to <see cref="MaxHeight"/>), its weekday's
/// letter, whether it is today, and whether it is the day the card shows (tapped).</summary>
public sealed partial class HistoryBar : ObservableObject
{
    public const double MaxHeight = 120;
    private const double MinHeight = 6;

    public HistoryBar(DateTime date, double height, string label, bool isToday, string name)
    {
        Date = date;
        Height = height;
        Label = label;
        IsToday = isToday;
        Name = name;
    }

    public DateTime Date { get; }
    public double Height { get; }
    public string Label { get; }
    public bool IsToday { get; }

    /// <summary>The screen reader's name: "Wednesday: 2 h 3 min, 5 plays".</summary>
    public string Name { get; }

    /// <summary>Changed in place, so a tap leaves the bars (and a screen reader's focus) where they are.</summary>
    [ObservableProperty] private bool _isSelected;

    public static IReadOnlyList<HistoryBar> Of(ListeningWeek week, DateTime? selected = null)
    {
        var max = week.DayTicks.Max();
        return week.Days.Select((day, i) => new HistoryBar(day,
            max == 0 ? MinHeight : Math.Max(MinHeight, MaxHeight * week.DayTicks[i] / max),
            Loc.Instance.Culture.DateTimeFormat.GetShortestDayName(day.DayOfWeek)[..1].ToUpper(Loc.Instance.Culture),
            i == week.Days.Count - 1,
            Loc.T("Mobile.History.DayName", day.ToString("dddd", Loc.Instance.Culture), ListeningHistoryViewModel.FormatTotal(week.DayTicks[i]),
                Loc.Plural("Mobile.Count.Plays", week.DayPlays[i])))
        {
            IsSelected = day == selected,
        }).ToList();
    }
}

/// <summary>A song in Recently Played with how long ago it last played ("Now", "12 min", "1 h") and,
/// under its title, its artist and how often the log has it ("Artist · 12 plays").</summary>
public sealed record HistoryRow(Track Track, string When, string Sub)
{
    /// <summary>The end of <see cref="Sub"/> after the artist (" · 12 plays", or all of it without one):
    /// the row draws it apart so a long artist is cut short instead of the count.</summary>
    public string Plays => string.IsNullOrWhiteSpace(Track.Artist) ? Sub : Sub[Track.Artist.Length..];
}

/// <summary>
/// Settings → Listening History: this week's listening from the phone's play log (or one day's,
/// a bar tapped), the all-time total, the last few plays with their counts, how long plays are
/// kept, whether they are shared with the computer and Clear; and the Pick Up on Computer switch
/// the main sheet shows. The choices live on <see cref="ShellViewModel"/> (the player and the
/// sync read them without Settings open); this is their face.
/// </summary>
public sealed partial class ListeningHistoryViewModel : ObservableObject
{
    private const int PreviewRows = 3;
    private const int ShowAllRows = 200;

    private readonly ShellViewModel _shell;
    private bool _syncing;
    private ListeningWeek? _week;

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
        _detach = () =>
        {
            _shell.HistoryPrefsChanged -= prefs;
            _shell.Player.PlayRecorded -= played;
        };
    }

    private Action? _detach;

    /// <summary>The sheet closed: stop following the shell. Until a collection let the weak
    /// handlers go, every Settings opened since launch re-read the whole play log (the week and
    /// the all-time total) on each play.</summary>
    public void Detach()
    {
        _detach?.Invoke();
        _detach = null;
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

    // The card's numbers: the week's, or the tapped day's (SelectedDay).
    [ObservableProperty] private string _weekCaption = Loc.T("Mobile.History.ThisWeek");
    [ObservableProperty] private string _weekTotal = FormatTotal(0);
    [ObservableProperty] private string _weekPlays = string.Empty;
    [ObservableProperty] private IReadOnlyList<HistoryBar> _bars = Array.Empty<HistoryBar>();

    /// <summary>The day the card shows, a bar tapped; null for the week. Kept by date across a refresh.</summary>
    public DateTime? SelectedDay { get; private set; }

    /// <summary>"All Time: 41 h 3 min · 812 plays" over the whole kept log; "" without plays.</summary>
    [ObservableProperty] private string _allTime = string.Empty;

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
        Func<Guid, TimeSpan?> lengthOf = id => library.GetTrackById(id)?.Duration;
        var week = ListeningWeekCalculator.Compute(events, lengthOf, now, Zone);
        _week = week;
        Summary = FormatSummary(week);
        // A day that has left the window (midnight passed) lets go; one still in it stays shown.
        if (SelectedDay is { } day && !week.Days.Contains(day)) SelectedDay = null;
        Bars = HistoryBar.Of(week, SelectedDay);
        ShowSelection();

        var (allTicks, allPlays) = ListeningWeekCalculator.ComputeAllTime(events, lengthOf);
        AllTime = allPlays == 0 ? string.Empty
            : Loc.T("Mobile.History.AllTime", FormatTotal(allTicks), Loc.Plural("Mobile.Count.Plays", allPlays));

        var recent = RecentPlays(events, PreviewRows).ToList();
        var counts = PlayCounts(events, recent.Select(r => r.Track.Id));
        var rows = recent.Select(r => new HistoryRow(r.Track, FormatAgo(r.PlayedUtc, now), RowSub(r.Track, counts[r.Track.Id]))).ToList();
        MobileLibrary.ReplaceIfChanged(Recent, rows);
        HasRecent = Recent.Count > 0;
    }

    /// <summary>A day bar tapped: the card shows that day; the shown day tapped again shows the week.</summary>
    [RelayCommand]
    private void SelectDay(HistoryBar? bar)
    {
        SelectedDay = bar == null || bar.Date == SelectedDay ? null : bar.Date;
        foreach (var b in Bars) b.IsSelected = b.Date == SelectedDay;
        ShowSelection();
    }

    /// <summary>The card tapped outside the bars: back to the week.</summary>
    [RelayCommand]
    private void ClearDay() => SelectDay(null);

    /// <summary>The caption, total and plays for the shown day, or for the week.</summary>
    private void ShowSelection()
    {
        if (_week is not { } week) return;
        var i = SelectedDay is { } day ? IndexOf(week.Days, day) : -1;
        if (i < 0)
        {
            WeekCaption = Loc.T("Mobile.History.ThisWeek");
            WeekTotal = FormatTotal(week.TotalTicks);
            WeekPlays = Loc.Plural("Mobile.Count.Plays", week.Plays);
            return;
        }
        var culture = Loc.Instance.Culture;
        var name = week.Days[i].ToString("dddd", culture);
        // Some languages write weekdays in lower case ("mercredi"); the caption is a title.
        WeekCaption = i == week.Days.Count - 1 ? Loc.T("Stats.Today")
            : name.Length == 0 ? name : char.ToUpper(name[0], culture) + name[1..];
        WeekTotal = FormatTotal(week.DayTicks[i]);
        WeekPlays = Loc.Plural("Mobile.Count.Plays", week.DayPlays[i]);
    }

    private static int IndexOf(IReadOnlyList<DateTime> days, DateTime day)
    {
        for (var i = 0; i < days.Count; i++)
            if (days[i] == day) return i;
        return -1;
    }

    /// <summary>How often the log has each of <paramref name="ids"/> (skips count, as in the week's plays).</summary>
    private static Dictionary<Guid, int> PlayCounts(IReadOnlyList<PlayHistoryEvent> events, IEnumerable<Guid> ids)
    {
        var counts = ids.Distinct().ToDictionary(id => id, _ => 0);
        if (counts.Count == 0) return counts;
        foreach (var e in events)
            if (counts.TryGetValue(e.TrackId, out var n)) counts[e.TrackId] = n + 1;
        return counts;
    }

    /// <summary>"Artist · 12 plays"; just the plays for a song without an artist.</summary>
    private static string RowSub(Track track, int plays)
    {
        var count = Loc.Plural("Mobile.Count.Plays", plays);
        return string.IsNullOrWhiteSpace(track.Artist) ? count : $"{track.Artist} · {count}";
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
