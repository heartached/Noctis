using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Library › Show Star Ratings (Discord 11-24, Halil): off hides the stars for people
/// who never rate. The Songs page Rating column shows only with both this and its own column
/// flag on (the flag keeps the user's value); the Rate menu item hides; on is the default.
/// </summary>
public class ShowStarRatingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "starratings-" + Guid.NewGuid().ToString("N"));

    public ShowStarRatingsTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private SettingsViewModel NewSettings() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    [Fact]
    public void DefaultIsOn()
    {
        Assert.True(new AppSettings().ShowStarRatings);
    }

    [AvaloniaFact]
    public async Task Setting_IsSavedLoadedAndReset()
    {
        var settings = NewSettings();
        await settings.LoadAsync();
        Assert.True(settings.ShowStarRatings);

        settings.ShowStarRatings = false;
        await settings.SaveAsync();

        var reloaded = NewSettings();
        await reloaded.LoadAsync();
        Assert.False(reloaded.ShowStarRatings);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.True(reloaded.ShowStarRatings);
    }

    [AvaloniaFact]
    public async Task SongsRatingColumn_NeedsBothFlags_AndKeepsTheColumnFlag()
    {
        var settings = NewSettings();
        await settings.LoadAsync();
        var layout = LibraryColumns.CreateSongs();
        using (LibraryColumns.BindSongs(layout, settings))
        {
            Assert.True(layout.IsVisible("Rating"));

            settings.ShowStarRatings = false;
            Assert.False(layout.IsVisible("Rating"));
            Assert.True(settings.ShowRatingColumn); // the user's column choice is untouched

            settings.ShowStarRatings = true;
            Assert.True(layout.IsVisible("Rating"));

            // Column off by the user: turning the stars back on doesn't bring it back.
            settings.ShowRatingColumn = false;
            settings.ShowStarRatings = false;
            settings.ShowStarRatings = true;
            Assert.False(layout.IsVisible("Rating"));
        }

        // Opening the page with the stars off starts without the column.
        settings.ShowRatingColumn = true;
        settings.ShowStarRatings = false;
        var fresh = LibraryColumns.CreateSongs();
        using (LibraryColumns.BindSongs(fresh, settings))
            Assert.False(fresh.IsVisible("Rating"));
    }

    [AvaloniaFact]
    public async Task HeaderMenu_LeavesOutRating_WhileStarsAreOff()
    {
        var settings = NewSettings();
        await settings.LoadAsync();
        var layout = LibraryColumns.CreateSongs();
        using (LibraryColumns.BindSongs(layout, settings))
        {
            Assert.Contains(layout.MenuColumns, s => s.Key == "Rating");
            settings.ShowStarRatings = false;
            Assert.DoesNotContain(layout.MenuColumns, s => s.Key == "Rating");
            Assert.Contains(layout.MenuColumns, s => s.Key == "Genre");
            settings.ShowStarRatings = true;
            Assert.Contains(layout.MenuColumns, s => s.Key == "Rating");
            settings.ShowStarRatings = false;
        }
        // Unbound, the layout offers every column again.
        Assert.Contains(layout.MenuColumns, s => s.Key == "Rating");
        Assert.DoesNotContain(layout.MenuColumns, s => s.Pinned);
    }

    [AvaloniaFact]
    public async Task RateMenuItem_HidesWithTheSetting()
    {
        var settings = NewSettings();
        await settings.LoadAsync();
        var rate = new RelayCommand<RateRequest>(_ => { });

        Assert.True(TrackContextMenuBuilder.ShowsRate(rate, settings));
        Assert.True(TrackContextMenuBuilder.ShowsRate(rate, null));
        Assert.False(TrackContextMenuBuilder.ShowsRate(null, settings));

        settings.ShowStarRatings = false;
        Assert.False(TrackContextMenuBuilder.ShowsRate(rate, settings));
    }
}
