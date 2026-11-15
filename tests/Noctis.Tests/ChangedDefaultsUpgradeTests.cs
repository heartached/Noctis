using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Album page colour, the play/pause fade and "ignore The when sorting artists" went on for new
/// installs on 2026-10-18. Existing installs keep what they had: a key in settings.json wins,
/// and a file saved before a toggle existed (no key) keeps the old default, off.
/// </summary>
public class ChangedDefaultsUpgradeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public ChangedDefaultsUpgradeTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<AppSettings> LoadFromAsync(string? settingsJson)
    {
        if (settingsJson != null)
            await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), settingsJson);
        return await new PersistenceService(_root).LoadSettingsAsync();
    }

    [Fact]
    public async Task NoSettingsFile_GetsTheNewDefaults()
    {
        var s = await LoadFromAsync(null);
        Assert.True(s.AlbumPageTintEnabled);
        Assert.True(s.PlayPauseFadeEnabled);
        Assert.True(s.IgnoreLeadingWordsInArtistSort);
        Assert.False(s.ImportDroppedMedia);
        Assert.Equal(50, s.PlayCountThresholdPercent);
    }

    [Fact]
    public async Task FileFromBeforeTheToggles_KeepsThemOff()
    {
        var s = await LoadFromAsync("""{ "volume": 42, "theme": "Dark" }""");
        Assert.Equal(42, s.Volume);
        Assert.False(s.AlbumPageTintEnabled);
        Assert.False(s.PlayPauseFadeEnabled);
        Assert.False(s.IgnoreLeadingWordsInArtistSort);
        // 2026-11-14: drops still move in, and a play still counts at the start.
        Assert.True(s.ImportDroppedMedia);
        Assert.Equal(0, s.PlayCountThresholdPercent);
        // Everything else in a key-less file still takes its normal default.
        Assert.True(s.GaplessPlaybackEnabled);
        Assert.Equal(300, s.PlayPauseFadeMs);
    }

    [Fact]
    public async Task FileFromBeforeTheToggles_WritesOffOnTheNextSave()
    {
        var persistence = new PersistenceService(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "settings.json"), """{ "volume": 42 }""");
        await persistence.SaveSettingsAsync(await persistence.LoadSettingsAsync());

        var json = await File.ReadAllTextAsync(Path.Combine(_root, "settings.json"));
        Assert.Contains("\"albumPageTintEnabled\": false", json);
        Assert.Contains("\"playPauseFadeEnabled\": false", json);
        Assert.Contains("\"ignoreLeadingWordsInArtistSort\": false", json);
        var reloaded = await persistence.LoadSettingsAsync();
        Assert.False(reloaded.AlbumPageTintEnabled);
    }

    [Theory]
    [InlineData(true, false, true)]
    [InlineData(false, true, false)]
    [InlineData(true, true, true)]
    public async Task ExplicitValuesInTheFile_AreKept(bool tint, bool fade, bool ignoreThe)
    {
        var s = await LoadFromAsync(
            $$"""{ "albumPageTintEnabled": {{(tint ? "true" : "false")}}, "playPauseFadeEnabled": {{(fade ? "true" : "false")}}, "ignoreLeadingWordsInArtistSort": {{(ignoreThe ? "true" : "false")}} }""");
        Assert.Equal(tint, s.AlbumPageTintEnabled);
        Assert.Equal(fade, s.PlayPauseFadeEnabled);
        Assert.Equal(ignoreThe, s.IgnoreLeadingWordsInArtistSort);
    }

    [Fact]
    public async Task SavedFile_RoundTripsEitherWay()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            AlbumPageTintEnabled = false,
            PlayPauseFadeEnabled = true,
            IgnoreLeadingWordsInArtistSort = false,
        });
        var s = await persistence.LoadSettingsAsync();
        Assert.False(s.AlbumPageTintEnabled);
        Assert.True(s.PlayPauseFadeEnabled);
        Assert.False(s.IgnoreLeadingWordsInArtistSort);
    }
}
