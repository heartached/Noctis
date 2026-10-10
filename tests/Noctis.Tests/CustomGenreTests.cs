using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #123 (LuigiGame3, 2026-10-10): "Type in new genres for genre list". The metadata
/// editor's genre box was a read-only ComboBox over a fixed built-in list, so a genre outside
/// it could not be entered. It is editable now: typed text saves to the tag like a pick.
/// </summary>
public class CustomGenreTests : IDisposable
{
    private readonly ITestOutputHelper _o;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public CustomGenreTests(ITestOutputHelper o)
    {
        _o = o;
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The real save path: typed text lands in the file's genre tag, trimmed.</summary>
    [Fact]
    public async Task TypedGenre_IsWrittenToTheFileTag()
    {
        var path = ReplayGainWriteTests.CreateFlac(_dir);
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "Song", Artist = "A", AlbumArtist = "A", Album = "B",
            TrackNumber = 1, Genre = "Pop", FilePath = path,
        };
        track.AlbumId = Track.ComputeAlbumId(track.AlbumArtist, track.Album);
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        using var p = new TestPersistenceService();
        var vm = new MetadataViewModel(track, new MetadataService(), lib, p, new FakeAnimatedCoverService());
        await vm.InitializeAsync();

        vm.Genre = "  Dungeon Synth ";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.Equal(string.Empty, vm.SaveErrorMessage);
        Assert.Equal("Dungeon Synth", track.Genre);
        using var f = TagLib.File.Create(path);
        Assert.Equal(new[] { "Dungeon Synth" }, f.Tag.Genres);
    }

    /// <summary>The window: the genre box takes typed text into Genre, a pick from the list
    /// still sets it, and the list offers the library's genres.</summary>
    [AvaloniaFact]
    public void GenreBox_TypingAndPicking_BothSetTheGenre()
    {
        EnsureAppStyles();
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "monaco", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny", Album = "nadie sabe",
            AlbumId = Guid.NewGuid(), TrackNumber = 2, Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(267),
            FilePath = "C:/m/does-not-exist/monaco.flac",
        };
        var other = new Track { Id = Guid.NewGuid(), Title = "x", Genre = "Shoegaze", FilePath = "C:/m/does-not-exist/x.flac" };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        lib.TrackList.Add(other);
        var vm = new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), metadataSearch: new MetadataSearchPanelTests.FakeSearch());
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        try
        {
            var load = vm.InitializeAsync();
            Pump(300);
            Assert.True(load.IsCompleted);

            var combo = win.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "GenreCombo");
            Assert.True(combo.IsEditable);
            Assert.Equal("Latin", combo.Text);
            Assert.Contains("Shoegaze", combo.Items.OfType<string>());

            // Type a genre that is in no list.
            var box = combo.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PART_EditableTextBox");
            Assert.True(box.IsEffectivelyVisible);
            box.Focus();
            box.SelectAll();
            Pump(30);
            win.KeyTextInput("Vaporwave");
            Pump(60);
            _o.WriteLine($"typed: Text='{combo.Text}' Genre='{vm.Genre}' SelectedItem='{combo.SelectedItem}'");
            Assert.Equal("Vaporwave", vm.Genre);
            Assert.Null(combo.SelectedItem);

            // Pick one from the drop-down.
            combo.SelectedItem = "Shoegaze";
            Pump(60);
            _o.WriteLine($"picked: Text='{combo.Text}' Genre='{vm.Genre}'");
            Assert.Equal("Shoegaze", vm.Genre);

            // A value set by the VM (a metadata search apply) shows as the box's text.
            vm.Genre = "Rap/Hip Hop";
            Pump(30);
            Assert.Equal("Rap/Hip Hop", combo.Text);
            Assert.Equal("Rap/Hip Hop", box.Text);

            var border = box.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Name == "PART_BorderElement");
            _o.WriteLine($"inner box: bg={box.Background} border={box.BorderThickness}/{box.BorderBrush} chrome={border?.Background}/{border?.BorderThickness}");
        }
        finally { win.Close(); }
    }
}
