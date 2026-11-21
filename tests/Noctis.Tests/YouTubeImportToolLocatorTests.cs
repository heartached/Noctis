using Noctis.Plugins.YouTubeImport;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The "Add from YouTube" plugin finds yt-dlp, ffmpeg, deno and node on PATH and then in the
/// extra folders macOS needs (Homebrew/MacPorts): Noctis opened from Finder gets launchd's
/// PATH (/usr/bin:/bin:/usr/sbin:/sbin), so a Homebrew node was never passed to yt-dlp.
/// </summary>
public class YouTubeImportToolLocatorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-toollocator-" + Guid.NewGuid().ToString("N")[..8]);

    public YouTubeImportToolLocatorTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private string MakeDir(string name)
    {
        var dir = Path.Combine(_root, name);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Find_LooksInExtraFolders_WhenPathLacksTheTool()
    {
        var path = MakeDir("usr-bin");
        var brew = MakeDir("homebrew-bin");
        File.WriteAllText(Path.Combine(brew, "node"), "");

        Assert.Equal(Path.Combine(brew, "node"), ToolLocator.Find("node", path, new[] { brew }));
    }

    [Fact]
    public void Find_PrefersPath_OverExtraFolders()
    {
        var path = MakeDir("usr-bin");
        var brew = MakeDir("homebrew-bin");
        File.WriteAllText(Path.Combine(path, "ffmpeg"), "");
        File.WriteAllText(Path.Combine(brew, "ffmpeg"), "");

        Assert.Equal(Path.Combine(path, "ffmpeg"), ToolLocator.Find("ffmpeg", path, new[] { brew }));
    }

    [Fact]
    public void Find_ReturnsNull_WhenNowhere()
    {
        Assert.Null(ToolLocator.Find("deno", MakeDir("a"), new[] { MakeDir("b") }));
    }

    [Fact]
    public void ChildPath_AppendsMissingFolders_Once()
    {
        var sep = Path.PathSeparator;
        var result = ToolLocator.ChildPath($"/usr/bin{sep}/bin{sep}/opt/homebrew/bin", new[] { "/opt/homebrew/bin", "/usr/local/bin" });
        Assert.Equal($"/usr/bin{sep}/bin{sep}/opt/homebrew/bin{sep}/usr/local/bin", result);
    }

    [Fact]
    public void MacFolders_AreTheOnesNoctisSearchesForFfmpeg()
    {
        Assert.Equal(new[] { "/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin" }, ToolLocator.MacPackageManagerBinDirs);
    }
}
