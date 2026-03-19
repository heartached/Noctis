using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class PlaylistInteropServiceTests
{
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
