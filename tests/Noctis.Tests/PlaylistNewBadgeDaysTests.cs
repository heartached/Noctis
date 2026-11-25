using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia.Headless.XUnit;
using Noctis.Converters;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-23: the playlist page's NEW badge was always "the last 7 days". Settings › Playlist page
/// now picks how long a track stays NEW (1, 3, 7, 14 or 30 days; 7 as before). The badge's
/// converter takes the days as one more binding (no settings lookup per row).
/// </summary>
[Collection("Localization")]
public class PlaylistNewBadgeDaysTests : IDisposable
{
    public PlaylistNewBadgeDaysTests() => Loc.Instance.SetCulture("en");
    public void Dispose() => Loc.Instance.SetCulture("en");

    private static object? New(DateTime added, object? days)
    {
        var id = Guid.NewGuid();
        var map = new Dictionary<Guid, DateTime> { [id] = added };
        var values = days == null
            ? new object?[] { id, added, map, true }
            : new object?[] { id, added, map, true, days };
        return new PlaylistAddedConverter().Convert(values, typeof(bool), "new", CultureInfo.InvariantCulture);
    }

    [Fact]
    public void Converter_FollowsTheChosenDays_SevenWhenNotGiven()
    {
        var fiveDaysAgo = DateTime.UtcNow.AddDays(-5);
        Assert.Equal(false, New(fiveDaysAgo, 3));
        Assert.Equal(true, New(fiveDaysAgo, 7));
        Assert.Equal(true, New(fiveDaysAgo, null));
        Assert.Equal(false, New(DateTime.UtcNow.AddDays(-10), null));
        Assert.Equal(true, New(DateTime.UtcNow.AddDays(-10), 14));
        Assert.Equal(true, New(DateTime.UtcNow.AddDays(-29), 30));
        Assert.Equal(false, New(DateTime.UtcNow.AddHours(-30), 1));
        Assert.Equal(true, New(DateTime.UtcNow.AddHours(-20), 1));
    }

    [Fact]
    public void Settings_DefaultIsSeven_AndAHandEditedValueSnaps()
    {
        Assert.Equal(7, new AppSettings().PlaylistNewBadgeDays);
        Assert.Equal(new[] { 1, 3, 7, 14, 30 }, AppSettings.PlaylistNewBadgeDayChoices);
        Assert.Equal(14, AppSettings.SnapPlaylistNewBadgeDays(12));
        Assert.Equal(1, AppSettings.SnapPlaylistNewBadgeDays(0));
        Assert.Equal(30, AppSettings.SnapPlaylistNewBadgeDays(365));
    }

    [AvaloniaFact]
    public void SettingsViewModel_OffersTheChoices_AndThePickSetsTheDays()
    {
        var vm = new SettingsViewModel(new TestPersistenceService(), new FakeLibraryService(), new ContentPackThemeTests.NoOpHistory());
        Assert.Equal(new[] { "1 day", "3 days", "7 days", "14 days", "30 days" },
            vm.PlaylistNewBadgeDayOptions.Select(o => o.Label));
        Assert.Equal(7, vm.SelectedPlaylistNewBadgeDaysOption?.Days);

        vm.SelectedPlaylistNewBadgeDaysOption = vm.PlaylistNewBadgeDayOptions[4];
        Assert.Equal(30, vm.PlaylistNewBadgeDays);
        // A ComboBox nulls its selection while it rebuilds: ignored.
        vm.SelectedPlaylistNewBadgeDaysOption = null;
        Assert.Equal(30, vm.PlaylistNewBadgeDays);
    }
}
