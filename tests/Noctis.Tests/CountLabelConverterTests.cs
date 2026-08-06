using System.Globalization;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests;

public class CountLabelConverterTests
{
    private static string Convert(object? value, string noun) =>
        (string)CountLabelConverter.Instance.Convert(value, typeof(string), noun, CultureInfo.InvariantCulture);

    [Theory]
    [InlineData(0, "0 playlists")]
    [InlineData(1, "1 playlist")]
    [InlineData(2, "2 playlists")]
    [InlineData(17, "17 playlists")]
    public void PluralizesOnlyAtOne(int count, string expected)
    {
        Assert.Equal(expected, Convert(count, "playlist"));
    }

    [Fact]
    public void UsesTheGivenNoun()
    {
        Assert.Equal("1 song", Convert(1, "song"));
        Assert.Equal("3 songs", Convert(3, "song"));
    }

    [Fact]
    public void NullCountReadsAsZero()
    {
        Assert.Equal("0 songs", Convert(null, "song"));
    }
}
