namespace Noctis.Mobile.ViewModels;

/// <summary>
/// Settings › Library › Scan Library (Discord #mobile-beta, beta 6): when the start of the app
/// rescans the folders. Rescan Library always scans. The names are the persisted values
/// (AppSettings.PhoneScanSchedule), so they must not be renamed. Manual is not saved there: it is
/// AppSettings.ScanOnStartup off, the switch this choice grew from.
/// </summary>
public enum ScanSchedule { EveryStart, Daily, Weekly, Manual }

public static class ScanSchedules
{
    /// <summary>
    /// Whether a start of the app at <paramref name="nowUtc"/> should scan, the last finished scan
    /// having been at <paramref name="lastScanUtc"/> (null: none recorded). Daily and Weekly go by
    /// the calendar days of <paramref name="zone"/> (the phone's own by default), not by 24-hour
    /// spans: opened every morning at about the same time, Daily scans each morning, not every
    /// other one. A last scan later than now (the clock was set back) is not trusted: due.
    /// </summary>
    public static bool IsDue(ScanSchedule schedule, DateTime? lastScanUtc, DateTime nowUtc, TimeZoneInfo? zone = null)
    {
        if (schedule == ScanSchedule.Manual) return false;
        if (schedule == ScanSchedule.EveryStart || lastScanUtc is not { } last || last > nowUtc) return true;
        zone ??= TimeZoneInfo.Local;
        var days = (LocalDate(nowUtc, zone) - LocalDate(last, zone)).Days;
        return days >= (schedule == ScanSchedule.Weekly ? 7 : 1);
    }

    private static DateTime LocalDate(DateTime utc, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone).Date;

    /// <summary>The saved interval read back; empty, unknown or Manual is <see cref="ScanSchedule.EveryStart"/>.</summary>
    public static ScanSchedule ParseInterval(string? key) =>
        Enum.TryParse<ScanSchedule>(key, ignoreCase: true, out var schedule) && Enum.IsDefined(schedule)
            && schedule != ScanSchedule.Manual && !int.TryParse(key, out _) ? schedule : ScanSchedule.EveryStart;

    /// <summary>What is saved: empty for Every Start (the default), so a settings file without a choice means it.</summary>
    public static string Key(ScanSchedule interval) => interval is ScanSchedule.Daily or ScanSchedule.Weekly ? interval.ToString() : "";
}
