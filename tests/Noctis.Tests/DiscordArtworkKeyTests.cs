using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DiscordArtworkKeyTests
{
    private const string Icon = "noctis_icon";

    [Fact]
    public void PrefersFreshUrlWhenAvailable()
    {
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: "https://relay/new", identity: "a", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Equal("https://relay/new", key);
    }

    [Fact]
    public void ReusesLastKeyWhenUrlMissingForSameTrack()
    {
        // Relay transiently down (null URL) but Discord already shows the cached cover —
        // keep the same key instead of flipping to the app icon.
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: null, identity: "a", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Equal("https://relay/old", key);
    }

    [Fact]
    public void FallsBackToIconWhenUrlMissingForDifferentTrack()
    {
        // New track with no art must not inherit the previous track's cover.
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: null, identity: "b", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Equal(Icon, key);
    }

    [Fact]
    public void FallsBackToIconWhenNoPriorArt()
    {
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: "   ", identity: "a", lastKey: null, lastIdentity: null);
        Assert.Equal(Icon, key);
    }
}
