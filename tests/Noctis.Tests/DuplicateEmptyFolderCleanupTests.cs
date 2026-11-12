using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>After a duplicate is deleted, the folder it leaves completely empty goes too.</summary>
public sealed class DuplicateEmptyFolderCleanupTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "noctis-dupe-clean-" + Guid.NewGuid().ToString("N"));

    public DuplicateEmptyFolderCleanupTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string P(params string[] parts) => Path.Combine(new[] { _root }.Concat(parts).ToArray());

    private HashSet<string> Protected(params string[] dirs) => new(dirs, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void EmptiedFolderAndItsEmptiedParent_AreRemoved_UpToTheMusicFolder()
    {
        Directory.CreateDirectory(P("Music", "Copies", "Album"));
        var deleted = P("Music", "Copies", "Album", "song.flac"); // already in the trash

        DuplicateFinderService.RemoveEmptiedFolders(new[] { deleted }, Protected(P("Music")));

        Assert.False(Directory.Exists(P("Music", "Copies")));
        Assert.True(Directory.Exists(P("Music")));
    }

    [Theory]
    [InlineData("other.flac")]
    [InlineData("cover.jpg")]
    [InlineData("song.lrc")]
    public void FolderThatStillHoldsAnything_IsKept(string leftover)
    {
        Directory.CreateDirectory(P("Music", "Album"));
        File.WriteAllBytes(P("Music", "Album", leftover), new byte[8]);

        DuplicateFinderService.RemoveEmptiedFolders(new[] { P("Music", "Album", "song.flac") }, Protected(P("Music")));

        Assert.True(File.Exists(P("Music", "Album", leftover)));
    }

    [Fact]
    public void FolderWithASubfolder_IsKept()
    {
        Directory.CreateDirectory(P("Music", "Album", "Disc 2"));

        DuplicateFinderService.RemoveEmptiedFolders(new[] { P("Music", "Album", "song.flac") }, Protected(P("Music")));

        Assert.True(Directory.Exists(P("Music", "Album", "Disc 2")));
    }

    [Fact]
    public void EmptyMusicFolderItself_IsNeverRemoved()
    {
        Directory.CreateDirectory(P("Music"));

        DuplicateFinderService.RemoveEmptiedFolders(new[] { P("Music", "song.flac") }, Protected(P("Music")));

        Assert.True(Directory.Exists(P("Music")));
    }
}
