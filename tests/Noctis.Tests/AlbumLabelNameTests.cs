using System.Collections.Generic;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards the label-name extraction from copyright notices shown in the album
/// description dialog: marks/years stripped, clause breaks cut, legalese rejected.
/// </summary>
public class AlbumLabelNameTests
{
    private static Album WithCopyright(string copyright) =>
        new() { Tracks = new List<Track> { new() { Copyright = copyright } } };

    [Theory]
    [InlineData("℗ 2014 Big Machine Records, LLC.", "Big Machine Records")]
    [InlineData("(P) 2023 Republic Records, a division of UMG Recordings, Inc.", "Republic Records")]
    [InlineData("℗ 2023 Taylor Swift, under exclusive license to Republic Records", "Taylor Swift")]
    [InlineData("© 2020 XL Recordings Ltd", "XL Recordings Ltd")]
    [InlineData("2016 Top Dawg Entertainment", "Top Dawg Entertainment")]
    [InlineData("℗ & © 2019 Columbia Records", "Columbia Records")]
    public void LabelName_ExtractsCleanName(string copyright, string expected)
        => Assert.Equal(expected, WithCopyright(copyright).LabelName);

    [Theory]
    [InlineData("")]
    [InlineData("℗ 2014")]
    [InlineData("This compilation is licensed exclusively without limitation for worldwide distribution in perpetuity")]
    public void LabelName_EmptyWhenNothingNameLike(string copyright)
        => Assert.False(WithCopyright(copyright).HasLabelName);
}
