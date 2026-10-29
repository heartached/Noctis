using System.Globalization;

namespace Noctis.Plugins.AlbumRoulette.Roulette;

/// <summary>The last Album of the Day, kept in the plugin's data folder so it comes once a day.</summary>
public sealed class DailyState
{
    /// <summary>The local day it was picked ("2026-10-29").</summary>
    public string Day { get; set; } = "";
    public string AlbumKey { get; set; } = "";
    public string Title { get; set; } = "";
}

/// <summary>
/// Album of the Day: one album per local day, from the hour the user set. The pick is stable:
/// the same library on the same day always gives the same album (a hash of the date into the
/// albums sorted by key), so restarting Noctis never changes it.
/// </summary>
public static class AlbumOfTheDay
{
    public static string DayKey(DateTimeOffset localNow) => localNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>Due: the feature is on, the hour has come, and today has no pick yet.</summary>
    public static bool IsDue(DateTimeOffset localNow, int hour, DailyState? last)
        => localNow.Hour >= Math.Clamp(hour, 0, 23) && last?.Day != DayKey(localNow);

    /// <summary>The day's album among <paramref name="albums"/> (compilations left out when asked), or null for none.</summary>
    public static RouletteAlbum? Pick(IReadOnlyList<RouletteAlbum> albums, DateTimeOffset localNow, bool skipCompilations)
    {
        var candidates = albums.Where(a => !(skipCompilations && a.IsCompilation))
            .OrderBy(a => a.Key, StringComparer.Ordinal).ToList();
        if (candidates.Count == 0) return null;
        var h = Hash(DayKey(localNow));
        return candidates[(int)(h % (uint)candidates.Count)];
    }

    /// <summary>"Today's album: Title by Artist. It turns 20 this year."</summary>
    public static string Message(RouletteAlbum album, int thisYear)
    {
        var text = "Today's album: " + album.Title;
        if (album.Artist.Length > 0) text += " by " + album.Artist;
        text += ".";
        if (Anniversary(album.Year, thisYear) is { } turns) text += " " + turns;
        return text;
    }

    /// <summary>"It turns 20 this year." from the year tag; null when the year is unknown or this year's.</summary>
    public static string? Anniversary(int year, int thisYear)
    {
        if (year <= 0 || year >= thisYear) return null;
        var age = thisYear - year;
        return "It turns " + age.ToString(CultureInfo.InvariantCulture) + " this year.";
    }

    /// <summary>How long until the next look: the pick hour today or tomorrow, at most an hour away
    /// (the clock may jump: sleep, a time zone change).</summary>
    public static TimeSpan UntilNextCheck(DateTimeOffset localNow, int hour, bool doneToday)
    {
        hour = Math.Clamp(hour, 0, 23);
        var today = new DateTimeOffset(localNow.Year, localNow.Month, localNow.Day, hour, 0, 0, localNow.Offset);
        var next = doneToday || localNow >= today ? today.AddDays(1) : today;
        if (!doneToday && localNow >= today) return TimeSpan.FromSeconds(5);
        var wait = next - localNow;
        return wait > TimeSpan.FromHours(1) ? TimeSpan.FromHours(1) : wait < TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait;
    }

    /// <summary>FNV-1a, mixed (stable across runs and machines, unlike string.GetHashCode).</summary>
    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (var ch in s) h = (h ^ ch) * 16777619;
        h ^= h >> 15;
        h *= 0x2C1B3C6D;
        h ^= h >> 12;
        return h;
    }
}
