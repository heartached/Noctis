using Noctis.Services;
using Noctis.ViewModels;
using Xunit;
using static Noctis.Tests.ReplayGainWriteTests;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-24: a date typed into Edit Info › Advanced Details › release date ("07/23/2016")
/// never showed up after Save. AdvancedTagIO read RELEASETIME / RELEASEDATE into the box but
/// ApplyAll never wrote the box back, so every release date edit was dropped.
/// </summary>
public class AdvancedReleaseDateWriteTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AdvancedReleaseDateWriteTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static bool Save(string path, string releaseDate)
    {
        var original = AdvancedTagIO.ReadAll(path);
        var updated = AdvancedTagIO.ReadAll(path);
        updated.ReleaseDate = releaseDate;
        // The editor's path: MetadataService's atomic temp-copy save around ApplyAll.
        return ((IMetadataService)new MetadataService()).WriteAdvancedFields(path, updated, original);
    }

    /// <summary>A tiny file of every format the library takes (MetadataService.SupportedExtensions)
    /// that TagLib can write; .dff is read-only (TagLib has no DSDIFF writer).</summary>
    private string Create(string ext)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N") + "." + ext);
        var fixture = ext switch
        {
            "oga" => "ogg",
            "alac" or "mp4" or "m4b" => "m4a",
            "aif" => "aiff",
            "asf" => "wma",
            _ => ext,
        };
        switch (ext)
        {
            case "dsf": File.WriteAllBytes(path, MinimalDsf()); break;
            case "ape": File.WriteAllBytes(path, MinimalApe()); break;
            default:
                File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", "Silence", "silence." + fixture), path, true);
                break;
        }
        return path;
    }

    private static byte[] MinimalDsf()
    {
        const int dataLen = 4096;
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        w.Write("DSD "u8); w.Write(28L); w.Write(28L + 52 + 12 + dataLen); w.Write(0L);
        w.Write("fmt "u8); w.Write(52L); w.Write(1); w.Write(0); w.Write(1); w.Write(1);
        w.Write(2822400); w.Write(1); w.Write((long)dataLen * 8); w.Write(4096); w.Write(0);
        w.Write("data"u8); w.Write(12L + dataLen); w.Write(new byte[dataLen]);
        return ms.ToArray();
    }

    private static byte[] MinimalApe()
    {
        using var ms = new MemoryStream();
        using var w = new BinaryWriter(ms);
        // APE_DESCRIPTOR (version 3990)
        w.Write("MAC "u8); w.Write((short)3990); w.Write((short)0);
        w.Write(52); w.Write(24); w.Write(0); w.Write(0); w.Write(16); w.Write(0); w.Write(0);
        w.Write(new byte[16]);
        // APE_HEADER
        w.Write((short)2000); w.Write((short)0); w.Write(73728); w.Write(1); w.Write(1);
        w.Write((short)16); w.Write((short)1); w.Write(44100);
        w.Write(new byte[16]);
        return ms.ToArray();
    }

    public static IEnumerable<object[]> WritableFormats() =>
        MetadataService.SupportedExtensions
            .Where(e => e != ".dff")
            .Select(e => new object[] { e.TrimStart('.') });

    [Fact]
    public void EveryLibraryFormat_IsCovered()
        => Assert.Equal(MetadataService.SupportedExtensions.Count - 1, WritableFormats().Count());

    [Theory]
    [MemberData(nameof(WritableFormats))]
    public void TypedDate_IsSaved_AndTheLibraryReadsIt(string kind)
    {
        var path = Create(kind);

        Assert.True(Save(path, "07/23/2016"));

        // Stored as an ISO date, which other players and taggers read.
        Assert.Equal("2016-07-23", AdvancedTagIO.ReadAll(path).ReleaseDate);
        var track = new MetadataService().ReadTrackMetadata(path);
        Assert.NotNull(track);
        Assert.Equal("2016-07-23", track!.ReleaseDate);
        Assert.Equal(2016, track.DisplayYear);
    }

    /// <summary>The scan read .alac and .aifc through TagLib#, which knows neither name, so
    /// those songs never reached the library.</summary>
    [Theory]
    [InlineData("alac")]
    [InlineData("aifc")]
    public void AlacAndAifc_AreRead(string ext)
    {
        var track = new MetadataService().ReadTrackMetadata(Create(ext));
        Assert.NotNull(track);
    }

    [Theory]
    [InlineData("aifc", "AIFC")]
    [InlineData("aiff", "AIFF")]
    public void Aiff_KeepsItsFormTypeAndSize_AfterASave(string ext, string formType)
    {
        var path = Create(ext);
        Assert.True(Save(path, "2016-07-23"));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal("FORM", System.Text.Encoding.ASCII.GetString(bytes, 0, 4));
        Assert.Equal(formType, System.Text.Encoding.ASCII.GetString(bytes, 8, 4));
        Assert.Equal(bytes.Length - 8, System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4)));
        var track = new MetadataService().ReadTrackMetadata(path);
        Assert.NotNull(track);
        Assert.True(track!.Duration > TimeSpan.Zero);
        Assert.Equal("2016-07-23", track.ReleaseDate);
    }

    /// <summary>The owner's steps: Edit Info › Advanced Details › release date, Save.</summary>
    [Theory]
    [InlineData("mp3")]
    [InlineData("flac")]
    [InlineData("m4a")]
    public async Task EditInfo_TypedDate_ReachesTheFileAndTheLibrary(string ext)
    {
        var path = Create(ext);
        var track = new MetadataService().ReadTrackMetadata(path)!;
        var library = new FakeLibraryService();
        library.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        await vm.InitializeAsync();

        vm.AdvReleaseDate = "07/23/2016";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.Equal("2016-07-23", track.ReleaseDate);
        Assert.Equal("2016-07-23", new MetadataService().ReadTrackMetadata(path)!.ReleaseDate);

        var reopened = new MetadataViewModel(track, new MetadataService(), library, new TestPersistenceService(), new FakeAnimatedCoverService());
        await reopened.InitializeAsync();
        Assert.Equal("2016-07-23", reopened.AdvReleaseDate);
    }

    [Fact]
    public void NewDate_ReplacesAnOlderReleaseTime_WhichIsReadFirst()
    {
        var path = CreateFlac(_dir);
        using (var f = TagLib.File.Create(path))
        {
            ((TagLib.Ogg.XiphComment)f.GetTag(TagLib.TagTypes.Xiph, true)).SetField("RELEASETIME", "2001-01-01");
            f.Save();
        }

        Assert.True(Save(path, "2016-07-23"));

        Assert.Equal("2016-07-23", AdvancedTagIO.ReadAll(path).ReleaseDate);
    }

    [Fact]
    public void ClearedBox_RemovesTheDate()
    {
        var path = CreateMp3WithId3v2Only(_dir);
        Assert.True(Save(path, "2016-07-23"));

        Assert.True(Save(path, ""));

        Assert.Equal("", AdvancedTagIO.ReadAll(path).ReleaseDate);
    }

    [Theory]
    [InlineData("07/23/2016", "2016-07-23")]
    [InlineData("23/07/2016", "2016-07-23")]
    [InlineData("2016/7/3", "2016-07-03")]
    [InlineData("2016-07-23", "2016-07-23")]
    [InlineData("2016", "2016")]            // a bare year stays a year
    [InlineData("2016-07", "2016-07")]      // no day: not turned into the 1st
    [InlineData("2014-10-27T07:00:00Z", "2014-10-27T07:00:00Z")]
    [InlineData("  summer 2016 ", "summer 2016")]
    public void NormalizeReleaseDate(string typed, string stored)
        => Assert.Equal(stored, AdvancedTagIO.NormalizeReleaseDate(typed));
}
