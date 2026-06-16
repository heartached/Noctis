using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class UpdateInstallSourceTests
{
    [Theory]
    // Running directory matches the Inno-recorded install location → Installed
    // (also how winget/Chocolatey install — they wrap the same Inno setup).
    [InlineData(@"C:\Program Files\Noctis", @"C:\Program Files\Noctis", InstallSource.Installed)]
    // Trailing-slash / case differences must still match.
    [InlineData(@"C:\Program Files\Noctis\", @"c:\program files\noctis", InstallSource.Installed)]
    [InlineData(@"C:\Users\me\AppData\Local\Programs\Noctis",
                @"C:\Users\me\AppData\Local\Programs\Noctis\", InstallSource.Installed)]
    // No Inno entry, running from a Scoop app dir → Scoop.
    [InlineData(@"C:\Users\me\scoop\apps\noctis\1.1.14", null, InstallSource.Scoop)]
    // An unrelated Inno install exists elsewhere, but we run from Scoop → Scoop.
    [InlineData(@"C:\Users\me\scoop\apps\noctis\1.1.14",
                @"C:\Program Files\Noctis", InstallSource.Scoop)]
    // No Inno entry, not under scoop → Portable (manually-extracted zip).
    [InlineData(@"C:\Users\me\Downloads\Noctis", null, InstallSource.Portable)]
    // Inno entry points somewhere else and we're not scoop → Portable.
    [InlineData(@"D:\Apps\Noctis-portable",
                @"C:\Program Files\Noctis", InstallSource.Portable)]
    public void ClassifyInstall_categorizesRunningCopy(
        string appDir, string? innoInstallLocation, InstallSource expected)
    {
        Assert.Equal(expected, UpdateService.ClassifyInstall(appDir, innoInstallLocation));
    }
}
