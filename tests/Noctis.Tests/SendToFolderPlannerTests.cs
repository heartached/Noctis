using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class SendToFolderPlannerTests
{
    private static Track T(string file, string artist = "Artist", string album = "Album", int no = 1, string title = "Song") => new()
    {
        Id = Guid.NewGuid(),
        FilePath = TestPaths.Primary("Music", file),
        Artist = artist,
        AlbumArtist = artist,
        Album = album,
        TrackNumber = no,
        Title = title,
    };

    private static Func<string, FileProbe?> Disk(Dictionary<string, long> files) =>
        path => files.TryGetValue(path, out var len) ? new FileProbe(len) : null;

    [Fact]
    public void Flat_CopiesEachFileToRoot_WithItsOwnName()
    {
        var a = T("a.flac"); var b = T("sub/b.mp3");
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a, b }, root, null, false,
            Disk(new() { [a.FilePath] = 10, [b.FilePath] = 20 }));

        Assert.Equal(2, plan.Count);
        Assert.Equal(Path.Combine(root, "a.flac"), plan[0].TargetPath);
        Assert.Equal(Path.Combine(root, "b.mp3"), plan[1].TargetPath);
        Assert.All(plan, p => Assert.Equal(SendToFolderAction.Copy, p.Action));
    }

    [Fact]
    public void IdenticalSizeAtTarget_IsSkipped()
    {
        var a = T("a.flac");
        var root = TestPaths.Other("USB");
        var target = Path.Combine(root, "a.flac");
        var plan = SendToFolderPlanner.Plan(new[] { a }, root, null, false,
            Disk(new() { [a.FilePath] = 1234, [target] = 1234 }));

        Assert.Equal(SendToFolderAction.SkipIdentical, plan[0].Action);
        Assert.Equal(target, plan[0].TargetPath);
    }

    [Fact]
    public void DifferentSizeAtTarget_GetsANumericSuffix()
    {
        var a = T("a.flac");
        var root = TestPaths.Other("USB");
        var target = Path.Combine(root, "a.flac");
        var plan = SendToFolderPlanner.Plan(new[] { a }, root, null, false,
            Disk(new() { [a.FilePath] = 1234, [target] = 999 }));

        Assert.Equal(SendToFolderAction.Renamed, plan[0].Action);
        Assert.Equal(Path.Combine(root, "a (2).flac"), plan[0].TargetPath);
    }

    [Fact]
    public void TwoSourcesWithTheSameName_DoNotCollideInTheBatch()
    {
        var a = T("x/song.mp3", title: "One"); var b = T("y/song.mp3", title: "Two");
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a, b }, root, null, false,
            Disk(new() { [a.FilePath] = 1, [b.FilePath] = 2 }));

        Assert.Equal(Path.Combine(root, "song.mp3"), plan[0].TargetPath);
        Assert.Equal(Path.Combine(root, "song (2).mp3"), plan[1].TargetPath);
        Assert.Equal(SendToFolderAction.Renamed, plan[1].Action);
    }

    [Fact]
    public void OrganizePattern_BuildsFoldersLikeTheOrganizer()
    {
        var a = T("a.flac", artist: "Daft Punk", album: "Discovery", no: 3, title: "Digital Love");
        var root = TestPaths.Other("USB");
        var plan = SendToFolderPlanner.Plan(new[] { a }, root, "{AlbumArtist}/{Album}/{TrackNo} {Title}", false,
            Disk(new() { [a.FilePath] = 1 }));

        Assert.Equal(Path.Combine(root, "Daft Punk", "Discovery", "03 Digital Love.flac"), plan[0].TargetPath);
    }

    [Fact]
    public void IncludeLyrics_PairsTheSidecar_WhenItExists()
    {
        var a = T("a.flac"); var b = T("b.flac");
        var root = TestPaths.Other("USB");
        var lrc = Path.ChangeExtension(a.FilePath, ".lrc");
        var plan = SendToFolderPlanner.Plan(new[] { a, b }, root, null, true,
            Disk(new() { [a.FilePath] = 1, [b.FilePath] = 1, [lrc] = 5 }));

        Assert.Equal(lrc, plan[0].SidecarSource);
        Assert.Equal(Path.Combine(root, "a.lrc"), plan[0].SidecarTarget);
        Assert.Null(plan[1].SidecarSource);
    }

    [Fact]
    public void EmptyRoot_OrNoTracks_PlansNothing()
    {
        Assert.Empty(SendToFolderPlanner.Plan(new[] { T("a.flac") }, "", null, false, _ => null));
        Assert.Empty(SendToFolderPlanner.Plan(Array.Empty<Track>(), TestPaths.Other("USB"), null, false, _ => null));
    }
}
