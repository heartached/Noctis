using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: on Albums, opening Settings from the sidebar moved the highlight to the
/// Settings row, and the Settings sheet's blurred backdrop (snapshotted inside that same
/// click) showed it there; "clicking around" then retook the backdrop and the highlight
/// jumped back to Albums. Settings opens as a sheet over the current page, so its row must
/// never take the selection — not even for the duration of the click.
/// </summary>
public class SidebarSettingsClickTests
{
    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    [AvaloniaTheory]
    [InlineData("albums")]
    [InlineData("artists")]
    public void ClickingSettings_OpensIt_WhileTheHighlightStaysOnTheCurrentSection(string sectionKey)
    {
        EnsureAppStyles();
        var vm = new SidebarViewModel(new TestPersistenceService(), new FakeLibraryService());
        var view = new SidebarView { DataContext = vm, Width = 240 };
        var win = new Window { Width = 400, Height = 900, Content = view };
        win.Show();
        try
        {
            var section = vm.NavItems.First(n => n.Key == sectionKey);
            var settings = vm.NavItems.First(n => n.Key == "settings");
            vm.SelectedNavItem = section;
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();

            var list = view.FindControl<ListBox>("NavList")!;
            var sectionRow = (ListBoxItem)list.ContainerFromItem(section)!;
            var settingsRow = (ListBoxItem)list.ContainerFromItem(settings)!;

            // What the sidebar shows at the moment Settings is asked to open: the sheet's
            // backdrop snapshot is requested synchronously from this request.
            var requests = new List<string>();
            bool? settingsLitAtRequest = null, sectionLitAtRequest = null;
            object? vmSelectionAtRequest = null;
            vm.NavigationRequested += (_, key) =>
            {
                requests.Add(key);
                settingsLitAtRequest = settingsRow.IsSelected;
                sectionLitAtRequest = sectionRow.IsSelected;
                vmSelectionAtRequest = vm.SelectedNavItem;
            };

            list.SelectedItem = settings; // the click's selection

            Assert.Equal(new[] { "settings" }, requests);
            Assert.False(settingsLitAtRequest, "Settings row was highlighted when the sheet opened");
            Assert.True(sectionLitAtRequest, $"{sectionKey} lost its highlight when the sheet opened");
            Assert.Same(section, vmSelectionAtRequest);

            // And it stays that way once everything posted has run.
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Assert.Same(section, vm.SelectedNavItem);
            Assert.Same(section, list.SelectedItem);
            Assert.False(settingsRow.IsSelected);
            Assert.True(sectionRow.IsSelected);

            // A second click on Settings opens it again (the row never held the selection).
            list.SelectedItem = settings;
            Assert.Equal(new[] { "settings", "settings" }, requests);
            Assert.Same(section, list.SelectedItem);
        }
        finally { win.Close(); }
    }
}
