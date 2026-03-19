using Noctis.Models;

namespace Noctis.Services;

/// <summary>
/// Evaluates smart playlist rules against a set of tracks.
/// Stateless helper — all methods are static.
/// </summary>
public static class SmartPlaylistEvaluator
{
    /// <summary>
    /// Filters tracks based on smart playlist configuration.
    /// </summary>
    public static List<Track> Evaluate(Playlist playlist, IReadOnlyList<Track> allTracks)
    {
        if (!playlist.IsSmartPlaylist || playlist.Rules.Count == 0)
            return new List<Track>();

        var filtered = allTracks.Where(track =>
        {
            if (playlist.MatchAll)
                return playlist.Rules.All(rule => EvaluateRule(rule, track));
            else
                return playlist.Rules.Any(rule => EvaluateRule(rule, track));
        });

        if (playlist.LimitCount is > 0)
        {
            var sorted = ApplySort(filtered, playlist.SortBy ?? SmartPlaylistSortBy.MostPlayed);
            return sorted.Take(playlist.LimitCount.Value).ToList();
        }

        return filtered.ToList();
    }

    /// <summary>
    /// Returns the set of valid operators for a given rule field.
    /// </summary>
    public static RuleOperator[] GetOperatorsForField(RuleField field)
    {
        return field switch
        {
            RuleField.Title or RuleField.Artist or RuleField.Album or
            RuleField.Genre or RuleField.Composer or RuleField.Codec
                => [RuleOperator.Contains, RuleOperator.Equals, RuleOperator.StartsWith,
                    RuleOperator.EndsWith, RuleOperator.DoesNotContain],

            RuleField.Year or RuleField.PlayCount or RuleField.Duration
                => [RuleOperator.Equals, RuleOperator.GreaterThan,
                    RuleOperator.LessThan, RuleOperator.Between],

            RuleField.DateAdded or RuleField.LastPlayed
                => [RuleOperator.Before, RuleOperator.After, RuleOperator.InLastNDays],

            RuleField.IsFavorite or RuleField.IsLossless or RuleField.IsExplicit
                => [RuleOperator.IsTrue, RuleOperator.IsFalse],

            _ => [RuleOperator.Equals]
        };
    }

    /// <summary>
    /// Returns a friendly display name for a RuleField enum value.
    /// </summary>
    public static string GetFieldDisplayName(RuleField field) => field switch
    {
        RuleField.Title => "Title",
        RuleField.Artist => "Artist",
        RuleField.Album => "Album",
        RuleField.Genre => "Genre",
        RuleField.Composer => "Composer",
        RuleField.Codec => "Codec",
        RuleField.Year => "Year",
        RuleField.PlayCount => "Play Count",
        RuleField.Duration => "Duration (sec)",
        RuleField.DateAdded => "Date Added",
        RuleField.LastPlayed => "Last Played",
        RuleField.IsFavorite => "Is Favorite",
        RuleField.IsLossless => "Is Lossless",
        RuleField.IsExplicit => "Is Explicit",
        _ => field.ToString()
    };

    /// <summary>
    /// Returns a friendly display name for a RuleOperator enum value.
    /// </summary>
    public static string GetOperatorDisplayName(RuleOperator op) => op switch
    {
        RuleOperator.Contains => "contains",
        RuleOperator.Equals => "equals",
        RuleOperator.StartsWith => "starts with",
        RuleOperator.EndsWith => "ends with",
        RuleOperator.DoesNotContain => "does not contain",
        RuleOperator.GreaterThan => "greater than",
        RuleOperator.LessThan => "less than",
        RuleOperator.Between => "between",
        RuleOperator.IsTrue => "is true",
        RuleOperator.IsFalse => "is false",
        RuleOperator.Before => "before",
        RuleOperator.After => "after",
        RuleOperator.InLastNDays => "in last N days",
        _ => op.ToString()
    };

    /// <summary>
    /// Returns a friendly display name for a SmartPlaylistSortBy enum value.
    /// </summary>
    public static string GetSortDisplayName(SmartPlaylistSortBy sort) => sort switch
    {
        SmartPlaylistSortBy.MostPlayed => "Most Played",
        SmartPlaylistSortBy.LeastPlayed => "Least Played",
        SmartPlaylistSortBy.RecentlyAdded => "Recently Added",
        SmartPlaylistSortBy.RecentlyPlayed => "Recently Played",
        SmartPlaylistSortBy.Title => "Title",
        SmartPlaylistSortBy.Artist => "Artist",
        SmartPlaylistSortBy.Random => "Random",
        _ => sort.ToString()
    };

    private static bool EvaluateRule(SmartPlaylistRule rule, Track track)
    {
        return rule.Field switch
        {
            RuleField.Title => EvaluateString(track.Title ?? "", rule),
            RuleField.Artist => EvaluateString(track.Artist ?? "", rule),
            RuleField.Album => EvaluateString(track.Album ?? "", rule),
            RuleField.Genre => EvaluateString(track.Genre ?? "", rule),
            RuleField.Composer => EvaluateString(track.Composer ?? "", rule),
            RuleField.Codec => EvaluateString(track.Codec ?? "", rule),
            RuleField.Year => EvaluateNumeric(track.Year, rule),
            RuleField.PlayCount => EvaluateNumeric(track.PlayCount, rule),
            RuleField.Duration => EvaluateNumeric((int)track.Duration.TotalSeconds, rule),
            RuleField.DateAdded => EvaluateDate(track.DateAdded, rule),
            RuleField.LastPlayed => track.LastPlayed.HasValue
                ? EvaluateDate(track.LastPlayed.Value, rule)
                : rule.Operator == RuleOperator.IsFalse,
            RuleField.IsFavorite => EvaluateBoolean(track.IsFavorite, rule),
            RuleField.IsLossless => EvaluateBoolean(track.IsLossless, rule),
            RuleField.IsExplicit => EvaluateBoolean(track.IsExplicit, rule),
            _ => false
        };
    }

    private static bool EvaluateString(string fieldValue, SmartPlaylistRule rule)
    {
        var value = rule.Value ?? "";
        return rule.Operator switch
        {
            RuleOperator.Contains => fieldValue.Contains(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.Equals => fieldValue.Equals(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.StartsWith => fieldValue.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.EndsWith => fieldValue.EndsWith(value, StringComparison.OrdinalIgnoreCase),
            RuleOperator.DoesNotContain => !fieldValue.Contains(value, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool EvaluateNumeric(int fieldValue, SmartPlaylistRule rule)
    {
        if (!int.TryParse(rule.Value, out var target)) return false;

        return rule.Operator switch
        {
            RuleOperator.Equals => fieldValue == target,
            RuleOperator.GreaterThan => fieldValue > target,
            RuleOperator.LessThan => fieldValue < target,
            RuleOperator.Between => int.TryParse(rule.Value2, out var upper)
                                    && fieldValue >= target && fieldValue <= upper,
            _ => false
        };
    }

    private static bool EvaluateDate(DateTime fieldValue, SmartPlaylistRule rule)
    {
        return rule.Operator switch
        {
            RuleOperator.Before => DateTime.TryParse(rule.Value, out var before) && fieldValue < before,
            RuleOperator.After => DateTime.TryParse(rule.Value, out var after) && fieldValue > after,
            RuleOperator.InLastNDays => int.TryParse(rule.Value, out var days)
                                        && fieldValue >= DateTime.UtcNow.AddDays(-days),
            _ => false
        };
    }

    private static bool EvaluateBoolean(bool fieldValue, SmartPlaylistRule rule)
    {
        return rule.Operator switch
        {
            RuleOperator.IsTrue => fieldValue,
            RuleOperator.IsFalse => !fieldValue,
            _ => false
        };
    }

    private static IEnumerable<Track> ApplySort(IEnumerable<Track> tracks, SmartPlaylistSortBy sortBy)
    {
        return sortBy switch
        {
            SmartPlaylistSortBy.MostPlayed => tracks.OrderByDescending(t => t.PlayCount),
            SmartPlaylistSortBy.LeastPlayed => tracks.OrderBy(t => t.PlayCount),
            SmartPlaylistSortBy.RecentlyAdded => tracks.OrderByDescending(t => t.DateAdded),
            SmartPlaylistSortBy.RecentlyPlayed => tracks.OrderByDescending(t => t.LastPlayed ?? DateTime.MinValue),
            SmartPlaylistSortBy.Title => tracks.OrderBy(t => t.Title, StringComparer.OrdinalIgnoreCase),
            SmartPlaylistSortBy.Artist => tracks.OrderBy(t => t.Artist, StringComparer.OrdinalIgnoreCase),
            SmartPlaylistSortBy.Random => tracks.OrderBy(_ => Random.Shared.Next()),
            _ => tracks
        };
    }
}
