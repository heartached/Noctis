using System.Collections.Generic;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class FolderTreeBuilderTests
{
    private static Track T(string path, int track = 0, int disc = 1) => new()
    {
        FilePath = path,
        Title = System.IO.Path.GetFileNameWithoutExtension(path),
        TrackNumber = track,
        DiscNumber = disc,
    };

    [Fact]
    public void Build_GroupsSubfoldersUnderRoot()
    {
        var tracks = new List<Track>
        {
            T(TestPaths.Primary("Music", "Rock", "song1.mp3")),
            T(TestPaths.Primary("Music", "Rock", "song2.mp3")),
            T(TestPaths.Primary("Music", "Metal", "song3.mp3")),
            T(TestPaths.Primary("Music", "Metal", "sub", "song4.mp3")),
        };
        var roots = new[] { TestPaths.Primary("Music") };

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
        var tracks = new List<Track> { T(TestPaths.Other("Other", "song.mp3")) };
        var roots = new[] { TestPaths.Primary("Music") };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        Assert.Single(forest);
        Assert.Equal(0, forest[0].TotalTrackCount);
    }

    [Fact]
    public void Build_SortsDirectTracksByDiscThenTrackNumber()
    {
        // Filenames deliberately disagree with the tags — tags must win.
        var tracks = new List<Track>
        {
            T(TestPaths.Primary("Music", "Album", "b.mp3"), track: 2, disc: 2),
            T(TestPaths.Primary("Music", "Album", "c.mp3"), track: 1, disc: 2),
            T(TestPaths.Primary("Music", "Album", "a.mp3"), track: 2, disc: 1),
            T(TestPaths.Primary("Music", "Album", "d.mp3"), track: 1, disc: 1),
        };
        var roots = new[] { TestPaths.Primary("Music") };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        var album = forest[0].Children.Single();
        Assert.Equal(new[] { "d", "a", "c", "b" }, album.DirectTracks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Build_UntaggedDirectTracks_SortNaturallyByFileName()
    {
        // Untagged rips (TrackNumber 0) arrive in arbitrary library order; they
        // must come out in numeric-aware filename order ("2" before "10"), not
        // insertion order and not ordinal string order.
        var tracks = new List<Track>
        {
            T(TestPaths.Primary("Music", "Zoo", "11 Jubilee.wav")),
            T(TestPaths.Primary("Music", "Zoo", "2 Please Forgive Us.wav")),
            T(TestPaths.Primary("Music", "Zoo", "10 Hateful Hate.wav")),
            T(TestPaths.Primary("Music", "Zoo", "1 Eat For Two.wav")),
        };
        var roots = new[] { TestPaths.Primary("Music") };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        var zoo = forest[0].Children.Single();
        Assert.Equal(
            new[] { "1 Eat For Two", "2 Please Forgive Us", "10 Hateful Hate", "11 Jubilee" },
            zoo.DirectTracks.Select(t => t.Title).ToArray());
    }

    [Fact]
    public void Build_SortsChildrenNaturally()
    {
        var tracks = new List<Track>
        {
            T(TestPaths.Primary("Music", "Zeta", "a.mp3")),
            T(TestPaths.Primary("Music", "Volume 10", "a.mp3")),
            T(TestPaths.Primary("Music", "Alpha", "a.mp3")),
            T(TestPaths.Primary("Music", "Volume 2", "a.mp3")),
            T(TestPaths.Primary("Music", "Mu", "a.mp3")),
        };
        var roots = new[] { TestPaths.Primary("Music") };

        var forest = FolderTreeBuilder.Build(tracks, roots);

        var names = forest[0].Children.Select(c => c.DisplayName).ToList();
        Assert.Equal(new[] { "Alpha", "Mu", "Volume 2", "Volume 10", "Zeta" }, names);
    }
}
