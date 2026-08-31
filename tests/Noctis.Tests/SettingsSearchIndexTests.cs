using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

public class SettingsSearchIndexTests
{
    private static Border Card(params string[] texts)
    {
        var stack = new StackPanel();
        foreach (var t in texts) stack.Children.Add(new TextBlock { Text = t });
        var card = new Border { Child = stack };
        card.Classes.Add(SettingsSearchIndex.CardClass);
        return card;
    }

    [AvaloniaFact]
    public void Build_IndexesCardText_AndQueryMatchesEveryWord()
    {
        var general = new StackPanel();
        var tray = Card("Startup & Window Behaviour", "Minimize to tray", "Close to tray");
        var anim = Card("Text Animation", "Animate long track titles");
        general.Children.Add(tray);
        general.Children.Add(anim);
        var audio = new StackPanel();
        var eq = Card("Equalizer", "Preamp");
        audio.Children.Add(eq);

        var index = SettingsSearchIndex.Build(new[] { ("General", (Control)general), ("Audio", (Control)audio) });

        Assert.Equal(3, index.Entries.Count);
        Assert.Same(tray, Assert.Single(index.Query("minimize")).Card);
        Assert.Same(tray, Assert.Single(index.Query("close TRAY")).Card);
        Assert.Empty(index.Query("minimize animation"));      // words must all hit the same card
        Assert.Empty(index.Query(""));
        Assert.Equal(1, index.CountByTab("tray")["General"]);
        Assert.Equal("Audio", index.FirstMatch("preamp", "General")!.Tab);
    }

    [AvaloniaFact]
    public void Apply_HidesNonMatchingCards_ByClass_AndRestores()
    {
        var panel = new StackPanel();
        var tray = Card("Minimize to tray");
        var anim = Card("Text Animation");
        panel.Children.Add(tray);
        panel.Children.Add(anim);
        var index = SettingsSearchIndex.Build(new[] { ("General", (Control)panel) });

        index.Apply("tray");
        Assert.DoesNotContain(SettingsSearchIndex.HiddenClass, tray.Classes);
        Assert.Contains(SettingsSearchIndex.HiddenClass, anim.Classes);

        index.Apply("");
        Assert.DoesNotContain(SettingsSearchIndex.HiddenClass, tray.Classes);
        Assert.DoesNotContain(SettingsSearchIndex.HiddenClass, anim.Classes);
    }

    /// <summary>End to end through the real SettingsView: typing in the rail's search box
    /// filters cards, badges the rail, and jumps to the first section with hits.</summary>
    [AvaloniaFact]
    public async Task SettingsView_SearchBox_FiltersCards_AndSwitchesSection()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        try
        {
            var vm = new SettingsViewModel(new PersistenceService(root), new FakeLibraryService(), new NoOpPlayHistory());
            await vm.LoadAsync();
            var view = new SettingsView { DataContext = vm };
            var window = new Window { Width = 920, Height = 720, Content = view };
            window.Show();

            var box = view.FindControl<TextBox>("SettingsSearchBox");
            Assert.NotNull(box);

            box!.Text = "minimize to tray";
            var cards = view.GetLogicalDescendants().OfType<Border>()
                .Where(b => b.Classes.Contains(SettingsSearchIndex.CardClass)).ToList();
            var trayCard = cards.Single(c => c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Minimize to tray"));
            var animCard = cards.Single(c => c.GetLogicalDescendants().OfType<TextBlock>().Any(t => t.Text == "Text Animation"));
            Assert.DoesNotContain(SettingsSearchIndex.HiddenClass, trayCard.Classes);
            Assert.Contains(SettingsSearchIndex.HiddenClass, animCard.Classes);
            Assert.True(vm.Sections.Single(s => s.Key == SettingsViewModel.TabGeneral).MatchCount >= 1);
            Assert.Equal(SettingsViewModel.TabGeneral, vm.SelectedSettingsTab);

            // A query with hits only in another section jumps there.
            box.Text = "Keyboard shortcuts";
            Assert.Equal(SettingsViewModel.TabShortcuts, vm.SelectedSettingsTab);

            box.Text = "";
            Assert.DoesNotContain(SettingsSearchIndex.HiddenClass, animCard.Classes);
            Assert.All(vm.Sections, s => Assert.Equal(0, s.MatchCount));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class NoOpPlayHistory : IPlayHistoryService
    {
        public System.Collections.Generic.IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }
}
