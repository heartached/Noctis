using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Noctis.Localization;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>Switching the Language picker must relabel the open Settings page at once.</summary>
public class LanguageLiveSwitchTests
{
    private readonly ITestOutputHelper _output;
    public LanguageLiveSwitchTests(ITestOutputHelper output) => _output = output;

    // The culture is reset inside each test, on the Avalonia thread, while the window is still
    // up: resetting from Dispose (another thread) re-labels the bound TextBlocks cross-thread
    // and the next headless Window failed to construct.
    private static void Reset(Window? window)
    {
        Loc.Instance.SetCulture("en");
        window?.Close();
    }

    [AvaloniaFact]
    public async Task PickingFrench_RelabelsTheOpenGeneralPage()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.SelectedSettingsTab = SettingsViewModel.TabGeneral;
            var view = new SettingsView { DataContext = vm };
            window = new Window { Width = 920, Height = 720, Content = view };
            window.Show();
            window.UpdateLayout();

            var label = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "Restore last played track");
            var description = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == Loc.T("Settings.Desc.General"));
            var englishDescription = description.Text;

            vm.LanguageChoice = vm.LanguageOptions.Single(o => o.Code == "fr");
            window.UpdateLayout();
            _output.WriteLine($"after switch: '{label.Text}' / '{description.Text}' culture={Loc.Instance.Culture.Name}");
            Assert.Equal("Restaurer le dernier titre lu", label.Text);
            Assert.Equal("fr", vm.LanguageChoice!.Code); // survives the picker rebuild
            Assert.Equal("fr", Loc.Instance.Culture.Name);
            // The page description is a cached Loc.T string; it re-reads too (English until Crowdin
            // carries the key, but the property must at least be re-raised without throwing).
            Assert.Equal(Loc.T("Settings.Desc.General"), description.Text);
            _ = englishDescription;
        }
        finally { Reset(window); try { Directory.Delete(root, true); } catch { } }
    }

    [AvaloniaFact]
    public async Task PickingKorean_RelabelsRailAndTitle_ButKeysStayEnglish()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        Window? window = null;
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
            var view = new SettingsView { DataContext = vm };
            window = new Window { Width = 920, Height = 720, Content = view };
            window.Show();
            window.UpdateLayout();

            var title = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.FontSize == 24 && t.Text == "Account & Devices");
            var rail = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Classes.Contains("rail-group") && t.Text == "Connect");
            var entry = view.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Text == "General" && t.FontSize == 13);

            vm.LanguageChoice = vm.LanguageOptions.Single(o => o.Code == "ko");
            window.UpdateLayout();
            _output.WriteLine($"ko: title '{title.Text}' group '{rail.Text}' entry '{entry.Text}'");
            Assert.Equal("계정 및 기기", title.Text);
            Assert.Equal("연결", rail.Text);
            Assert.Equal("일반", entry.Text);
            // Identity is untouched: tab selection still compares the English constants.
            Assert.Equal(SettingsViewModel.TabAccountDevices, vm.SelectedSettingsTab);
            Assert.Equal(new[] { "App", "Playback", "Library", "Connect", "More" }, vm.SectionGroups.Select(g => g.Name));
            Assert.True(vm.Sections.Single(s => s.Key == SettingsViewModel.TabAccountDevices).IsSelected);
        }
        finally { Reset(window); try { Directory.Delete(root, true); } catch { } }
    }

    [Theory]
    [InlineData("en", "English")]
    [InlineData("es", "Español (Spanish)")]
    [InlineData("ko", "한국어 (Korean)")]
    [InlineData("ru", "Русский (Russian)")]
    [InlineData("tr", "Türkçe (Turkish)")]
    public void LanguageEntries_ShowNativeThenEnglishName(string code, string expected)
        => Assert.Equal(expected, SettingsViewModel.DescribeCulture(code));

    /// <summary>The native name comes from the OS's ICU data, and CLDR spells Simplified
    /// Chinese two ways: "中文（简体）" (Windows' ICU) and "简体中文" (the newer data macOS
    /// ships). Either is the native name; the English one follows it the same way.</summary>
    [Fact]
    public void SimplifiedChinese_ShowsItsNativeThenEnglishName()
        => Assert.Contains(SettingsViewModel.DescribeCulture("zh-Hans"),
            new[] { "中文（简体） (Chinese, Simplified)", "简体中文 (Chinese, Simplified)" });

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public System.Collections.Generic.IReadOnlyList<Noctis.Models.PlayHistoryEvent> Events => Array.Empty<Noctis.Models.PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Noctis.Models.Track track) { }
        public void RecordSkip(Noctis.Models.Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
