using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Convert Tracks Format / Quality drop-downs showed plain square highlight
/// rows; they should be "rounded pill just like the media kind" list in the metadata editor.
/// The inset pill item style lived only in MetadataWindow.axaml, so every other pill-field
/// ComboBox (converter, smart playlist, lyrics search) fell back to Fluent's square rows. It
/// now ships with the pill-field ComboBox in PillDialog.axaml.
/// </summary>
public class PillComboItemTests
{
    private readonly ITestOutputHelper _o;
    public PillComboItemTests(ITestOutputHelper o) => _o = o;

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

    private static Track[] Tracks() => new[]
    {
        new Track
        {
            Id = Guid.NewGuid(), Title = "Song", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "Album", TrackNumber = 1, FilePath = TestPaths.Primary("Music", "song1.flac"),
        },
    };

    /// <summary>Opens <paramref name="combo"/> and checks every item is an inset, fully rounded
    /// pill and the list sits inside a rounded popup with room around the pills.</summary>
    private void AssertPillList(ComboBox combo)
    {
        combo.IsDropDownOpen = true;
        Pump(60);
        Assert.True(combo.IsDropDownOpen);
        Assert.True(combo.ItemCount > 1);

        // The list lives in the template's Popup, whose child is not under the ComboBox visually.
        var popup = combo.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.Popup>().First();
        var popupBorder = Assert.IsType<Border>(popup.Child);
        Assert.Equal("PopupBorder", popupBorder.Name);
        Assert.Equal(new CornerRadius(18), popupBorder.CornerRadius);
        var presenter = popupBorder.GetVisualDescendants().OfType<ItemsPresenter>().First();
        _o.WriteLine($"PopupBorder padding {popupBorder.Padding}, items presenter margin {presenter.Margin}");

        // Long lists (genres) virtualize: check every row that was realized.
        var realized = combo.GetRealizedContainers().ToList();
        Assert.NotEmpty(realized);
        foreach (var container in realized)
        {
            var item = Assert.IsType<ComboBoxItem>(container);
            Assert.Equal(new CornerRadius(999), item.CornerRadius);
            Assert.Equal(new Thickness(4, 1), item.Margin);
            Assert.Equal(new Thickness(12, 6), item.Padding);
            Assert.Equal(30, item.MinHeight);
            var chrome = item.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
        }

        // The first pill clears the popup's rounded rim: its top sits at least 4px inside the
        // popup and its sides at least 4px in, so the 18px corners never clip a 15px pill cap.
        // A long list opens scrolled to its selection (genres): then only the side inset applies.
        var first = realized.OrderBy(c => combo.IndexFromContainer(c)).First();
        var at = first.TranslatePoint(new Point(0, 0), popupBorder);
        Assert.NotNull(at);
        _o.WriteLine($"first realized item #{combo.IndexFromContainer(first)} at {at} in popup {popupBorder.Bounds.Size}, item {first.Bounds.Size}");
        if (combo.IndexFromContainer(first) == 0 && at!.Value.Y < popupBorder.Bounds.Height / 2)
            Assert.True(at.Value.Y >= 4, $"first pill {at.Value.Y}px from the popup's top");
        Assert.True(at!.Value.X >= 4, $"pill {at.Value.X}px from the popup's side");

        combo.IsDropDownOpen = false;
        Pump(30);
    }

    [AvaloniaFact]
    public void ConverterDropDowns_AreInsetRoundedPills()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new AudioConverterViewModel(Tracks(), new FakeConverterForCombos(), new FakeLibraryService());
            var win = new AudioConverterDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            win.Show();
            try
            {
                Pump(400);
                var combos = win.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible).ToList();
                Assert.Equal(2, combos.Count); // Format + Quality (bitrate for MP3)
                foreach (var combo in combos) AssertPillList(combo);
            }
            finally { win.Close(); }
        });
    }

    /// <summary>The metadata editor lost its local copy of the item style: every drop-down on
    /// every tab (media kind, release type, genre, advisory, EQ preset) still gets the pills.</summary>
    [AvaloniaFact]
    public void MetadataEditorDropDowns_KeepTheirPills_FromTheSharedStyle()
    {
        EnsureAppStyles();
        var track = new Track
        {
            Id = Guid.NewGuid(), Title = "monaco", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny", Album = "nadie sabe",
            AlbumId = Guid.NewGuid(), TrackNumber = 2, Genre = "Latin", Year = 2023, Duration = TimeSpan.FromSeconds(267),
            FilePath = "C:/m/does-not-exist/monaco.flac",
        };
        var lib = new FakeLibraryService();
        lib.TrackList.Add(track);
        var vm = new MetadataViewModel(track, new SearchPopupsShotsTests.OkTags(), lib, new TestPersistenceService(),
            new FakeAnimatedCoverService(), metadataSearch: new MetadataSearchPanelTests.FakeSearch());
        var win = new MetadataWindow(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
        win.Show();
        try
        {
            var load = vm.InitializeAsync();
            Pump(300);
            Assert.True(load.IsCompleted);
            var tabs = win.GetVisualDescendants().OfType<TabControl>().Single();
            var checkedCombos = 0;
            foreach (var tab in tabs.Items.OfType<TabItem>().ToList())
            {
                tabs.SelectedItem = tab;
                Pump(60);
                foreach (var combo in win.GetVisualDescendants().OfType<ComboBox>().Where(c => c.IsEffectivelyVisible && c.ItemCount > 1).ToList())
                {
                    Assert.Contains("pill-field", combo.Classes);
                    AssertPillList(combo);
                    checkedCombos++;
                }
            }
            _o.WriteLine($"checked {checkedCombos} drop-downs");
            Assert.True(checkedCombos >= 3, $"only {checkedCombos} drop-downs found");
        }
        finally { win.Close(); }
    }

    /// <summary>The shared style is scoped to pill-field: a plain ComboBox beside one keeps
    /// Fluent's rows, and the selected row still gets a visible fill in both themes.</summary>
    [AvaloniaTheory]
    [InlineData("Light")]
    [InlineData("Dark")]
    public void SharedStyle_IsScopedToPillField_AndSelectedRowStillReads(string theme)
    {
        EnsureAppStyles();
        var variant = theme == "Light" ? ThemeVariant.Light : ThemeVariant.Dark;
        var pill = new ComboBox { ItemsSource = new[] { "Normal", "Audiobook", "Podcast" }, SelectedIndex = 1 };
        pill.Classes.Add("pill-field");
        var plain = new ComboBox { ItemsSource = new[] { "One", "Two" }, SelectedIndex = 0 };
        var host = new PillDialogHost { Content = new StackPanel { Width = 300, Children = { pill, plain } } };
        var win = new Window { RequestedThemeVariant = variant, Width = 600, Height = 500, Content = host };
        win.Show();
        try
        {
            Pump(100);
            AssertPillList(pill);

            plain.IsDropDownOpen = true;
            Pump(60);
            var row = Assert.IsType<ComboBoxItem>(plain.ContainerFromIndex(0));
            Assert.NotEqual(new CornerRadius(999), row.CornerRadius);
            plain.IsDropDownOpen = false;

            pill.IsDropDownOpen = true;
            Pump(60);
            var selected = (ComboBoxItem)pill.ContainerFromIndex(1)!;
            Assert.True(selected.IsSelected);
            var chrome = selected.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
            var fill = Assert.IsAssignableFrom<ISolidColorBrush>(chrome.Background);
            Assert.True(fill.Color.A * fill.Opacity > 20, $"{theme}: selected row fill {fill.Color} @ {fill.Opacity}");
            pill.IsDropDownOpen = false;
        }
        finally { win.Close(); }
    }

    private sealed class FakeConverterForCombos : Noctis.Services.IAudioConverterService
    {
        public string? GetFfmpegPath() => "ffmpeg";
        public System.Threading.Tasks.Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult<string?>(null);
        public System.Threading.Tasks.Task<Noctis.Services.ConvertSummary> ConvertAsync(
            System.Collections.Generic.IReadOnlyList<Track> tracks, Noctis.Services.AudioConvertOptions options,
            IProgress<Noctis.Services.ConvertProgress> progress, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new Noctis.Services.ConvertSummary());
    }
}
