using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// 11-03 Settings audit: the Settings › Statistics tab's numbers agree with the Statistics page.
/// </summary>
public class SettingsStatisticsTabTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private async Task<SettingsViewModel> Refreshed(FakeLibraryService library, params PlayHistoryEvent[] log)
    {
        var vm = new SettingsViewModel(new PersistenceService(_root), library, new StatisticsNumbersTests.History(log));
        var task = vm.RefreshLibraryStatsAsync();
        for (var i = 0; i < 500 && !task.IsCompleted; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(2);
        }
        Dispatcher.UIThread.RunJobs();
        await task;
        return vm;
    }

    // HEAD: Expected "4:00", Actual "3:30": the tab averaged the logged plays (a play of a song
    // no longer in the library counts as 3.5 min), while the Statistics page's tile of the same
    // name shows the library's average (StatisticsNumbersTests.LibraryAvgLength_IsTheLibraryAverage).
    [AvaloniaFact]
    public async Task AvgTrackLength_IsTheLibraryAverage_LikeTheStatisticsPage()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "A", Artist = "X", Duration = TimeSpan.FromMinutes(2) });
        library.TrackList.Add(new Track { Title = "B", Artist = "X", Duration = TimeSpan.FromMinutes(6) });
        var gone = StatisticsNumbersTests.Play(Guid.NewGuid(), "Removed", "Nobody", DateTime.UtcNow.AddHours(-1));

        var vm = await Refreshed(library, gone);

        Assert.Equal("4:00", vm.AvgTrackLength);
    }

    // HEAD: Expected "1:15:00", Actual "15:00" (m\:ss drops the hours).
    [AvaloniaFact]
    public async Task AvgTrackLength_OverAnHour_KeepsTheHours()
    {
        var library = new FakeLibraryService();
        library.TrackList.Add(new Track { Title = "Mix", Artist = "DJ", Duration = TimeSpan.FromMinutes(75) });

        var vm = await Refreshed(library);

        Assert.Equal("1:15:00", vm.AvgTrackLength);
    }

    [AvaloniaFact]
    public async Task EmptyLibrary_ReadsZero()
    {
        var vm = await Refreshed(new FakeLibraryService());

        Assert.Equal("0:00", vm.AvgTrackLength);
        Assert.Equal(0, vm.TotalSongs);
        Assert.Equal("0%", vm.LosslessPercentageText);
    }
}
