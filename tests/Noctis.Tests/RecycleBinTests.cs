using System;
using System.IO;
using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

public class RecycleBinTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryMoveToTrash_EmptyPath_ReturnsFalse(string? path)
    {
        Assert.False(RecycleBin.TryMoveToTrash(path!));
    }

    [Fact]
    public void TryMoveToTrash_NonexistentFile_ReturnsFalseWithoutThrowing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"noctis-missing-{Guid.NewGuid():N}.tmp");

        // Safe fallback: a missing file is a no-op, never throws, never deletes anything.
        Assert.False(RecycleBin.TryMoveToTrash(path));
    }
}
