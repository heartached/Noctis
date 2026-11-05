using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Headless.XUnit;
using CommunityToolkit.Mvvm.Input;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #176: the Albums View menu's carousel case styles (Cover / Jewel Case / Vinyl /
/// Cassette) do nothing in the grid, so they are greyed out there (IsEnabled, so screen
/// readers hear "unavailable" too) and come back the moment the carousel is up. A tooltip
/// on the disabled items says why. Settings › Player stays editable: the case is shared
/// with the artist page carousel, and settings pages keep every control live.
/// </summary>
public class AlbumsViewMenuCaseStyleTests
{
    private static readonly string[] Styles = { "Cover", "JewelCase", "Vinyl", "Cassette" };

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + System.AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryCaseStyleItem_IsEnabledOnlyInTheCarousel_WithTheExplainingTooltip()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "Views", "MainWindow.axaml"));
        foreach (var style in Styles)
        {
            var tag = Regex.Match(xaml,
                $@"<MenuItem\b[^>]*CommandParameter=""{style}""[^>]*>").Value;
            Assert.Contains("Command=\"{Binding AlbumCarouselStyleCommand}\"", tag);
            Assert.Contains("IsEnabled=\"{Binding IsAlbumCarouselView}\"", tag);
            Assert.Contains("ToolTip.Tip=\"{loc:T Main.CarouselStyleCarouselOnly}\"", tag);
            Assert.Contains("ToolTip.ShowOnDisabled=\"True\"", tag);
            Assert.Contains("ToolTip.ServiceEnabled=\"{Binding !IsAlbumCarouselView}\"", tag);
        }
    }

    [Fact]
    public void TooltipString_ExistsInEveryLanguageThatHasTheCaseStyles()
    {
        var dir = Path.Combine(FindRepoRoot(), "src", "Noctis.UI", "Localization");
        var files = Directory.GetFiles(dir, "Strings*.resx")
            .Where(f => File.ReadAllText(f).Contains("name=\"Main.CarouselCassette\""))
            .ToList();
        Assert.NotEmpty(files);
        foreach (var f in files)
            Assert.Contains("name=\"Main.CarouselStyleCarouselOnly\"", File.ReadAllText(f));
    }

    [AvaloniaFact]
    public void BoundItem_FollowsTheView_EvenWithAnExecutableCommand()
    {
        var bar = new TopBarViewModel { AlbumCarouselStyleCommand = new RelayCommand<string>(_ => { }) };
        var item = new MenuItem { DataContext = bar, CommandParameter = "Vinyl" };
        item.Bind(MenuItem.CommandProperty, new Binding(nameof(TopBarViewModel.AlbumCarouselStyleCommand)));
        item.Bind(Avalonia.Input.InputElement.IsEnabledProperty, new Binding(nameof(TopBarViewModel.IsAlbumCarouselView)));

        // Grid (the default view): greyed, not just dimmed.
        Assert.False(bar.IsAlbumCarouselView);
        Assert.False(item.IsEnabled);
        Assert.False(item.IsEffectivelyEnabled);

        bar.IsAlbumCarouselView = true;
        Assert.True(item.IsEnabled);
        Assert.True(item.IsEffectivelyEnabled);

        bar.IsAlbumCarouselView = false;
        Assert.False(item.IsEffectivelyEnabled);
    }
}
