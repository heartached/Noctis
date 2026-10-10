using System.Text.RegularExpressions;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Albums / Artists / Folders sort and type buttons should open and close
/// like the Songs section's. The open motion is global (Styles.axaml sets
/// MenuOpenAnimation.Enable on every presenter); the close motion is opt-in per MenuFlyout
/// (MenuOpenAnimation.EnableFlyoutClose), and six top-bar flyouts lacked it, so they vanished
/// on close. Pins every top-bar MenuFlyout to the close motion.
/// </summary>
public class TopBarFlyoutCloseMotionTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void EveryTopBarMenuFlyout_ClosesWithTheSharedMotion()
    {
        var xaml = File.ReadAllText(Path.Combine(FindRepoRoot(), "src", "Noctis", "Views", "MainWindow.axaml"));
        // Each opening tag, attributes included (they span lines).
        var tags = Regex.Matches(xaml, @"<MenuFlyout\b[^>]*>").Select(m => m.Value).ToList();

        Assert.True(tags.Count >= 10, $"expected the top bar's menus, found {tags.Count}");
        var missing = tags.Where(t => !t.Contains("helpers:MenuOpenAnimation.EnableFlyoutClose=\"True\"")).ToList();
        Assert.True(missing.Count == 0, "MenuFlyout without the close motion:\n" + string.Join("\n", missing));
    }
}
