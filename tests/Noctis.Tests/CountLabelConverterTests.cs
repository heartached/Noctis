using System.Globalization;
using Noctis.Converters;
using Avalonia.Headless.XUnit;
using Noctis.Localization;
using Xunit;

namespace Noctis.Tests;

[Collection("Localization")] // song / playlist counts read Loc.Instance, which some tests switch
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

    /// <summary>The sidebar's song and playlist counts follow the language (they were built in
    /// English: "4 songs" in French), with the language's own plural forms. On the Avalonia
    /// thread: a language switch re-labels bound controls other tests left alive.</summary>
    [AvaloniaFact]
    public void SongAndPlaylistCounts_AreTranslated()
    {
        try
        {
            Loc.Instance.SetCulture("fr");
            Assert.Equal("1 titre", Convert(1, "song"));
            Assert.Equal("4 titres", Convert(4, "song"));
            Loc.Instance.SetCulture("de");
            Assert.Equal("2 Playlists", Convert(2, "playlist"));
            Loc.Instance.SetCulture("ru");
            Assert.Equal("5 плейлистов", Convert(5, "playlist"));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }
}
