using Noctis.Models;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>Discord 1v1ctus: "Tint Whole Window" paints the album page colour behind the
/// islands, only on a tinted album page and only while the option is on.</summary>
public class WindowTintTests
{
    private sealed class OtherPage : ViewModelBase { }

    [Fact]
    public void Off_ByDefault()
    {
        Assert.False(new AppSettings().AlbumPageTintWholeWindow);
    }

    [Fact]
    public void NoTint_OnNonAlbumPages()
    {
        Assert.Null(MainWindowViewModel.WindowTintFor(true, null));
        Assert.Null(MainWindowViewModel.WindowTintFor(true, new OtherPage()));
    }
}
