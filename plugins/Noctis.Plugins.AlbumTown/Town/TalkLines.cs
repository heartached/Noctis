using System.Globalization;

namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>A two-line chat: <see cref="First"/> says <see cref="Opener"/>, the other replies.</summary>
public sealed record Chat(string Topic, string Opener, string Reply);

/// <summary>
/// Small talk built from real metadata: shared artist, year, genre, play counts, how long since
/// a play. No network and no language model: a list of topics that apply to the pair, the most
/// specific ones first, one picked at random among the best few.
/// </summary>
public static class TalkLines
{
    /// <summary>A resident not played for this long says it misses you.</summary>
    public static readonly TimeSpan Lonely = TimeSpan.FromDays(60);

    public static Chat Between(TownAlbum a, TownAlbum b, DateTimeOffset now, Random random)
    {
        var options = Topics(a, b, now).ToList();
        // The first topics are the most specific; pick among the top three so pairs vary.
        var top = Math.Min(3, options.Count);
        return options[random.Next(top)];
    }

    /// <summary>Every topic that applies to the pair, most specific first. Never empty.</summary>
    public static IEnumerable<Chat> Topics(TownAlbum a, TownAlbum b, DateTimeOffset now)
    {
        var c = CultureInfo.InvariantCulture;
        if (a.Artist.Length > 0 && string.Equals(a.Artist, b.Artist, StringComparison.OrdinalIgnoreCase))
            yield return new Chat("artist", "Same artist, cousin!", $"Family reunion! Say hi to {Short(a.Artist)}.");
        if (a.Year > 0 && a.Year == b.Year)
            yield return new Chat("year", $"We're both from {a.Year.ToString(c)}!", "What a year that was.");
        if (IsLonely(a, now))
            yield return new Chat("lonely", "You haven't played me in a while...", "Same here. Maybe today?");
        if (a.PlayCount >= 10 && a.PlayCount >= b.PlayCount * 2)
            yield return new Chat("plays", $"I've been played {a.PlayCount.ToString(c)} times.", b.PlayCount == 0 ? "I'm still waiting for my first." : $"Only {Plays(b.PlayCount)} for me.");
        if (a.Genre.Length > 0 && string.Equals(a.Genre, b.Genre, StringComparison.OrdinalIgnoreCase))
            yield return new Chat("genre", $"Another {a.Genre} fan?", "Always.");
        if (a.Year > 0 && b.Year > 0 && Math.Abs(a.Year - b.Year) >= 20)
            yield return new Chat("decades", $"{Math.Abs(a.Year - b.Year).ToString(c)} years apart and still friends.", a.Year < b.Year ? "Respect your elders!" : "You kids and your music.");
        if (a.IsSingle && !b.IsSingle)
            yield return new Chat("single", "Just one song here. I'm a single.", $"I've got {b.TrackCount.ToString(c)}. Want to borrow some?");
        if (!a.IsSingle && a.TrackCount >= 15)
            yield return new Chat("long", $"I have {a.TrackCount.ToString(c)} songs inside me.", "No wonder you walk so slow.");
        if (a.IsFavorite)
            yield return new Chat("favorite", "Psst. I'm a favorite.", "Lucky! Tell me your secret.");
        if (a.PlayCount == 0)
            yield return new Chat("fresh", "Nobody has played me yet.", "Your time will come.");
        yield return new Chat("hello", "Nice day in the plaza.", "Perfect for a walk.");
    }

    /// <summary>What a resident says when you click it.</summary>
    public static string Greeting(TownAlbum a, DateTimeOffset now)
    {
        var c = CultureInfo.InvariantCulture;
        if (a.PlayCount == 0) return "Hi! Play me sometime?";
        if (IsLonely(a, now)) return "You came back! It's been a while.";
        return a.PlayCount == 1 ? "Hi! You've played me once." : $"Hi! {a.PlayCount.ToString(c)} plays and counting.";
    }

    public static bool IsLonely(TownAlbum a, DateTimeOffset now)
        => a.LastPlayed is { } last ? now - last >= Lonely : a.PlayCount > 0;

    private static string Plays(int n) => n == 1 ? "1 play" : n.ToString(CultureInfo.InvariantCulture) + " plays";

    private static string Short(string s) => s.Length <= 24 ? s : s[..23].TrimEnd() + "...";
}
