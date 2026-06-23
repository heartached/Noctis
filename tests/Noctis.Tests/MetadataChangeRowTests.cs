using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

public class MetadataChangeRowTests
{
    [Fact]
    public void TryCreate_ReturnsNull_WhenNewValueEmpty()
    {
        Assert.Null(MetadataChangeRow.TryCreate("title", "Old", "", () => { }));
        Assert.Null(MetadataChangeRow.TryCreate("title", "Old", null, () => { }));
    }

    [Fact]
    public void TryCreate_ReturnsNull_WhenUnchanged()
    {
        Assert.Null(MetadataChangeRow.TryCreate("year", "2018", "2018", () => { }));
        Assert.Null(MetadataChangeRow.TryCreate("year", " 2018 ", "2018", () => { })); // trimmed compare
    }

    [Fact]
    public void TryCreate_ReturnsRow_WhenChanged()
    {
        var row = MetadataChangeRow.TryCreate("genre", "Hip-Hop", "Rap", () => { });
        Assert.NotNull(row);
        Assert.Equal("genre", row!.Field);
        Assert.Equal("Hip-Hop", row.OldValue);
        Assert.Equal("Rap", row.NewValue);
        Assert.True(row.Apply);
    }

    [Fact]
    public void ApplyIfChecked_RunsAction_OnlyWhenChecked()
    {
        var ran = 0;
        var row = MetadataChangeRow.TryCreate("title", "A", "B", () => ran++)!;

        row.Apply = false;
        row.ApplyIfChecked();
        Assert.Equal(0, ran);

        row.Apply = true;
        row.ApplyIfChecked();
        Assert.Equal(1, ran);
    }
}
