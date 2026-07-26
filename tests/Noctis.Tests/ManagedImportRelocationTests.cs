using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Drag-and-drop imports from outside the configured music folders are relocated into
/// the managed library root.
/// <para>
/// This used to copy, leaving the dropped file behind as a duplicate the user never
/// saw: the library track pointed at the copy, so "Remove from library → Move to
/// Recycle Bin" trashed the copy while the file the user had actually dropped stayed
/// on disk — which read as the Recycle Bin option doing nothing at all.
/// </para>
/// </summary>
public class ManagedImportRelocationTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private readonly string _source;
    private readonly string _root;

    public ManagedImportRelocationTests()
    {
        _source = Path.Combine(_dir, "source");
        _root = Path.Combine(_dir, "root");
        Directory.CreateDirectory(_source);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CreateSourceFile(string name = "antisocial.wav", string content = "payload")
    {
        var path = Path.Combine(_source, name);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void MoveFileIntoManagedRoot_LeavesNoFileBehindAtTheSource()
    {
        var source = CreateSourceFile();

        var final = MainWindowViewModel.MoveFileIntoManagedRoot(source, _root);

        Assert.Equal(Path.Combine(_root, "antisocial.wav"), final);
        Assert.True(File.Exists(final));
        Assert.False(File.Exists(source));   // the dropped file itself moved — no duplicate
    }

    [Fact]
    public void MoveFileIntoManagedRoot_PreservesContentAndTimestamp()
    {
        var source = CreateSourceFile(content: "audio bytes");
        var stamp = new DateTime(2019, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, stamp);

        var final = MainWindowViewModel.MoveFileIntoManagedRoot(source, _root)!;

        Assert.Equal("audio bytes", File.ReadAllText(final));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(final));
    }

    [Fact]
    public void MoveFileIntoManagedRoot_NameClashWithDifferentPayload_KeepsBoth()
    {
        File.WriteAllText(Path.Combine(_root, "antisocial.wav"), "already here");
        var source = CreateSourceFile(content: "a different song");

        var final = MainWindowViewModel.MoveFileIntoManagedRoot(source, _root)!;

        Assert.Equal(Path.Combine(_root, "antisocial (2).wav"), final);
        Assert.Equal("a different song", File.ReadAllText(final));
        Assert.Equal("already here", File.ReadAllText(Path.Combine(_root, "antisocial.wav")));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public void MoveFileIntoManagedRoot_ReDroppingAnAlreadyImportedFile_LeavesTheSourceAlone()
    {
        // Same size + timestamp is too weak an identity check to delete a file over,
        // so this path reuses the existing import instead of relocating.
        var existing = Path.Combine(_root, "antisocial.wav");
        File.WriteAllText(existing, "payload");
        var source = CreateSourceFile(content: "payload");
        var stamp = new DateTime(2019, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(existing, stamp);
        File.SetLastWriteTimeUtc(source, stamp);

        var final = MainWindowViewModel.MoveFileIntoManagedRoot(source, _root);

        Assert.Equal(existing, final);
        Assert.True(File.Exists(source));
    }

    [Fact]
    public void MoveFileIntoManagedRoot_MissingSource_ReturnsNull()
    {
        Assert.Null(MainWindowViewModel.MoveFileIntoManagedRoot(
            Path.Combine(_source, "gone.wav"), _root));
    }
}
