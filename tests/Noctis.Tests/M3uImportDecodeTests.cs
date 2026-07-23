using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// M3U8 imports written by other tools (VLC, foobar2000, web exporters) carry
/// file:// URIs and/or percent-encoding; these must decode before path matching
/// — they previously imported as zero matches.
/// </summary>
public class M3uImportDecodeTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public M3uImportDecodeTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private Track LibraryTrack(string relative) => new()
    {
        Id = Guid.NewGuid(),
        Title = "T",
        FilePath = Path.Combine(_dir, relative)
    };

    private async Task<IReadOnlyList<Track>> ImportAsync(string[] lines, params Track[] library)
    {
        var playlist = Path.Combine(_dir, "list.m3u8");
        await File.WriteAllLinesAsync(playlist, lines);
        return await new PlaylistInteropService().ImportM3uAsync(playlist, library);
    }

    [Fact]
    public async Task Import_ResolvesFileUriEntries()
    {
        var track = LibraryTrack(Path.Combine("My Album", "My Song.mp3"));
        var uri = new Uri(track.FilePath).AbsoluteUri; // file:///...My%20Album/My%20Song.mp3
        Assert.Contains("%20", uri); // sanity: the encoding is actually exercised

        var resolved = await ImportAsync(new[] { "#EXTM3U", uri }, track);

        var hit = Assert.Single(resolved);
        Assert.Equal(track.Id, hit.Id);
    }

    [Fact]
    public async Task Import_ResolvesPercentEncodedPlainPaths()
    {
        var track = LibraryTrack("Some Song.mp3");
        var encoded = track.FilePath.Replace(" ", "%20");

        var resolved = await ImportAsync(new[] { encoded }, track);

        var hit = Assert.Single(resolved);
        Assert.Equal(track.Id, hit.Id);
    }

    [Fact]
    public async Task Import_PlainPathsStillResolve()
    {
        var track = LibraryTrack("plain.mp3");

        var resolved = await ImportAsync(new[] { track.FilePath }, track);

        Assert.Single(resolved);
    }

    [Fact]
    public async Task Import_LiteralPercentInFileName_StillResolves()
    {
        // A real "%" in a filename must not be destroyed by decoding, because
        // the un-decoded candidate wouldn't match either way; the decode is
        // attempted but "100%25 hit.mp3"-style names round-trip correctly.
        var track = LibraryTrack("100%25 hit.mp3"); // literal "%25" in the name on disk
        var resolved = await ImportAsync(new[] { track.FilePath.Replace("%25", "%2525") }, track);

        Assert.Single(resolved);
    }
}
