using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Artist Separators on the phone (Discord beta feedback: "an artist separator tag
/// setting"): the desktop's Artist Tag Separators rules (trimmed, no repeat ignoring case, the last
/// one removed puts the defaults back), saved in the phone's settings, applied to the tokenizer at
/// once with a regroup, and applied at start before the library builds its artist list. Touches the
/// process-wide <see cref="ArtistCredit"/>, so it runs in that non-parallel collection and puts the
/// defaults back.
/// </summary>
[Collection("ArtistCredit global configuration")]
public class MobileArtistSeparatorsTests : IDisposable
{
    private static readonly string[] Defaults = ArtistCredit.DefaultSeparators.ToArray();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public MobileArtistSeparatorsTests() => ArtistCredit.ResetToDefaults();

    public void Dispose()
    {
        ArtistCredit.ResetToDefaults();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string[] Rows(ArtistSeparatorsViewModel vm) => vm.Separators.Select(r => r.Text).ToArray();

    [Fact]
    public async Task Add_TrimsSkipsRepeats_AppliesSavesAndRegroups()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new ArtistSeparatorsViewModel(rig.Shell);
        Assert.Equal(Defaults, Rows(vm));
        Assert.True(vm.IsDefault);
        Assert.Equal("6", vm.Summary);
        Assert.False(vm.AddCommand.CanExecute(null));          // empty field
        Assert.Equal(new[] { "Boys Noize x ABRA" }, ArtistCredit.Split("Boys Noize x ABRA"));

        vm.NewSeparator = "  x  ";
        Assert.True(vm.AddCommand.CanExecute(null));
        vm.AddCommand.Execute(null);
        Assert.Equal(string.Empty, vm.NewSeparator);
        Assert.Equal(Defaults.Append("x"), Rows(vm));
        Assert.False(vm.IsDefault);
        Assert.Equal("7", vm.Summary);
        Assert.Equal(new[] { "Boys Noize", "ABRA" }, ArtistCredit.Split("Boys Noize x ABRA"));
        Assert.Equal(1, rig.Library.MetadataChangedCount);      // the library rebuilds its artists
        await vm.PendingSave;
        Assert.Equal(Defaults.Append("x"), (await rig.Persistence.LoadSettingsAsync()).ArtistTagSeparators);

        // A repeat in another case, or blanks: nothing changes, nothing regroups.
        vm.NewSeparator = "FEAT.";
        vm.AddCommand.Execute(null);
        vm.NewSeparator = "   ";
        vm.AddCommand.Execute(null);
        Assert.Equal(Defaults.Append("x"), Rows(vm));
        Assert.Equal(1, rig.Library.MetadataChangedCount);
    }

    [Fact]
    public async Task Remove_AndReset_FollowTheDesktopRules()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new ArtistSeparatorsViewModel(rig.Shell);

        // "," out: the tester's "Boys Noize, ABRA" stays one artist.
        vm.RemoveCommand.Execute(vm.Separators.Single(r => r.Text == ","));
        Assert.DoesNotContain(",", Rows(vm));
        Assert.Equal(new[] { "Boys Noize, ABRA" }, ArtistCredit.Split("Boys Noize, ABRA"));
        Assert.Equal("Remove /", vm.Separators[0].RemoveName);

        // Down to one, then the last one out: the defaults come back (an empty list splits nothing).
        foreach (var row in vm.Separators.Skip(1).ToList()) vm.RemoveCommand.Execute(row);
        Assert.Equal(new[] { "/" }, Rows(vm));
        vm.RemoveCommand.Execute(vm.Separators.Single());
        Assert.Equal(Defaults, Rows(vm));
        Assert.True(vm.IsDefault);
        Assert.Equal(Defaults, ArtistCredit.Separators);

        vm.NewSeparator = "&";
        vm.AddCommand.Execute(null);
        Assert.False(vm.IsDefault);
        vm.ResetCommand.Execute(null);
        Assert.Equal(Defaults, Rows(vm));
        Assert.Equal(Defaults, ArtistCredit.Separators);
        await vm.PendingSave;
        Assert.Equal(Defaults, (await rig.Persistence.LoadSettingsAsync()).ArtistTagSeparators);
    }

    /// <summary>Separators with spaces inside or regex characters split literally.</summary>
    [Fact]
    public void SeparatorsWithSpacesOrRegexCharacters_SplitLiterally()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new ArtistSeparatorsViewModel(rig.Shell);
        foreach (var s in new[] { "with love", "|", "+", "(x)", ".*", "a.k.a." })
        {
            vm.NewSeparator = s;
            vm.AddCommand.Execute(null);
        }
        Assert.Equal(new[] { "A", "B" }, ArtistCredit.Split("A with love B"));
        Assert.Equal(new[] { "Andrea with lovers" }, ArtistCredit.Split("Andrea with lovers"));   // whole words only
        Assert.Equal(new[] { "A", "B", "C" }, ArtistCredit.Split("A | B + C"));
        Assert.Equal(new[] { "A", "B" }, ArtistCredit.Split("A (x) B"));
        Assert.Equal(new[] { "A", "B" }, ArtistCredit.Split("A .* B"));
        Assert.Equal(new[] { "ABBA" }, ArtistCredit.Split("ABBA"));                               // ".*" is not a wildcard
        Assert.Equal(new[] { "Prince", "The Artist" }, ArtistCredit.Split("Prince a.k.a. The Artist"));
    }

    [Fact]
    public async Task TheSettingsRow_ShowsTheCount_AndThePaneOpensOnTheActiveSeparators()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.OpenSettingsCommand.Execute(null);
        var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await settings.Loaded;
        Assert.Equal("Artist Separators, 6", settings.ArtistSeparatorsRowName);

        // Applied after the sheet was made (the start's read): opening the page shows them.
        ArtistCredit.Configure(ArtistGroupMode.Artist, new[] { "/", "&" });
        settings.OpenPaneCommand.Execute(SettingsPane.ArtistSeparators);
        Assert.Equal("Artist Separators", settings.HeaderTitle);
        Assert.True(settings.ShowsArtistSeparatorsPane);
        Assert.Equal(new[] { "/", "&" }, Rows(settings.ArtistSeparators));
        Assert.Equal("Artist Separators, 2", settings.ArtistSeparatorsRowName);
    }

    [Fact]
    public void ApplySaved_ConfiguresTheTokenizerFromTheSettings()
    {
        ArtistSeparatorsViewModel.ApplySaved(new AppSettings
        {
            ArtistTagSeparators = { "x" },
            ArtistGroupMode = nameof(ArtistGroupMode.AlbumArtist),
        });
        Assert.Equal(Defaults.Append("x"), ArtistCredit.Separators);
        Assert.Equal(ArtistGroupMode.AlbumArtist, ArtistCredit.GroupMode);
        Assert.Equal(ArtistCredit.DefaultProtectedNames, ArtistCredit.ProtectedNames);
    }

    /// <summary>
    /// The phone's start: before this the separators were applied only by LibraryService's
    /// background pass, after the artist list had been built (or restored) under the defaults, and
    /// nothing rebuilt it. Applied first (AndroidApp.StartAsync), a restart keeps the split.
    /// </summary>
    [Fact]
    public async Task AfterARestart_TheArtistListIsBuiltUnderTheSavedSeparators()
    {
        var music = Path.Combine(_root, "music");
        Directory.CreateDirectory(music);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        var path = Path.Combine(music, "song.mp3");
        using (var fs = File.Create(path))
            for (int i = 0; i < 40; i++) fs.Write(frame, 0, frame.Length);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = "Song";
            f.Tag.Performers = new[] { "Boys Noize x ABRA" };
            f.Save();
        }
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music } });

        ArtistCredit.Configure(ArtistGroupMode.Artist, Defaults.Append("x"));
        await persistence.UpdateSettingsAsync(s => s.ArtistTagSeparators = Defaults.Append("x").ToList());
        var library = NewLibrary(persistence);
        await library.ScanAsync(new[] { music });
        if (library.AllTracks.Count != 1) Assert.Fail(ScanMissReport.Describe(library, music));
        // The list goes by the first credited artist (Track.GroupingArtist), as on the desktop.
        Assert.Equal("Boys Noize", Assert.Single(library.Artists).Name);

        // A new process: the tokenizer starts on the defaults.
        ArtistCredit.ResetToDefaults();
        ArtistSeparatorsViewModel.ApplySaved(await persistence.LoadSettingsAsync());
        var reloaded = NewLibrary(persistence);
        await reloaded.LoadAsync();
        await reloaded.BackgroundInit;
        Assert.Equal("Boys Noize", Assert.Single(reloaded.Artists).Name);   // not "Boys Noize x ABRA"
    }

    private static LibraryService NewLibrary(PersistenceService persistence) =>
        new(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());

    [AvaloniaFact]
    public async Task ThePane_ListsRemovesAndAdds()
    {
        using var rig = MobileFixtures.MakeRig();
        await rig.Shell.InitializeAsync();
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            window.UpdateLayout();
            var page = MobileFixtures.Find<SettingsPage>(view);
            Assert.True(MobileFixtures.Named<Button>(page, "ArtistSeparatorsRow").IsEffectivelyVisible);
            vm.OpenPaneCommand.Execute(SettingsPane.ArtistSeparators);
            MobileFixtures.Land(window);
            Pump(window);
            var pane = MobileFixtures.Named<ArtistSeparatorsPane>(page, "ArtistSeparatorsPane");
            Assert.True(pane.IsEffectivelyVisible);
            var removes = RemoveButtons(pane);
            Assert.Equal(Defaults.Length, removes.Length);
            Assert.All(removes, b => Assert.True(b.Bounds.Height >= 44));
            // Hairlines between the rows, none above the first.
            Assert.Equal(Defaults.Length - 1, pane.GetVisualDescendants().OfType<Border>()
                .Count(b => b.Classes.Contains("row-separator") && b.IsEffectivelyVisible));
            Assert.False(MobileFixtures.Named<Button>(pane, "SeparatorsResetButton").IsEnabled);

            removes[1].Command!.Execute(removes[1].CommandParameter);   // ","
            Pump(window);
            Assert.Equal(Defaults.Length - 1, RemoveButtons(pane).Length);
            Assert.True(MobileFixtures.Named<Button>(pane, "SeparatorsResetButton").IsEnabled);

            MobileFixtures.Named<TextBox>(pane, "NewSeparatorBox").Text = " & ";
            Pump(window);
            var add = MobileFixtures.Named<Button>(pane, "AddSeparatorButton");
            Assert.True(add.IsEffectivelyEnabled);
            add.Command!.Execute(null);
            Pump(window);
            Assert.Equal(string.Empty, MobileFixtures.Named<TextBox>(pane, "NewSeparatorBox").Text ?? string.Empty);
            Assert.Equal("&", ((ArtistSeparatorRow)RemoveButtons(pane)[^1].DataContext!).Text);
            Assert.Equal(new[] { "Simon", "Garfunkel" }, ArtistCredit.Split("Simon & Garfunkel"));

            MobileFixtures.Named<Button>(pane, "SeparatorsResetButton").Command!.Execute(null);
            Pump(window);
            Assert.Equal(Defaults.Length, RemoveButtons(pane).Length);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }

        static Button[] RemoveButtons(ArtistSeparatorsPane p) =>
            p.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is ArtistSeparatorRow).ToArray();
    }

    private static void Pump(Window window, int ticks = 4)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            window.UpdateLayout();
        }
    }
}
