using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Lyrics Studio saves must reach the disk: the lyrics page reads sidecars before the stored
/// lyrics. Word timings go to a .elrc next to the song while the .lrc keeps a line-level
/// projection, so other players keep working and ELRC stays additive.
/// </summary>
public class LyricsWriterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "noctis-lw-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _trashed = new();
    private readonly LyricsWriter _writer;
    private readonly string _audio;
    private readonly string _lrc;
    private readonly string _elrc;

    private const string Elrc = "[00:01.00]<00:01.00>Hello <00:01.50>world<00:02.00>";
    private const string ElrcAsLrc = "[00:01.00]Hello world";
    private const string ForeignLrc = "[00:01.00]Hello there";

    public LyricsWriterTests()
    {
        Directory.CreateDirectory(_dir);
        _audio = Path.Combine(_dir, "song.flac");
        _lrc = Path.Combine(_dir, "song.lrc");
        _elrc = Path.Combine(_dir, "song.elrc");
        File.WriteAllText(_audio, "x");
        var registry = new AppWrittenSidecarRegistry(Path.Combine(_dir, "registry.json"));
        _writer = new LyricsWriter(new StubMetadata(), null, registry, Path.Combine(_dir, "cache"))
        {
            TrashFile = p => { _trashed.Add(p); File.Delete(p); return true; },
        };
    }

    private Track NewTrack() => new() { Title = "Song", FilePath = _audio, SourceType = SourceType.Local };

    [Fact]
    public void LineLevelProjection_StripsWordTagsOnly()
    {
        Assert.Equal(ElrcAsLrc, LyricsWriter.LineLevelProjection(Elrc));
        Assert.Equal("[ar:X]\n[00:01.00]Hello world\n[00:03.00]Plain line", LyricsWriter.LineLevelProjection("[ar:X]\n" + Elrc + "\n[00:03.00]Plain line"));
    }

    [Fact]
    public void Save_WordTimings_WritesElrcAndLineLevelLrc()
    {
        var outcome = _writer.SaveDetailed(NewTrack(), null, Elrc, embedInTags: false, replaceForeignSidecar: true);

        Assert.True(outcome.SidecarWritten);
        Assert.False(outcome.ReplacedForeignSidecar);
        Assert.Empty(_trashed);
        Assert.Equal(Elrc, File.ReadAllText(_elrc));
        Assert.Equal(ElrcAsLrc, File.ReadAllText(_lrc));
    }

    [Fact]
    public void Save_WordTimings_DefaultLeavesForeignLrcAlone_ButStillWritesElrc()
    {
        File.WriteAllText(_lrc, ForeignLrc);

        var outcome = _writer.SaveDetailed(NewTrack(), "Hello world", Elrc, embedInTags: false, replaceForeignSidecar: false);

        Assert.True(outcome.Wrote);
        Assert.True(outcome.SidecarWritten);
        Assert.False(outcome.ReplacedForeignSidecar);
        Assert.Equal(ForeignLrc, File.ReadAllText(_lrc));
        Assert.Equal(Elrc, File.ReadAllText(_elrc));
        Assert.Empty(_trashed);
    }

    [Fact]
    public void Save_ReplaceForeignSidecar_TrashesOldLrcAndWritesProjection()
    {
        File.WriteAllText(_lrc, ForeignLrc);

        var outcome = _writer.SaveDetailed(NewTrack(), "Hello world", Elrc, embedInTags: false, replaceForeignSidecar: true);

        Assert.True(outcome.SidecarWritten);
        Assert.True(outcome.ReplacedForeignSidecar);
        Assert.Equal(new[] { _lrc }, _trashed);
        Assert.Equal(ElrcAsLrc, File.ReadAllText(_lrc));
        Assert.Equal(Elrc, File.ReadAllText(_elrc));
    }

    [Fact]
    public void Save_LineLevelAfterWordLevel_RemovesOurStaleElrc()
    {
        _writer.SaveDetailed(NewTrack(), "a", Elrc, embedInTags: false, replaceForeignSidecar: true);
        _trashed.Clear();

        var outcome = _writer.SaveDetailed(NewTrack(), "a", "[00:02.00]Again", embedInTags: false, replaceForeignSidecar: true);

        Assert.True(outcome.SidecarWritten);
        Assert.False(outcome.ReplacedForeignSidecar); // both files were ours
        Assert.Equal("[00:02.00]Again", File.ReadAllText(_lrc));
        Assert.False(File.Exists(_elrc));
    }

    [Fact]
    public void Save_LineLevel_ForeignElrcLeftAloneUnlessReplacing()
    {
        File.WriteAllText(_elrc, Elrc);

        _writer.SaveDetailed(NewTrack(), "a", "[00:02.00]Again", embedInTags: false, replaceForeignSidecar: false);
        Assert.True(File.Exists(_elrc));

        var outcome = _writer.SaveDetailed(NewTrack(), "a", "[00:02.00]Again", embedInTags: false, replaceForeignSidecar: true);
        Assert.True(outcome.ReplacedForeignSidecar);
        Assert.False(File.Exists(_elrc));
        Assert.Equal(new[] { _elrc }, _trashed);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }
}
