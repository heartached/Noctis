using System.Globalization;

namespace Noctis.Plugins.AlbumTown.Town;

/// <summary>What two residents do when they meet: talk, or a little game with its own moves.</summary>
public enum ChatAct { Talk, RockPaperScissors, Scuffle, HighFive }

/// <summary>A two-line chat: the one who opens says <see cref="Opener"/>, the other replies.
/// <see cref="Act"/>: what their hands and bodies do meanwhile.</summary>
public sealed record Chat(string Topic, string Opener, string Reply)
{
    public ChatAct Act { get; init; } = ChatAct.Talk;
}

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

    /// <summary>How often a meeting is a game (rock-paper-scissors, a scuffle, a high five) and how
    /// often a tease, instead of small talk (Discord, Jafezy 11-02: "more interactions").</summary>
    public const double GameChance = 0.3;
    public const double TeaseChance = 0.15;

    private static readonly string[] Throws = { "Rock", "Paper", "Scissors" };

    /// <summary>
    /// What two residents do when they meet: mostly <see cref="Between"/> (small talk), sometimes
    /// a game or a tease, and now and then, when <paramref name="lyric"/> gives one, a line from
    /// the opener's own songs.
    /// </summary>
    public static Chat Meet(TownAlbum a, TownAlbum b, DateTimeOffset now, Random random, Func<TownAlbum, string?>? lyric = null)
    {
        var roll = random.NextDouble();
        if (roll < GameChance) return Game(a, b, random);
        if (roll < GameChance + TeaseChance) return Tease(a, b, random);
        if (roll < GameChance + TeaseChance + 0.15 && lyric?.Invoke(a) is { Length: > 0 } line)
            return Quote(line, random);
        return Between(a, b, now, random);
    }

    /// <summary>Rock-paper-scissors, a playful scuffle or a high five.</summary>
    public static Chat Game(TownAlbum a, TownAlbum b, Random random)
    {
        switch (random.Next(3))
        {
            case 0:
            {
                var mine = random.Next(3);
                var theirs = random.Next(3);
                var reply = mine == theirs ? $"{Throws[theirs]}! Again?!"
                    : (theirs - mine + 3) % 3 == 1 ? $"{Throws[theirs]}! I win!"
                    : $"{Throws[theirs]}... you win.";
                return new Chat("rps", $"Rock, paper, scissors... {Throws[mine]}!", reply) { Act = ChatAct.RockPaperScissors };
            }
            case 1:
                return new Chat("scuffle", Pick(random, "Put 'em up!", "You want some of this?", $"Say that again, {Short(b.Title)}!"),
                    Pick(random, "Oh, it's ON.", "Hey, not the cover!", "Bring it!")) { Act = ChatAct.Scuffle };
            default:
                return new Chat("highfive", Pick(random, "High five!", "Up top!"), Pick(random, "Yeah!", "Nice one!")) { Act = ChatAct.HighFive };
        }
    }

    /// <summary>A little cheek between neighbours (the residents can be rude to each other; the users love it).</summary>
    public static Chat Tease(TownAlbum a, TownAlbum b, Random random)
    {
        var c = CultureInfo.InvariantCulture;
        var options = new List<Chat>
        {
            new("tease", "Your cover is... a choice.", "At least I have one."),
            new("tease", "Nobody skips me.", "Nobody plays you."),
            new("tease", "Step aside, I'm on repeat.", "Since when?"),
        };
        if (b.PlayCount > a.PlayCount && a.PlayCount > 0)
            options.Add(new("tease", $"Only {Plays(b.PlayCount)}? Cute.", $"More than your {Plays(a.PlayCount)}."));
        if (a.Year > 0 && b.Year > 0 && a.Year != b.Year)
            options.Add(new("tease", a.Year < b.Year ? "I was a classic before you were born." : "Old music. Classic.", $"Says the one from {a.Year.ToString(c)}."));
        if (!a.IsSingle && b.IsSingle)
            options.Add(new("tease", "One song? That's it?", "Quality over quantity."));
        return options[random.Next(options.Count)];
    }

    /// <summary>A resident quotes a line from one of its songs; the other is impressed.</summary>
    public static Chat Quote(string line, Random random)
        => new("lyric", "\"" + Shorten(line.Trim(), 60) + "\"", Pick(random, "Nice line!", "Sing it!", "That one's stuck in my head now."));

    /// <summary>A lyric line worth quoting from <paramref name="lines"/> (a real line of words, not
    /// a [Chorus] marker or a single word), or null.</summary>
    public static string? PickLyric(IReadOnlyList<string> lines, Random random)
    {
        var start = lines.Count == 0 ? 0 : random.Next(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[(start + i) % lines.Count].Trim();
            if (line.Length < 10 || line.Length > 90 || line.StartsWith('[') || line.StartsWith('(') || !line.Contains(' ')) continue;
            return line;
        }
        return null;
    }

    private static string Pick(Random random, params string[] lines) => lines[random.Next(lines.Length)];

    /// <summary>What a newcomer says as it walks into the plaza.</summary>
    public static string Arrival(TownAlbum a) => $"Hi everyone! {Short(a.Title.Length == 0 ? "Untitled" : a.Title)} is here!";

    /// <summary>What a resident says on its way out to make room.</summary>
    public const string Leaving = "Gotta go. Bye!";

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
            yield return new Chat("genre", $"Another {Short(a.Genre)} fan?", "Always.");
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

    /// <summary>A click: sometimes (when <paramref name="lyric"/> has one) a line from its songs, else <see cref="Greeting"/>.</summary>
    public static string Hello(TownAlbum a, DateTimeOffset now, Random random, Func<TownAlbum, string?>? lyric)
        => random.NextDouble() < 0.35 && lyric?.Invoke(a) is { Length: > 0 } line ? "\"" + Shorten(line.Trim(), 60) + "\"" : Greeting(a, now);

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

    private static string Short(string s) => Shorten(s, 24);

    /// <summary>
    /// <paramref name="s"/> cut to at most <paramref name="max"/> characters with "..." when longer,
    /// never splitting a surrogate pair (an emoji or a rare CJK character in a tag would otherwise
    /// end the text with half a character).
    /// </summary>
    public static string Shorten(string s, int max)
    {
        if (s.Length <= max) return s;
        var n = Math.Max(1, max - 1);
        if (char.IsHighSurrogate(s[n - 1])) n--;
        return s[..n].TrimEnd() + "...";
    }
}
