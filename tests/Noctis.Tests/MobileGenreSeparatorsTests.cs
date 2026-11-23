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
/// Settings › Genre Separators on the phone (GitHub #260 follow-up): the desktop's Genre Tag
/// Separators rules (trimmed, no repeat ignoring case, ';' always on so never listed, an empty
/// list allowed), saved in the phone's settings, applied at once with a regroup, and applied at
/// start before the library is read. Touches the process-wide <see cref="GenreSeparators"/>, so it
/// runs in that non-parallel collection and puts the defaults back.
/// </summary>
[Collection("GenreSeparators global configuration")]
public class MobileGenreSeparatorsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public MobileGenreSeparatorsTests() => GenreSeparators.ResetToDefaults();

    public void Dispose()
    {
        GenreSeparators.ResetToDefaults();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static string[] Rows(GenreSeparatorsViewModel vm) => vm.Separators.Select(r => r.Text).ToArray();

    [Fact]
    public async Task Add_TrimsSkipsRepeatsAndTheSemicolon_AppliesSavesAndRegroups()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new GenreSeparatorsViewModel(rig.Shell);
        Assert.Equal(new[] { "," }, Rows(vm));
        Assert.True(vm.IsDefault);
        Assert.False(vm.IsEmpty);
        Assert.Equal("1", vm.Summary);
        Assert.False(vm.AddCommand.CanExecute(null));          // empty field
        Assert.Equal(new[] { "Hip-Hop/Rap" }, Track.SplitGenres("Hip-Hop/Rap"));

        vm.NewSeparator = "  /  ";
        Assert.True(vm.AddCommand.CanExecute(null));
        vm.AddCommand.Execute(null);
        Assert.Equal(string.Empty, vm.NewSeparator);
        Assert.Equal(new[] { ",", "/" }, Rows(vm));
        Assert.False(vm.IsDefault);
        Assert.Equal("2", vm.Summary);
        Assert.Equal(new[] { "Hip-Hop", "Rap" }, Track.SplitGenres("Hip-Hop/Rap"));
        Assert.Equal(1, rig.Library.MetadataChangedCount);      // the library rebuilds its albums
        await vm.PendingSave;
        Assert.Equal(new[] { ",", "/" }, (await rig.Persistence.LoadSettingsAsync()).GenreTagSeparators);

        // ';' (always on), a repeat or blanks: nothing changes, nothing regroups.
        foreach (var s in new[] { ";", " ; ", ",", "   " })
        {
            vm.NewSeparator = s;
            vm.AddCommand.Execute(null);
        }
        Assert.Equal(new[] { ",", "/" }, Rows(vm));
        Assert.Equal(1, rig.Library.MetadataChangedCount);
    }

    [Fact]
    public async Task Remove_LeavesAnEmptyList_OnlyTheSemicolonSplits_AndResetPutsTheCommaBack()
    {
        using var rig = MobileFixtures.MakeRig();
        var vm = new GenreSeparatorsViewModel(rig.Shell);
        Assert.Equal(new[] { "Rock", "Pop" }, Track.SplitGenres("Rock, Pop"));

        vm.RemoveCommand.Execute(vm.Separators.Single());
        // Unlike the artist separators an empty list stays empty: ';' still splits.
        Assert.Empty(Rows(vm));
        Assert.True(vm.IsEmpty);
        Assert.False(vm.IsDefault);
        Assert.Equal("0", vm.Summary);
        Assert.Empty(GenreSeparators.Configured);
        Assert.Equal(new[] { "Rock, Pop" }, Track.SplitGenres("Rock, Pop"));
        Assert.Equal(new[] { "Rock", "Pop" }, Track.SplitGenres("Rock; Pop"));
        Assert.Equal(1, rig.Library.MetadataChangedCount);
        await vm.PendingSave;
        Assert.Empty((await rig.Persistence.LoadSettingsAsync()).GenreTagSeparators);

        vm.ResetCommand.Execute(null);
        Assert.Equal(new[] { "," }, Rows(vm));
        Assert.True(vm.IsDefault);
        Assert.False(vm.IsEmpty);
        Assert.Equal(new[] { "," }, GenreSeparators.Configured);
        Assert.Equal(2, rig.Library.MetadataChangedCount);
        await vm.PendingSave;
        Assert.Equal(new[] { "," }, (await rig.Persistence.LoadSettingsAsync()).GenreTagSeparators);
        Assert.Equal("Remove ,", vm.Separators[0].RemoveName);
    }

    /// <summary>What the phone shows of the split: the album page's "Genre · Year" line, rebuilt
    /// from the fresh album the regroup hands it.</summary>
    [Fact]
    public void AChange_RegroupsTheAlbumPagesGenreLine()
    {
        using var rig = MobileFixtures.MakeRig();
        var album = new Album { Id = Guid.NewGuid(), Name = "A", Artist = "B", Year = 2018, Genre = "Rock, Pop" };
        Assert.Equal("Rock · Pop · 2018", AlbumPageViewModel.BuildMetaLine(album));
        var vm = new GenreSeparatorsViewModel(rig.Shell);
        vm.RemoveCommand.Execute(vm.Separators.Single());
        Assert.Equal("Rock, Pop · 2018", AlbumPageViewModel.BuildMetaLine(album));
        Assert.Equal(1, rig.Library.MetadataChangedCount);
    }

    [Fact]
    public async Task TheSettingsRow_ShowsTheCount_AndThePaneOpensOnTheActiveSeparators()
    {
        using var rig = MobileFixtures.MakeRig();
        rig.Shell.OpenSettingsCommand.Execute(null);
        var settings = (SettingsPageViewModel)rig.Shell.CurrentPage!;
        await settings.Loaded;
        Assert.Equal("Genre Separators, 1", settings.GenreSeparatorsRowName);

        // Applied after the sheet was made (the start's read): opening the page shows them.
        GenreSeparators.Configure(new[] { "/", "|" });
        settings.OpenPaneCommand.Execute(SettingsPane.GenreSeparators);
        Assert.Equal("Genre Separators", settings.HeaderTitle);
        Assert.True(settings.ShowsGenreSeparatorsPane);
        Assert.False(settings.ShowsArtistSeparatorsPane);
        Assert.Equal(new[] { "/", "|" }, Rows(settings.GenreSeparators));
        Assert.Equal("Genre Separators, 2", settings.GenreSeparatorsRowName);
    }

    [Fact]
    public void ApplySaved_ConfiguresTheSplitterFromTheSettings()
    {
        GenreSeparatorsViewModel.ApplySaved(new AppSettings { GenreTagSeparators = { "/" } });
        Assert.Equal(new[] { ",", "/" }, GenreSeparators.Configured);
        GenreSeparatorsViewModel.ApplySaved(new AppSettings { GenreTagSeparators = new() });
        Assert.Empty(GenreSeparators.Configured);
    }

    /// <summary>
    /// The phone's start: before this the saved separators were applied only by LibraryService's
    /// background pass, after the library had loaded and raised LibraryUpdated, so whatever read
    /// genres meanwhile (an album page's genre line) split them on the defaults. Applied first
    /// (AndroidApp.StartAsync), the loaded library reads under the saved ones at once.
    /// </summary>
    [Fact]
    public async Task AfterARestart_TheLoadedLibraryReadsGenresUnderTheSavedSeparators()
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
            f.Tag.Album = "Album";
            f.Tag.Performers = new[] { "Artist" };
            f.Tag.Genres = new[] { "Rock, Pop" };
            f.Save();
        }
        var persistence = new PersistenceService(Path.Combine(_root, "data"));
        await persistence.SaveSettingsAsync(new AppSettings { MusicFolders = { music } });
        await persistence.UpdateSettingsAsync(s => s.GenreTagSeparators = new());
        GenreSeparators.Configure(Array.Empty<string>());
        var library = NewLibrary(persistence);
        await library.ScanAsync(new[] { music });
        if (library.AllTracks.Count != 1) Assert.Fail(ScanMissReport.Describe(library, music));
        await library.BackgroundInit;

        // A new process: the splitter starts on the defaults.
        GenreSeparators.ResetToDefaults();
        GenreSeparatorsViewModel.ApplySaved(await persistence.LoadSettingsAsync());
        var reloaded = NewLibrary(persistence);
        await reloaded.LoadAsync();
        Assert.Equal("Rock, Pop", Assert.Single(reloaded.Albums).GenreDisplay);   // not "Rock · Pop"
        await reloaded.BackgroundInit;
    }

    private static LibraryService NewLibrary(PersistenceService persistence) =>
        new(new MetadataService(), persistence, new SqliteLibraryIndexService(persistence), new NoctisAccountLibraryTests.NoOpAudit());

    [AvaloniaFact]
    public async Task ThePane_ListsRemovesAddsAndShowsTheEmptyLine()
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
            Assert.True(MobileFixtures.Named<Button>(page, "GenreSeparatorsRow").IsEffectivelyVisible);
            vm.OpenPaneCommand.Execute(SettingsPane.GenreSeparators);
            MobileFixtures.Land(window);
            Pump(window);
            var pane = MobileFixtures.Named<GenreSeparatorsPane>(page, "GenreSeparatorsPane");
            Assert.True(pane.IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<ArtistSeparatorsPane>(page, "ArtistSeparatorsPane").IsEffectivelyVisible);
            var removes = RemoveButtons(pane);
            Assert.Single(removes);
            Assert.All(removes, b => Assert.True(b.Bounds.Height >= 44));
            Assert.False(MobileFixtures.Named<TextBlock>(pane, "NoGenreSeparatorsLabel").IsEffectivelyVisible);
            Assert.False(MobileFixtures.Named<Button>(pane, "GenreSeparatorsResetButton").IsEnabled);

            removes[0].Command!.Execute(removes[0].CommandParameter);   // ","
            Pump(window);
            Assert.Empty(RemoveButtons(pane));
            Assert.True(MobileFixtures.Named<TextBlock>(pane, "NoGenreSeparatorsLabel").IsEffectivelyVisible);
            Assert.True(MobileFixtures.Named<Button>(pane, "GenreSeparatorsResetButton").IsEnabled);

            MobileFixtures.Named<TextBox>(pane, "NewGenreSeparatorBox").Text = " / ";
            Pump(window);
            var add = MobileFixtures.Named<Button>(pane, "AddGenreSeparatorButton");
            Assert.True(add.IsEffectivelyEnabled);
            add.Command!.Execute(null);
            Pump(window);
            Assert.Equal(string.Empty, MobileFixtures.Named<TextBox>(pane, "NewGenreSeparatorBox").Text ?? string.Empty);
            Assert.Equal("/", ((GenreSeparatorRow)Assert.Single(RemoveButtons(pane)).DataContext!).Text);
            Assert.False(MobileFixtures.Named<TextBlock>(pane, "NoGenreSeparatorsLabel").IsEffectivelyVisible);
            Assert.Equal(new[] { "Hip-Hop", "Rap" }, Track.SplitGenres("Hip-Hop/Rap"));

            MobileFixtures.Named<Button>(pane, "GenreSeparatorsResetButton").Command!.Execute(null);
            Pump(window);
            Assert.Equal(",", ((GenreSeparatorRow)Assert.Single(RemoveButtons(pane)).DataContext!).Text);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }

        static Button[] RemoveButtons(GenreSeparatorsPane p) =>
            p.GetVisualDescendants().OfType<Button>().Where(b => b.DataContext is GenreSeparatorRow && b.IsEffectivelyVisible).ToArray();
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
