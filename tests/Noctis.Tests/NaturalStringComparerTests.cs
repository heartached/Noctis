using System;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class NaturalStringComparerTests
{
    private static int C(string? x, string? y) => NaturalStringComparer.Instance.Compare(x, y);

    [Theory]
    [InlineData("2 foo", "10 foo")]   // numeric-aware: 2 < 10
    [InlineData("02 foo", "10 foo")]  // leading zeros still compare by value
    [InlineData("1-02", "1-10")]      // multiple digit runs
    [InlineData("Disc 1", "Disc 2")]
    [InlineData("9", "10")]
    [InlineData("abc", "abd")]
    [InlineData("abc", "abcd")]       // prefix sorts first
    [InlineData(null, "a")]           // null sorts first
    public void Compare_LeftSortsBeforeRight(string? left, string? right)
    {
        Assert.True(C(left, right) < 0);
        Assert.True(C(right, left) > 0);
    }

    [Theory]
    [InlineData("a", "a")]
    [InlineData("a", "A")]            // case-insensitive
    [InlineData("Track 07", "track 07")]
    [InlineData("", "")]
    public void Compare_Equal(string x, string y) => Assert.Equal(0, C(x, y));

    [Fact]
    public void Compare_EqualNumericValue_FewerLeadingZerosFirst()
    {
        Assert.True(C("1 foo", "01 foo") < 0);
        Assert.True(C("01 foo", "1 foo") > 0);
    }

    [Fact]
    public void Sort_ProducesExplorerLikeOrder()
    {
        var names = new[] { "10 b", "9 a", "1 x", "02 y" };
        Array.Sort(names, NaturalStringComparer.Instance);
        Assert.Equal(new[] { "1 x", "02 y", "9 a", "10 b" }, names);
    }
}
