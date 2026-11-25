using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>The album page footer ("2 songs, 0 min" for a 40-second album): the length was cut
/// down to whole minutes instead of rounded, so a short album read 0 min and 3:59 read 3 min.</summary>
public class AlbumDurationFormatTests
{
    private static Album WithLength(TimeSpan length) => new() { TotalDuration = length };

    [Theory]
    [InlineData(40, "1 min")]          // under a minute still shows a length
    [InlineData(239, "4 min")]         // 3:59 rounds to 4
    [InlineData(150, "3 min")]         // 2:30 rounds up
    [InlineData(149, "2 min")]
    [InlineData(0, "0 min")]
    [InlineData(3 * 3600 + 59, "3h 0m")]
    public void TotalDurationFormatted_RoundsToTheNearestMinute(int seconds, string expected)
        => Assert.Equal(expected, WithLength(TimeSpan.FromSeconds(seconds)).TotalDurationFormatted);
}
