using System.Collections.Generic;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class FolderTreeBuilderTests
{
    private static Track T(string path) => new() { FilePath = path, Title = System.IO.Path.GetFileNameWithoutExtension(path) };

    [Fact]
    public void Build_GroupsSubfoldersUnderRoot()
    {
        var tracks = new List<Track>
        {
            T(@"C:\Music\Rock\song1.mp3"),
            T(@"C:\Music\Rock\song2.mp3"),
            T(@"C:\Music\Metal\song3.mp3"),
            T(@"C:\Music\Metal\sub\song4.mp3"),
        };
        var roots = new[] { @"C:\Music" };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        Assert.Single(forest);
        var root = forest[0];
        Assert.True(root.IsRoot);
        Assert.Equal(4, root.TotalTrackCount);
        Assert.Equal(2, root.Children.Count);

        var rock = root.Children.First(c => c.DisplayName == "Rock");
        Assert.Equal(2, rock.TotalTrackCount);
        Assert.Equal(2, rock.DirectTracks.Count);
        Assert.Empty(rock.Children);

        var metal = root.Children.First(c => c.DisplayName == "Metal");
        Assert.Equal(2, metal.TotalTrackCount);
        Assert.Single(metal.DirectTracks);
        Assert.Single(metal.Children);
        Assert.Equal("sub", metal.Children[0].DisplayName);
    }

    [Fact]
    public void Build_TracksOutsideAnyRoot_AreIgnored()
    {
        var tracks = new List<Track> { T(@"D:\Other\song.mp3") };
        var roots = new[] { @"C:\Music" };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        Assert.Single(forest);
        Assert.Equal(0, forest[0].TotalTrackCount);
    }

    [Fact]
    public void Build_SortsChildrenAlphabetically()
    {
        var tracks = new List<Track>
        {
            T(@"C:\Music\Zeta\a.mp3"),
            T(@"C:\Music\Alpha\a.mp3"),
            T(@"C:\Music\Mu\a.mp3"),
        };
        var roots = new[] { @"C:\Music" };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        var names = forest[0].Children.Select(c => c.DisplayName).ToList();
        Assert.Equal(new[] { "Alpha", "Mu", "Zeta" }, names);
    }
}
