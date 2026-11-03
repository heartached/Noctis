using System.Globalization;
using Avalonia;
using Avalonia.Headless.XUnit;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #147: the Queue panel's width (left-edge drag, Settings slider) and what its rows
/// show (Settings ▸ Player ▸ Queue ▸ Queue Rows). The defaults must be the queue as it was.
/// </summary>
public class QueueCustomizeIssue147Tests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", "queue147-" + Guid.NewGuid().ToString("N"));

    public QueueCustomizeIssue147Tests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
    }

    private static Track Song() => new()
    {
        Title = "Pyramids",
        Artist = "Frank Ocean",
        AlbumArtist = "Frank Ocean",
        Album = "channel ORANGE",
        Composer = "Frank Ocean; Om'Mas Keith",
        Genre = "R&B; Soul",
        Year = 2012,
        Duration = TimeSpan.FromSeconds(592),
        Codec = "FLAC",
    };

    [Fact]
    public void DefaultLayout_ShowsTheRowsAsBefore()
    {
        var track = Song();
        var layout = QueueRowLayout.Default;

        Assert.Equal(track.TitleDisplay, layout.LineText(track, 1));
        Assert.Equal(track.ArtistDisplay, layout.LineText(track, 2));
        Assert.True(layout.ShowsPosition);
        Assert.False(layout.ShowsDuration);
        Assert.True(layout.ShowsRight);
        Assert.True(layout.Line2IsArtistLink);
        Assert.False(layout.Line2IsPlain);
        Assert.True(layout.HasLine2);
        Assert.Equal(QueueRowLayout.Default, QueueRowLayout.FromSettings(new AppSettings()));
    }

    [Theory]
    [InlineData(QueueRowField.Title, "Pyramids")]
    [InlineData(QueueRowField.Artist, "Frank Ocean")]
    [InlineData(QueueRowField.Album, "channel ORANGE")]
    [InlineData(QueueRowField.AlbumArtist, "Frank Ocean")]
    [InlineData(QueueRowField.Composer, "Frank Ocean; Om'Mas Keith")]
    [InlineData(QueueRowField.Genre, "R&B · Soul")]
    [InlineData(QueueRowField.Year, "2012")]
    [InlineData(QueueRowField.Duration, "9:52")]
    [InlineData(QueueRowField.Format, "FLAC")]
    [InlineData(QueueRowField.None, "")]
    public void EachField_FormatsFromTheTrack(QueueRowField field, string expected) =>
        Assert.Equal(expected, QueueRowLayout.Format(Song(), field));

    [Fact]
    public void ExtraField_JoinsWithADot_AndBlankPartsDropOut()
    {
        var track = Song();
        var layout = new QueueRowLayout(QueueRowField.Title, QueueRowField.Duration,
            QueueRowField.Album, QueueRowField.Year, QueueRowSide.Duration);
        Assert.Equal("Pyramids · 9:52", layout.LineText(track, 1));
        Assert.Equal("channel ORANGE · 2012", layout.LineText(track, 2));
        Assert.True(layout.Line2IsPlain);
        Assert.False(layout.Line2IsArtistLink);
        Assert.True(layout.ShowsDuration);
        Assert.False(layout.ShowsPosition);

        // No year, no genre: the empty parts leave no stray dot.
        var bare = new Track { Title = "Untitled", Artist = "Someone" };
        var sparse = new QueueRowLayout(QueueRowField.Title, QueueRowField.Year,
            QueueRowField.Genre, QueueRowField.Artist, QueueRowSide.None);
        Assert.Equal("Untitled", sparse.LineText(bare, 1));
        Assert.Equal("Someone", sparse.LineText(bare, 2));
        Assert.False(sparse.ShowsRight);
    }

    [Fact]
    public void Year_FollowsTheReleaseDateLikeTheRestOfTheApp()
    {
        var track = new Track { Year = 2025, ReleaseDate = "2024-11-01" };
        Assert.Equal("2024", QueueRowLayout.Format(track, QueueRowField.Year));
    }

    [Fact]
    public void SecondLineNone_HidesIt()
    {
        var layout = QueueRowLayout.Default with { Line2 = QueueRowField.None };
        Assert.False(layout.HasLine2);
        Assert.False(layout.Line2IsPlain);
        Assert.False(layout.Line2IsArtistLink);
        Assert.Equal(string.Empty, layout.LineText(Song(), 2));
    }

    [Fact]
    public void StoredValues_UnknownReadAsDefaults_AndLine1IsNeverEmpty()
    {
        var layout = QueueRowLayout.From("Bogus", "nope", null, "", "Sideways");
        Assert.Equal(QueueRowLayout.Default, layout);

        Assert.Equal(QueueRowField.Title, QueueRowLayout.From("None", null, null, null, null).Line1);
        Assert.Equal(QueueRowField.Album, QueueRowLayout.From("album", null, null, null, null).Line1);
        Assert.Equal(QueueRowSide.Duration, QueueRowLayout.From(null, null, null, null, "duration").Right);
        Assert.DoesNotContain(QueueRowField.None, QueueRowLayout.Line1Choices);
        Assert.Equal(QueueRowField.None, QueueRowLayout.OptionalChoices[0]);
    }

    [Theory]
    [InlineData(330, 330)]
    [InlineData(100, 300)]
    [InlineData(5000, 720)]
    [InlineData(412.6, 413)]
    [InlineData(double.NaN, 330)]
    [InlineData(double.PositiveInfinity, 330)]
    public void Width_IsClamped(double stored, double expected)
    {
        Assert.Equal(expected, QueuePanelMetrics.Clamp(stored));
        var settings = new AppSettings { QueuePanelWidth = stored };
        settings.ClampToValidRanges();
        Assert.Equal(expected, settings.QueuePanelWidth);
    }

    [Fact]
    public void TitleMaxWidth_GrowsWithThePanel_165AtTheStockWidth()
    {
        Assert.Equal(165, QueuePanelMetrics.TitleMaxWidth(330));
        Assert.Equal(265, QueuePanelMetrics.TitleMaxWidth(430));
        Assert.Equal(135, QueuePanelMetrics.TitleMaxWidth(300));
        Assert.Equal(165, QueuePanelMetrics.TitleMaxWidth(double.NaN));
    }

    [Fact]
    public void LineConverter_UsesTheLayout_AndFallsBackToTheDefault()
    {
        var track = Song();
        var album = QueueRowLayout.Default with { Line1 = QueueRowField.Album };
        var c = CultureInfo.InvariantCulture;

        Assert.Equal("channel ORANGE", QueueRowConverters.Line1.Convert(new List<object?> { track, album }, typeof(string), null, c));
        Assert.Equal("Frank Ocean", QueueRowConverters.Line2.Convert(new List<object?> { track, album }, typeof(string), null, c));
        // The layout binding not resolved yet (row outside the window): the stock rows.
        Assert.Equal("Pyramids", QueueRowConverters.Line1.Convert(new List<object?> { track, AvaloniaProperty.UnsetValue }, typeof(string), null, c));
        // The drag preview starts with no track.
        Assert.Equal(string.Empty, QueueRowConverters.Line1.Convert(new List<object?> { null, album }, typeof(string), null, c));
    }

    private SettingsViewModel NewVm() => new(new PersistenceService(_root), new FakeLibraryService(), new FakeHistoryLog());

    private static PlayerViewModel NewPlayer() =>
        new(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());

    [AvaloniaFact]
    public async Task Choices_SurviveSaveAndReload_ReachThePlayer_AndResetPutsThemBack()
    {
        var vm = NewVm();
        await vm.LoadAsync();
        var player = NewPlayer();
        vm.SetPlayer(player);
        player.SetSettingsViewModel(vm);

        Assert.Equal("Title", vm.SelectedQueueLine1Option?.Value);
        Assert.Equal("None", vm.SelectedQueueLine1ExtraOption?.Value);
        Assert.Equal("Artist", vm.SelectedQueueLine2Option?.Value);
        Assert.Equal("None", vm.SelectedQueueLine2ExtraOption?.Value);
        Assert.Equal("Position", vm.SelectedQueueRightOption?.Value);
        Assert.Equal(QueueRowLayout.Default, player.QueueRowLayout);
        Assert.Equal(330, player.QueuePanelWidth);
        Assert.Equal(Loc.T("Settings.QueueField.AlbumArtist"),
            vm.QueueOptionalFieldOptions.Single(o => o.Value == "AlbumArtist").Label);

        vm.SelectedQueueLine1Option = vm.QueueLine1Options.Single(o => o.Value == "Title");
        vm.SelectedQueueLine1ExtraOption = vm.QueueOptionalFieldOptions.Single(o => o.Value == "Year");
        vm.SelectedQueueLine2Option = vm.QueueOptionalFieldOptions.Single(o => o.Value == "Album");
        vm.SelectedQueueLine2ExtraOption = vm.QueueOptionalFieldOptions.Single(o => o.Value == "Genre");
        vm.SelectedQueueRightOption = vm.QueueRightOptions.Single(o => o.Value == "Duration");
        var custom = new QueueRowLayout(QueueRowField.Title, QueueRowField.Year,
            QueueRowField.Album, QueueRowField.Genre, QueueRowSide.Duration);
        Assert.Equal(custom, player.QueueRowLayout);

        // The panel's edge drag commits through the player; the slider shows it too.
        player.CommitQueuePanelWidth(455.4);
        Assert.Equal(455, player.QueuePanelWidth);
        Assert.Equal(455, vm.QueuePanelWidth);
        Assert.Equal(455 - 330 + 165, player.QueueTitleMaxWidth);
        vm.QueuePanelWidth = 9999; // slider / hand edit: clamped
        Assert.Equal(720, vm.QueuePanelWidth);
        Assert.Equal(720, player.QueuePanelWidth);
        vm.QueuePanelWidth = 455;
        await vm.SaveAsync();

        var reloaded = NewVm();
        await reloaded.LoadAsync();
        Assert.Equal(custom, reloaded.CurrentQueueRowLayout);
        Assert.Equal("Duration", reloaded.SelectedQueueRightOption?.Value);
        Assert.Equal(455, reloaded.QueuePanelWidth);
        Assert.Equal("Genre", reloaded.GetSettings().QueueRowLine2Extra);

        var reloadedPlayer = NewPlayer();
        reloaded.SetPlayer(reloadedPlayer);
        Assert.Equal(custom, reloadedPlayer.QueueRowLayout);
        Assert.Equal(455, reloadedPlayer.QueuePanelWidth);

        reloaded.ResetSettingsToDefaults(new AppSettings());
        Assert.Equal(QueueRowLayout.Default, reloaded.CurrentQueueRowLayout);
        Assert.Equal(330, reloaded.QueuePanelWidth);
        Assert.Equal(QueueRowLayout.Default, reloadedPlayer.QueueRowLayout);
        Assert.Equal(330, reloadedPlayer.QueuePanelWidth);
    }

    [AvaloniaFact]
    public async Task HandEditedValues_ReadAsDefaultsOrClamped()
    {
        var persistence = new PersistenceService(_root);
        await persistence.SaveSettingsAsync(new AppSettings
        {
            QueueRowLine1 = "None",
            QueueRowLine2 = "Mood",
            QueueRowRight = "Stars",
            QueuePanelWidth = 50,
        });

        var vm = NewVm();
        await vm.LoadAsync();

        Assert.Equal(QueueRowLayout.Default, vm.CurrentQueueRowLayout);
        Assert.Equal("Title", vm.SelectedQueueLine1Option?.Value);
        Assert.Equal(300, vm.QueuePanelWidth);
    }
}
