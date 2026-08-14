using System;
using System.IO;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Favorite artists (GitHub #41) must survive a restart — a "restart" here is a
/// fresh service instance over the same file — and match names the way the
/// library does (artist names compare case-insensitively).
/// </summary>
public sealed class FavoriteArtistsServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public FavoriteArtistsServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "noctis-fav-artists-" + Guid.NewGuid().ToString("N"));
        _file = Path.Combine(_dir, "favorite_artists.json");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void SetFavorite_PersistsAcrossRestart()
    {
        new FavoriteArtistsService(_file).SetFavorite("Juice WRLD", true);

        var reloaded = new FavoriteArtistsService(_file);
        Assert.True(reloaded.IsFavorite("Juice WRLD"));
        Assert.False(reloaded.IsFavorite("Bad Bunny"));
    }

    [Fact]
    public void IsFavorite_IsCaseInsensitive()
    {
        var service = new FavoriteArtistsService(_file);
        service.SetFavorite("Bad Bunny", true);

        Assert.True(service.IsFavorite("bad bunny"));
        Assert.True(new FavoriteArtistsService(_file).IsFavorite("BAD BUNNY"));
    }

    [Fact]
    public void RemoveFavorite_PersistsAcrossRestart()
    {
        var service = new FavoriteArtistsService(_file);
        service.SetFavorite("Charli xcx", true);
        service.SetFavorite("Charli xcx", false);

        Assert.False(service.IsFavorite("Charli xcx"));
        Assert.False(new FavoriteArtistsService(_file).IsFavorite("Charli xcx"));
    }

    [Fact]
    public void NullOrWhitespaceNames_AreIgnored()
    {
        var service = new FavoriteArtistsService(_file);
        service.SetFavorite(" ", true);

        Assert.False(service.IsFavorite(" "));
        Assert.False(service.IsFavorite(null));
        Assert.False(File.Exists(_file));
    }
}
