using Noctis.Models;
using Noctis.Services;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// The record label (the phone album page's "RECORD LABEL" footer line): read by the scan from
/// a LABEL field, else TagLib#'s publisher (ID3v2 TPUB, Vorbis ORGANIZATION), persisted in
/// library.json like the copyright, and the album's taken from its first labelled track.
/// </summary>
public class TrackLabelTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public TrackLabelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string Flac(params (string Field, string Value)[] fields)
    {
        var path = CreateFlac(_dir);
        var named = Path.Combine(_dir, $"{Guid.NewGuid():N}.flac");
        File.Move(path, named);
        using (var f = TagLib.File.Create(named))
        {
            var xiph = (TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true);
            xiph.Title = "Leave the Door Open";
            foreach (var (field, value) in fields) xiph.SetField(field, value);
            f.Save();
        }
        return named;
    }

    private string Mp3(Action<TagLib.Id3v2.Tag> tag)
    {
        var path = CreateMp3WithId3v2Only(_dir);
        var named = Path.Combine(_dir, $"{Guid.NewGuid():N}.mp3");
        File.Move(path, named);
        using (var f = TagLib.File.Create(named))
        {
            tag((TagLib.Id3v2.Tag)f.GetTag(TagLib.TagTypes.Id3v2, true));
            f.Save();
        }
        return named;
    }

    private static string ReadLabel(string path) => new MetadataService().ReadTrackMetadata(path)!.Label;

    [Fact]
    public void Flac_LabelField_IsRead()
    {
        Assert.Equal("Aftermath Entertainment", ReadLabel(Flac(("LABEL", "Aftermath Entertainment"))));
    }

    [Fact]
    public void Flac_Organization_IsTheFallback_AndLabelWins()
    {
        Assert.Equal("Atlantic", ReadLabel(Flac(("ORGANIZATION", "Atlantic"))));
        Assert.Equal("Aftermath", ReadLabel(Flac(("ORGANIZATION", "Atlantic"), ("LABEL", "Aftermath"))));
    }

    [Fact]
    public void Mp3_Tpub_IsRead_AndTxxxLabelWins()
    {
        Assert.Equal("Big Machine Label Group", ReadLabel(Mp3(t => t.Publisher = "Big Machine Label Group")));
        Assert.Equal("Republic", ReadLabel(Mp3(t =>
        {
            t.Publisher = "Big Machine";
            TagLib.Id3v2.UserTextInformationFrame.Get(t, "LABEL", true).Text = new[] { "Republic" };
        })));
    }

    [Fact]
    public void NoLabelTag_ReadsEmpty()
    {
        Assert.Equal(string.Empty, ReadLabel(Flac(("COPYRIGHT", "℗ 2021 Aftermath"))));
    }

    [Fact]
    public async Task Label_RoundTripsThroughLibraryJson()
    {
        var persistence = new PersistenceService(_dir);
        await persistence.SaveLibraryAsync(new List<Track> { new() { Id = Guid.NewGuid(), Title = "Skate", Label = "Aftermath" } });
        var loaded = await persistence.LoadLibraryAsync();
        Assert.Equal("Aftermath", Assert.Single(loaded!).Label);
    }

    [Fact]
    public void AlbumRecordLabel_IsTheFirstLabelledTrack_NeverTheCopyrightHolder()
    {
        var album = new Album
        {
            Tracks =
            {
                new Track { Copyright = "℗ 2017 Taylor Swift" },
                new Track { Label = " Big Machine Label Group " },
                new Track { Label = "Other" },
            },
        };
        Assert.Equal("Big Machine Label Group", album.RecordLabel);
        Assert.Equal(string.Empty, new Album { Tracks = { new Track { Copyright = "℗ 2017 Taylor Swift" } } }.RecordLabel);
    }
}
