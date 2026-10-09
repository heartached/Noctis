using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Converters;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Statistics bars (Discord report 2026-09-07, "Most Skipped bars don't go to the top"):
/// the fill was Percentage × a fixed 400px, so a 100% bar stopped at 400px inside a
/// track that was wider. The fill is now a fraction of the track's laid-out width.
/// </summary>
public class StatisticsBarWidthTests
{
    private readonly ITestOutputHelper _output;
    public StatisticsBarWidthTests(ITestOutputHelper output) => _output = output;

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    [Theory]
    [InlineData(1.0, 620.0, 620.0)]
    [InlineData(0.5, 620.0, 310.0)]
    [InlineData(0.0, 620.0, 0.0)]
    [InlineData(1.0, double.NaN, 0.0)] // before first layout
    public void Converter_IsAFractionOfTheTrack(double fraction, double track, double expected)
    {
        var w = new FractionOfWidthConverter().Convert(new object?[] { fraction, track }, typeof(double), null, null!);
        Assert.Equal(expected, (double)w, 3);
    }

    [AvaloniaFact]
    public void MostSkipped_FullBar_ReachesTheEndOfItsTrack()
    {
        var vm = new StatisticsViewModel(new FakeLibraryService(), new NoOpPlayHistory());
        vm.SelectedTab = StatisticsViewModel.TabHistory; // Most Skipped lives on the History tab
        vm.MostSkipped.Add(new StatsSkipRow { Title = "A Bird's Last Look", Subtitle = "Macabre Plaza", Fraction = 1.0, RateText = "100%" });
        vm.MostSkipped.Add(new StatsSkipRow { Title = "Rush", Subtitle = "Seatbelts", Fraction = 0.5, RateText = "50%" });
        var view = new StatisticsView { DataContext = vm };
        var window = new Window { Width = 1400, Height = 900, Content = view };
        window.Show();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        // Skip-rate fills are the ones whose parent is a 4px-tall clipped track.
        var fills = view.GetVisualDescendants().OfType<Border>()
            .Where(b => b.Parent is Border { Height: 4, ClipToBounds: true } && b.IsEffectivelyVisible)
            .ToList();
        Assert.Equal(2, fills.Count);
        var track = (Border)fills[0].Parent!;
        Assert.Equal(track.Bounds.Width, ((Border)fills[1].Parent!).Bounds.Width, 0.5); // rows share one track width
        _output.WriteLine($"track {track.Bounds.Width} full {fills[0].Bounds.Width} half {fills[1].Bounds.Width}");
        Assert.True(track.Bounds.Width > 400, "test track must be wider than the old 400px cap");
        Assert.Equal(track.Bounds.Width, fills[0].Bounds.Width, 0.5);
        Assert.Equal(track.Bounds.Width / 2, fills[1].Bounds.Width, 0.5);
    }
}
