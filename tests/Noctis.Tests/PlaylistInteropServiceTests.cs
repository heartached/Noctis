using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class PlaylistInteropServiceTests
{
    [Fact]
    public void PortablePath_TrackUnderPlaylistDir_IsRelativeWithForwardSlashes()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "NoctisTests", "lib");
        var track = Path.Combine(baseDir, "Artist", "song.flac");
        Assert.Equal("Artist/song.flac", PlaylistInteropService.PortablePath(baseDir, track));
    }

    [Fact]
    public void PortablePath_TrackAbovePlaylistDir_UsesDotDot()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", "lib");
        var baseDir = Path.Combine(root, "Playlists");
        var track = Path.Combine(root, "Music", "song.mp3");
        Assert.Equal("../Music/song.mp3", PlaylistInteropService.PortablePath(baseDir, track));
    }

    [Fact]
    public void PortablePath_DifferentRoot_FallsBackToAbsolute()
    {
        if (!OperatingSystem.IsWindows()) return; // distinct roots only exist as drives
        var result = PlaylistInteropService.PortablePath(@"C:\Playlists", @"X:\Music\song.mp3");
        Assert.Equal(@"X:\Music\song.mp3", result);
    }

    [Fact]
    public async Task Export_WritesRelativePaths_ForTracksUnderPlaylistDir()
    {
        var svc = new PlaylistInteropService();
        var tempDir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            var track = new Track
            {
                Artist = "A",
                Title = "T",
                Duration = TimeSpan.FromSeconds(10),
                FilePath = Path.Combine(tempDir, "sub", "song.mp3")
            };
            var exportPath = Path.Combine(tempDir, "list.m3u8");
            await svc.ExportM3uAsync(exportPath, new[] { track });

            var lines = await File.ReadAllLinesAsync(exportPath);
            Assert.Contains("sub/song.mp3", lines);
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task ExportImport_M3u_PreservesOrder()
    {
        var svc = new PlaylistInteropService();
        var tempDir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var t1 = new Track
            {
                Id = Guid.NewGuid(),
                Artist = "Artist A",
                Title = "Track 1",
                Duration = TimeSpan.FromSeconds(100),
                FilePath = Path.Combine(tempDir, "01.mp3")
            };
            var t2 = new Track
            {
                Id = Guid.NewGuid(),
                Artist = "Artist B",
                Title = "Track 2",
                Duration = TimeSpan.FromSeconds(200),
                FilePath = Path.Combine(tempDir, "02.mp3")
            };

            await File.WriteAllTextAsync(t1.FilePath, "x");
            await File.WriteAllTextAsync(t2.FilePath, "x");

            var exportPath = Path.Combine(tempDir, "test.m3u8");
            await svc.ExportM3uAsync(exportPath, new[] { t1, t2 });
            var imported = await svc.ImportM3uAsync(exportPath, new[] { t1, t2 });

            Assert.Equal(new[] { t1.Id, t2.Id }, imported.Select(t => t.Id).ToArray());
        }
        finally
        {
            try { Directory.Delete(tempDir, true); } catch { }
        }
    }
}
