using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class FileOrganizePlannerTests
{
    private const string Root = @"C:\Music";

    private static Track T(string artist, string album, int trackNo, string title, string sourcePath)
        => new()
        {
            AlbumArtist = artist,
            Artist = artist,
            Album = album,
            TrackNumber = trackNo,
            Title = title,
            FilePath = sourcePath
        };

    private static IReadOnlyList<OrganizeMove> Plan(IEnumerable<Track> tracks, Func<string, bool>? exists = null)
        => FileOrganizePlanner.Plan(tracks, FileOrganizePlanner.DefaultPattern, Root, exists ?? (_ => false));

    [Fact]
    public void DefaultPattern_BuildsTagDerivedPath()
    {
        var move = Plan(new[] { T("Adele", "25", 1, "Hello", @"D:\in\x.mp3") }).Single();

        Assert.Equal(OrganizeAction.Move, move.Action);
        Assert.Equal(Path.Combine(Root, "Adele", "25", "01 Hello.mp3"), move.TargetPath);
    }

    [Fact]
    public void TrackNumber_IsZeroPaddedToTwoDigits()
    {
        var move = Plan(new[] { T("A", "B", 7, "Song", @"D:\in\s.flac") }).Single();
        Assert.EndsWith(Path.Combine("A", "B", "07 Song.flac"), move.TargetPath);
    }

    [Fact]
    public void InvalidFilenameChars_AreSanitized()
    {
        var move = Plan(new[] { T("A", "B", 1, "AC/DC: Live?", @"D:\in\s.mp3") }).Single();
        Assert.Equal(Path.Combine(Root, "A", "B", "01 AC_DC_ Live_.mp3"), move.TargetPath);
    }

    [Fact]
    public void AlreadyAtTarget_IsSkipped()
    {
        var target = Path.Combine(Root, "Adele", "25", "01 Hello.mp3");
        var move = Plan(new[] { T("Adele", "25", 1, "Hello", target) }).Single();
        Assert.Equal(OrganizeAction.Skip, move.Action);
        Assert.Equal(target, move.TargetPath);
    }

    [Fact]
    public void TwoTracksSameTarget_SecondGetsSuffix()
    {
        var moves = Plan(new[]
        {
            T("A", "B", 1, "Song", @"D:\in\one.mp3"),
            T("A", "B", 1, "Song", @"D:\in\two.mp3")
        });

        Assert.Equal(Path.Combine(Root, "A", "B", "01 Song.mp3"), moves[0].TargetPath);
        Assert.Equal(OrganizeAction.Move, moves[0].Action);
        Assert.Equal(Path.Combine(Root, "A", "B", "01 Song (2).mp3"), moves[1].TargetPath);
        Assert.Equal(OrganizeAction.Conflict, moves[1].Action);
    }

    [Fact]
    public void ExistingFileAtTarget_TriggersSuffix()
    {
        var occupied = Path.Combine(Root, "A", "B", "01 Song.mp3");
        var move = Plan(
            new[] { T("A", "B", 1, "Song", @"D:\in\one.mp3") },
            exists: p => string.Equals(p, occupied, StringComparison.OrdinalIgnoreCase)).Single();

        Assert.Equal(OrganizeAction.Conflict, move.Action);
        Assert.Equal(Path.Combine(Root, "A", "B", "01 Song (2).mp3"), move.TargetPath);
    }

    [Fact]
    public void BlankTags_FallBackToPlaceholders()
    {
        var t = new Track { AlbumArtist = "", Album = "", Title = "", TrackNumber = 0, FilePath = @"D:\in\x.mp3" };
        var move = FileOrganizePlanner.Plan(new[] { t }, FileOrganizePlanner.DefaultPattern, Root, _ => false).Single();
        Assert.Equal(Path.Combine(Root, "Unknown Artist", "Unknown Album", "00 Untitled.mp3"), move.TargetPath);
    }
}
