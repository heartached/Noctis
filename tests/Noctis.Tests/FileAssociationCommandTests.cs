using Noctis.Helpers;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The Windows Open-with registration's pure half: reading the exe back out of a
/// recorded command, whole-path (not substring) matching, and the silent re-point
/// rule — registration follows the app when it moved, never when another copy still
/// exists at the recorded path.
/// </summary>
public class FileAssociationCommandTests
{
    [Theory]
    [InlineData("\"C:\\Apps\\Noctis\\Noctis.exe\" \"%1\"", "C:\\Apps\\Noctis\\Noctis.exe")]
    [InlineData("C:\\Noctis.exe \"%1\"", "C:\\Noctis.exe")]
    [InlineData("  \"D:\\a b\\Noctis.exe\"  ", "D:\\a b\\Noctis.exe")]
    public void ExtractExePath_ReadsQuotedAndUnquotedCommands(string command, string expected)
        => Assert.Equal(expected, FileAssociationCommand.ExtractExePath(command));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ExtractExePath_BlankIsNull(string? command)
        => Assert.Null(FileAssociationCommand.ExtractExePath(command));

    [Fact]
    public void Format_RoundTripsThroughExtract()
    {
        const string exe = @"C:\Program Files\Noctis\Noctis.exe";
        Assert.Equal(exe, FileAssociationCommand.ExtractExePath(FileAssociationCommand.Format(exe)));
    }

    [Fact]
    public void PointsAt_IsWholePathNotSubstring()
    {
        var recorded = FileAssociationCommand.Format(@"C:\Noctis\Noctis.exe.bak");
        // The old Contains() check said "registered" here because the exe path is a
        // substring of the recorded one.
        Assert.False(FileAssociationCommand.PointsAt(recorded, @"C:\Noctis\Noctis.exe"));
        Assert.True(FileAssociationCommand.PointsAt(recorded, @"C:\Noctis\Noctis.exe.bak"));
        Assert.True(FileAssociationCommand.PointsAt(recorded, @"c:\noctis\NOCTIS.EXE.BAK"));
    }

    [Fact]
    public void ShouldRepoint_NeverRegistered_IsFalse()
        => Assert.False(FileAssociationCommand.ShouldRepoint(null, @"C:\New\Noctis.exe", _ => false));

    [Fact]
    public void ShouldRepoint_AlreadyThisCopy_IsFalse()
    {
        var recorded = FileAssociationCommand.Format(@"C:\New\Noctis.exe");
        Assert.False(FileAssociationCommand.ShouldRepoint(recorded, @"C:\New\Noctis.exe", _ => true));
    }

    [Fact]
    public void ShouldRepoint_RecordedExeStillExists_LeavesItAlone()
    {
        // A dev build next to the installed copy must not steal the registration.
        var recorded = FileAssociationCommand.Format(@"C:\Program Files\Noctis\Noctis.exe");
        Assert.False(FileAssociationCommand.ShouldRepoint(recorded, @"C:\Dev\Noctis.exe", _ => true));
    }

    [Fact]
    public void ShouldRepoint_RecordedExeGone_IsTrue()
    {
        var recorded = FileAssociationCommand.Format(@"C:\Old\Noctis.exe");
        Assert.True(FileAssociationCommand.ShouldRepoint(recorded, @"C:\New\Noctis.exe",
            path => !string.Equals(path, @"C:\Old\Noctis.exe", StringComparison.OrdinalIgnoreCase)));
    }
}
