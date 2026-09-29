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

    [Theory]
    [InlineData(@"\\nas\music\Album\01.flac")]
    [InlineData(@"\\?\UNC\nas\music\Album\01.flac")]
    public void IsRecyclableVolume_UncPath_IsRefused(string path)
    {
        // UNC shares have no Recycle Bin: the shell would delete permanently.
        Assert.False(RecycleBin.IsRecyclableVolume(path, _ => DriveType.Fixed));
    }

    [Theory]
    [InlineData(DriveType.Network, false)]
    [InlineData(DriveType.Removable, false)]
    [InlineData(DriveType.Ram, false)]
    [InlineData(DriveType.CDRom, false)]
    [InlineData(DriveType.Fixed, true)]
    public void IsRecyclableVolume_OnlyFixedDrivesHaveARecycleBin(DriveType type, bool expected)
    {
        if (!OperatingSystem.IsWindows())
            return; // Drive-letter roots are Windows-only.

        string? asked = null;
        var result = RecycleBin.IsRecyclableVolume(@"Z:\Music\Album\01.flac", root => { asked = root; return type; });

        Assert.Equal(@"Z:\", asked);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, true, true)]       // "No" at the permanent-delete prompt
    [InlineData(1223, false, true)]   // ERROR_CANCELLED
    [InlineData(0x75, false, true)]   // DE_OPCANCELLED
    [InlineData(0, false, false)]     // recycled
    [InlineData(2, false, false)]     // missing path (measured)
    [InlineData(32, false, false)]    // file in use (measured)
    [InlineData(124, false, false)]   // folder holding an open file (measured)
    public void WasDeclined_OnlyCancelsCountAsNo(int result, bool aborted, bool expected)
    {
        Assert.Equal(expected, RecycleBin.WasDeclined(result, aborted));
    }
}
