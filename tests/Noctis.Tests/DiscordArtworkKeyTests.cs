using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DiscordArtworkKeyTests
{
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
    public void OmitsImageWhenUrlMissingForDifferentTrack()
    {
        // New track with no art must not inherit the previous track's cover. It also must
        // not name an asset the application doesn't have (it has none) — that rendered a
        // broken-image placeholder instead of no image.
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: null, identity: "b", lastKey: "https://relay/old", lastIdentity: "a");
        Assert.Null(key);
    }

    [Fact]
    public void OmitsImageWhenNoPriorArt()
    {
        var key = DiscordPresenceService.ResolveArtworkKey(
            incomingUrl: "   ", identity: "a", lastKey: null, lastIdentity: null);
        Assert.Null(key);
    }
}
