using Xunit;
using Noctis.Converters;

namespace Noctis.Tests;

public class KeepHangulWordsTests
{
    [Fact]
    public void Apply_JoinsAdjacentSyllables_Only()
    {
        Assert.Equal("다\u2060른 문\u2060을", KeepHangulWordsConverter.Apply("다른 문을"));
        Assert.Equal("Blue Blood", KeepHangulWordsConverter.Apply("Blue Blood"));
        Assert.Equal("I 갈 you", KeepHangulWordsConverter.Apply("I 갈 you"));   // lone syllables: nothing to join
        var plain = "no hangul here";
        Assert.Same(plain, KeepHangulWordsConverter.Apply(plain));
    }
}
