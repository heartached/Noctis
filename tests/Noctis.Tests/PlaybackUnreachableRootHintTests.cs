using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// When five tracks in a row fail with "File not found", the cascade stop logs one
/// extra line naming the drive root when the root itself is gone (Mapletic's log,
/// 09-20: 90 misses, every one under A:\, after a partition change — the files were
/// fine, the volume wasn't reachable). Individual missing files under a live root
/// get no hint; streams and audio CDs have no root to test.
/// </summary>
public class PlaybackUnreachableRootHintTests
{
    /// <summary>A drive root on Windows; "/" elsewhere, the only root Path.GetPathRoot gives
    /// there (off Windows a drive-letter path is relative and has no root at all).</summary>
    private static readonly string Root = OperatingSystem.IsWindows() ? @"A:\" : "/";

    [Fact]
    public void RootGone_NamesTheRoot()
    {
        var hint = PlayerViewModel.UnreachableRootHint(Path.Combine(Root, "Music", "Artist", "Album", "song.flac"), _ => false);
        Assert.NotNull(hint);
        Assert.StartsWith($"{Root} is not reachable", hint);
    }

    [Fact]
    public void RootPresent_NoHint()
        => Assert.Null(PlayerViewModel.UnreachableRootHint(Path.Combine(Root, "Music", "song.flac"), root => root == Root));

    /// <summary>UNC shares are a Windows path form; elsewhere the backslashes are filename characters.</summary>
    [Fact]
    public void UncShare_UsesTheShareAsRoot()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "UNC paths are Windows-only");
        var hint = PlayerViewModel.UnreachableRootHint(@"\\nas\music\a.mp3", _ => false);
        Assert.NotNull(hint);
        Assert.StartsWith(@"\\nas\music is not reachable", hint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("https://server/rest/stream?id=1&t=secret")]
    [InlineData("cdda:///D:/#3")]
    public void PathlessOrEmpty_NoHint(string? path)
        => Assert.Null(PlayerViewModel.UnreachableRootHint(path, _ => false));

    [Fact]
    public void ProbeThatThrows_CountsAsUnreachable()
        => Assert.NotNull(PlayerViewModel.UnreachableRootHint(Path.Combine(Root, "x.mp3"), _ => throw new IOException("device not ready")));
}
