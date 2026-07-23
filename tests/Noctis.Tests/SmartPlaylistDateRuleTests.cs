using System.Globalization;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Smart-playlist Before/After rules must mean the same thing on every OS
/// locale (ISO dates parsed invariant-first) while locally-typed formats keep
/// working via the culture fallback. Dates are chosen ≥2 days from each
/// boundary so results hold in every timezone.
/// </summary>
public class SmartPlaylistDateRuleTests
{
    private static Playlist SmartWith(RuleOperator op, string value) => new()
    {
        Name = "smart",
        IsSmartPlaylist = true,
        MatchAll = true,
        Rules = { new SmartPlaylistRule { Field = RuleField.DateAdded, Operator = op, Value = value } }
    };

    private static Track AddedOn(DateTime utc) => new()
    {
        Id = Guid.NewGuid(),
        Title = "T",
        FilePath = "x.mp3",
        DateAdded = utc
    };

    private static void WithCulture(string name, Action body)
    {
        var saved = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo(name);
        try { body(); }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData("en-US")]
    [InlineData("de-DE")] // day-first culture: ISO must not flip month/day
    public void IsoDates_MeanTheSame_OnEveryLocale(string culture)
    {
        WithCulture(culture, () =>
        {
            var old = AddedOn(new DateTime(2026, 5, 1, 12, 0, 0, DateTimeKind.Utc));
            var recent = AddedOn(new DateTime(2026, 7, 20, 12, 0, 0, DateTimeKind.Utc));
            var all = new[] { old, recent };

            var after = SmartPlaylistEvaluator.Evaluate(SmartWith(RuleOperator.After, "2026-07-01"), all);
            Assert.Equal(new[] { recent.Id }, after.Select(t => t.Id));

            var before = SmartPlaylistEvaluator.Evaluate(SmartWith(RuleOperator.Before, "2026-07-01"), all);
            Assert.Equal(new[] { old.Id }, before.Select(t => t.Id));
        });
    }

    [Fact]
    public void LocalCultureFormat_StillWorks_ViaFallback()
    {
        WithCulture("de-DE", () =>
        {
            var recent = AddedOn(new DateTime(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc));

            // German day-first format: not invariant-parseable, must hit the fallback.
            var after = SmartPlaylistEvaluator.Evaluate(
                SmartWith(RuleOperator.After, "01.07.2026"), new[] { recent });

            Assert.Single(after);
        });
    }

    [Fact]
    public void UnparseableDate_MatchesNothing()
    {
        var track = AddedOn(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        var result = SmartPlaylistEvaluator.Evaluate(
            SmartWith(RuleOperator.After, "not a date"), new[] { track });
        Assert.Empty(result);
    }
}
