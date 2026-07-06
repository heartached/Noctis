using Noctis.Helpers;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

public class LibraryRemovalHelperTests
{
    private static Track Local(string path) => new() { FilePath = path, SourceType = SourceType.Local };
    private static Track Remote(string path, SourceType type) => new() { FilePath = path, SourceType = type };

    [Fact]
    public void SelectTrashablePaths_KeepsLocalFiles()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(@"C:\music\a.flac"),
            Local(@"C:\music\b.mp3"),
        });

        Assert.Equal(new[] { @"C:\music\a.flac", @"C:\music\b.mp3" }, paths);
    }

    [Fact]
    public void SelectTrashablePaths_SkipsRemoteSources()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(@"C:\music\a.flac"),
            Remote(@"\\nas\share\b.flac", SourceType.Smb),
            Remote("http://host/c.flac", SourceType.Navidrome),
        });

        Assert.Equal(new[] { @"C:\music\a.flac" }, paths);
    }

    [Fact]
    public void SelectTrashablePaths_SkipsEmptyPaths()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(""),
            Local("   "),
            Local(@"C:\music\a.flac"),
        });

        Assert.Equal(new[] { @"C:\music\a.flac" }, paths);
    }

    [Fact]
    public void SelectTrashablePaths_DeduplicatesPaths()
    {
        var paths = LibraryRemovalHelper.SelectTrashablePaths(new[]
        {
            Local(@"C:\music\a.flac"),
            Local(@"C:\music\a.flac"),
        });

        Assert.Single(paths);
    }
}
