using Avalonia.Controls;
using Avalonia.Input;
using Noctis.ViewModels;

namespace Noctis.Views;

public partial class StatisticsView : UserControl
{
    public StatisticsView()
    {
        InitializeComponent();
    }

    /// <summary>GitHub #222: a Last.fm row the library has opens its artist or album page; every
    /// other row stays read-only (the commands ignore rows that aren't linked).</summary>
    private void OnStatsItemTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not StatisticsViewModel vm || sender is not Control { DataContext: { } item }) return;
        switch (item)
        {
            case StatsArtistTile { IsLinked: true } tile:
                vm.OpenArtistCommand.Execute(tile);
                e.Handled = true;
                break;
            case StatsRankRow { IsLinked: true } row:
                vm.OpenRankRowCommand.Execute(row);
                e.Handled = true;
                break;
        }
    }
}
