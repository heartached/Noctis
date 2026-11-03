using System.IO;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// App Icon on a pinned taskbar button (11-03): only the pinned shortcut of THIS exe is
/// repointed; classic resets it to the exe's own icon.
/// </summary>
public class PinnedShortcutIconTests
{
    [Theory]
    [InlineData(@"G:\Noctis\Noctis.exe", @"G:\Noctis\Noctis.exe", true)]
    [InlineData(@"g:\noctis\NOCTIS.EXE", @"G:\Noctis\Noctis.exe", true)]
    [InlineData(@"G:\Noctis\.\Noctis.exe", @"G:\Noctis\Noctis.exe", true)]
    [InlineData(@"C:\dev\bin\Debug\Noctis.exe", @"G:\Noctis\Noctis.exe", false)]
    [InlineData("", @"G:\Noctis\Noctis.exe", false)]
    [InlineData(null, @"G:\Noctis\Noctis.exe", false)]
    public void Only_this_exe_matches(string? target, string exe, bool expected) =>
        Assert.Equal(expected, PinnedShortcutIcon.IsSameExe(target, exe));

    [Fact]
    public void Classic_resets_to_exe_icon_others_point_at_copied_ico()
    {
        Assert.Equal("", PinnedShortcutIcon.DesiredIconPath(AppIconService.DefaultId));
        var dark = PinnedShortcutIcon.DesiredIconPath("dark");
        Assert.Equal("dark.ico", Path.GetFileName(dark));
        Assert.Equal("app-icons", Path.GetFileName(Path.GetDirectoryName(dark)));
    }
}
