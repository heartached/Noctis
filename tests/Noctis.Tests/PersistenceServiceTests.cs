using System.Text.Json;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Exercises the REAL PersistenceService against a temp data root — the fake
/// used elsewhere never touches the atomic-write, crash-recovery or secret-
/// protection paths, which are exactly the data-loss-critical ones.
/// </summary>
public class PersistenceServiceTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    private PersistenceService Create() => new(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public async Task Settings_RoundTrip_PreservesValues()
    {
        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings
        {
            Volume = 42,
            LastFmUsername = "listener"
        });

        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal(42, loaded.Volume);
        Assert.Equal("listener", loaded.LastFmUsername);
    }

    [Fact]
    public async Task Save_LeavesNoTempFileBehind()
    {
        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings());
        await svc.SaveLibraryAsync(new List<Track> { new() { Title = "T", FilePath = "x.mp3" } });

        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Load_RecoversFromTempFile_WhenMainFileMissing()
    {
        // A crash between serializing the .tmp and the atomic rename leaves only
        // the temp file; load must recover it instead of resetting to defaults.
        var svc = Create();
        var json = JsonSerializer.Serialize(
            new AppSettings { Volume = 77 },
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json.tmp"), json);

        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal(77, loaded.Volume);
    }

    [Fact]
    public async Task ConcurrentLibrarySaves_NeverCorruptTheFile()
    {
        // Overlapping saves used to race on the shared library.json.tmp
        // (sharing violation or truncated file). With the per-path write gate
        // every save serializes; the final file must always parse.
        var svc = Create();
        var saves = Enumerable.Range(0, 12).Select(i =>
            svc.SaveLibraryAsync(Enumerable.Range(0, 50)
                .Select(n => new Track
                {
                    Id = Guid.NewGuid(),
                    Title = $"t{i}-{n}",
                    FilePath = $"lib{i}/{n}.mp3"
                })
                .ToList()));

        await Task.WhenAll(saves);

        var loaded = await svc.LoadLibraryAsync();
        Assert.NotNull(loaded);
        Assert.Equal(50, loaded!.Count);
        Assert.Empty(Directory.GetFiles(_root, "*.tmp"));
    }

    [Fact]
    public async Task Secrets_AreEncryptedAtRest_AndRoundTrip()
    {
        if (!OperatingSystem.IsWindows()) return; // DPAPI is Windows-only by design

        var svc = Create();
        await svc.SaveSettingsAsync(new AppSettings
        {
            LastFmSessionKey = "plain-lastfm-session",
            SourceConnections =
            {
                new SourceConnection
                {
                    Name = "navi",
                    Username = "u",
                    TokenOrPassword = "plain-navidrome-pass"
                }
            }
        });

        var raw = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        Assert.DoesNotContain("plain-lastfm-session", raw);
        Assert.DoesNotContain("plain-navidrome-pass", raw);
        Assert.Contains("enc:dpapi:", raw);

        var loaded = await svc.LoadSettingsAsync();
        Assert.Equal("plain-lastfm-session", loaded.LastFmSessionKey);
        Assert.Equal("plain-navidrome-pass", loaded.SourceConnections[0].TokenOrPassword);
    }
}
