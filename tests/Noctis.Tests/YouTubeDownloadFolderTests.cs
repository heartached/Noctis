using Noctis.Services.YouTube;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08 "how come it says saved to that location?": with one music folder per artist
/// and no Download folder set, downloads went to "YouTube" inside the FIRST artist's folder
/// (B:\ALAC\Taylor Swift\YouTube). The default is now "YouTube" in the deepest folder that
/// holds every music folder, falling back to the old rule when that would be a bare drive or
/// there is no shared folder.
/// </summary>
public class YouTubeDownloadFolderTests
{
    [Fact]
    public void OneFolderPerArtist_DownloadsGoBesideThem_NotInsideTheFirstArtist()
    {
        // The owner's real list (order as in settings.json), including a nested album folder.
        var folders = new[]
        {
            @"B:\ALAC\Taylor Swift", @"B:\ALAC\Yung Pinch", @"B:\ALAC\Juice WRLD", @"B:\ALAC\Drake",
            @"B:\ALAC\Tory Lanez\I Told You [M] [E]", @"B:\ALAC\Bruno Mars",
        };
        Assert.Equal(@"B:\ALAC\YouTube", YouTubeImportService.DefaultDownloadFolder(folders));
    }

    [Theory]
    // A single music folder: inside it, as before.
    [InlineData(@"B:\ALAC\YouTube", @"B:\ALAC")]
    [InlineData(@"C:\Users\me\Music\YouTube", @"C:\Users\me\Music\")]
    // A root plus a folder inside it: the root.
    [InlineData(@"D:\Music\YouTube", @"D:\Music", @"D:\Music\Extra")]
    // Only the drive is shared, or nothing: the old "inside the first folder" rule.
    [InlineData(@"B:\Rap\YouTube", @"B:\Rap", @"B:\Pop")]
    [InlineData(@"C:\Music\YouTube", @"C:\Music", @"D:\Songs")]
    [InlineData(@"B:\YouTube", @"B:\")]
    // Unix and network shares keep their prefix.
    [InlineData("/home/me/Music/YouTube", "/home/me/Music/Rap", "/home/me/Music/Pop")]
    [InlineData(@"\\nas\share\Music\YouTube", @"\\nas\share\Music\A", @"\\nas\share\Music\B")]
    public void DefaultDownloadFolder_FollowsTheSharedParent(string expected, params string[] folders) =>
        Assert.Equal(expected, YouTubeImportService.DefaultDownloadFolder(folders));

    [Fact]
    public void NoMusicFolders_NoDefault() =>
        Assert.Equal(string.Empty, YouTubeImportService.DefaultDownloadFolder(new[] { "", "  " }));
}
