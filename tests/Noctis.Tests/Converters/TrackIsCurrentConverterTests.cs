using System;
using System.Collections.Generic;
using System.Globalization;
using Noctis.Converters;
using Xunit;

namespace Noctis.Tests.Converters;

public class TrackIsCurrentConverterTests
{
    private readonly TrackIsCurrentConverter _conv = new();

    [Fact]
    public void Returns_true_when_ids_match()
    {
        var id = Guid.NewGuid().ToString();
        var result = _conv.Convert(new List<object?> { id, id }, typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(true, result);
    }

    [Fact]
    public void Returns_false_when_ids_differ()
    {
        var result = _conv.Convert(
            new List<object?> { Guid.NewGuid().ToString(), Guid.NewGuid().ToString() },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Returns_false_when_current_is_null()
    {
        var result = _conv.Convert(
            new List<object?> { Guid.NewGuid().ToString(), null },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Returns_false_when_row_is_null()
    {
        var result = _conv.Convert(
            new List<object?> { null, Guid.NewGuid().ToString() },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }

    [Fact]
    public void Returns_false_when_both_null()
    {
        var result = _conv.Convert(
            new List<object?> { null, null },
            typeof(bool), null, CultureInfo.InvariantCulture);
        Assert.Equal(false, result);
    }
}
