using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// "Snooze for a Month" on album and artist menus: the track menu's row applied to every track
/// of an album, or to every track an artist page plays (releases plus songs credited to the name,
/// featured credits included). All of them go through ILibraryService.SetTracksSnoozedAsync with
/// the same 30-day window as a single track.
/// </summary>
public class SnoozeAlbumArtistTests
{
    private static Album MakeAlbum(string name, string artist, params (string title, string trackArtist)[] tracks)
    {
        var id = Guid.NewGuid();
        var album = new Album { Id = id, Name = name, Artist = artist, Year = 2020, Tracks = new List<Track>() };
        var n = 1;
        foreach (var (title, trackArtist) in tracks)
        {
            album.Tracks.Add(new Track
            {
                Id = Guid.NewGuid(), Title = title, Artist = trackArtist, AlbumArtist = artist, Album = name,
                AlbumId = id, TrackNumber = n++, DiscNumber = 1, Year = 2020, Duration = TimeSpan.FromMinutes(3),
            });
        }
        album.TrackCount = album.Tracks.Count;
        return album;
    }

    /// <summary>
    /// "Target" releases two tracks; "Other feat. Target" is featured on a compilation; "Other"
    /// has an unrelated album and a solo track on the compilation. Expected = Target's releases
    /// plus the featured track; the other two tracks must not be snoozed.
    /// </summary>
    private static (FakeLibraryService Lib, List<Track> Expected, List<Track> Others) ArtistLibrary()
    {
        var release = MakeAlbum("Target Album", "Target", ("r1", "Target"), ("r2", "Target"));
        var compilation = MakeAlbum("Compilation", "Various", ("f1", "Other feat. Target"), ("o1", "Other"));
        var unrelated = MakeAlbum("Other Album", "Other", ("o2", "Other"));

        var lib = new FakeLibraryService();
        ((List<Album>)lib.Albums).AddRange(new[] { release, compilation, unrelated });
        foreach (var a in new[] { release, compilation, unrelated }) lib.TrackList.AddRange(a.Tracks);

        var expected = release.Tracks.Append(compilation.Tracks[0]).ToList();
        var others = new List<Track> { compilation.Tracks[1], unrelated.Tracks[0] };
        return (lib, expected, others);
    }

    private static PlayerViewModel MakePlayer(ILibraryService lib) =>
        new(new FakeAudioPlayer(), lib, new TestPersistenceService(), new FakeAnimatedCoverService());

    private static List<Guid> Sorted(IEnumerable<Track> tracks) => tracks.Select(t => t.Id).OrderBy(id => id).ToList();

    private static void AssertMonthFromNow(DateTime? until, DateTime before, DateTime after)
    {
        Assert.NotNull(until);
        Assert.InRange(until!.Value, before.AddDays(30), after.AddDays(30));
    }

    [Fact]
    public void AlbumSnooze_SnoozesExactlyThatAlbumsTracks()
    {
        var lib = new FakeLibraryService();
        var first = MakeAlbum("First", "Target", ("a", "Target"), ("b", "Target"));
        var second = MakeAlbum("Second", "Target", ("c", "Target"));
        ((List<Album>)lib.Albums).AddRange(new[] { first, second });
        lib.TrackList.AddRange(first.Tracks);
        lib.TrackList.AddRange(second.Tracks);
        var player = MakePlayer(lib);

        var before = DateTime.UtcNow;
        player.SnoozeAlbumForMonthCommand.Execute(first);
        var after = DateTime.UtcNow;

        var call = Assert.Single(lib.SnoozeCalls);
        Assert.Equal(first.Tracks.Select(t => t.Id), call.Tracks.Select(t => t.Id));
        AssertMonthFromNow(call.Until, before, after);
    }

    [Fact]
    public void AlbumSnooze_EmptyAlbum_CallsNothing()
    {
        var lib = new FakeLibraryService();
        var player = MakePlayer(lib);

        player.SnoozeAlbumForMonthCommand.Execute(MakeAlbum("Empty", "Target"));

        Assert.Empty(lib.SnoozeCalls);
    }

    [Fact]
    public void ArtistSnooze_FromArtistPage_SnoozesEveryTrackThePagePlays()
    {
        var (lib, expected, others) = ArtistLibrary();
        var vm = new ArtistDetailViewModel("Target", lib, MakePlayer(lib));

        var before = DateTime.UtcNow;
        vm.SnoozeArtistForMonthCommand.Execute(null);
        var after = DateTime.UtcNow;

        var call = Assert.Single(lib.SnoozeCalls);
        Assert.Equal(Sorted(expected), Sorted(call.Tracks));
        Assert.DoesNotContain(call.Tracks, t => others.Any(o => o.Id == t.Id));
        AssertMonthFromNow(call.Until, before, after);
    }

    [Fact]
    public void ArtistSnooze_FromArtistsTile_SnoozesTheSameTracks()
    {
        var (lib, expected, others) = ArtistLibrary();
        var vm = new LibraryArtistsViewModel(lib);

        vm.SnoozeArtistForMonthCommand.Execute(new Artist { Name = "Target" });

        var call = Assert.Single(lib.SnoozeCalls);
        Assert.Equal(Sorted(expected), Sorted(call.Tracks));
        Assert.DoesNotContain(call.Tracks, t => others.Any(o => o.Id == t.Id));
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
    }

    private static void BindAlbum(AlbumContextMenuBuilder b, Album album, ICommand? snooze)
    {
        var noop = new RelayCommand(() => { });
        b.Bind(album, noop, noop, noop, noop, noop, noop, noop, noop, noop, snoozeCommand: snooze);
    }

    [AvaloniaFact]
    public void AlbumMenu_SnoozeRow_IsBoundToTheAlbum_AndHiddenWhenUnwired()
    {
        EnsureAppStyles();
        var album = MakeAlbum("First", "Target", ("a", "Target"));
        var host = new Border();
        var b = new AlbumContextMenuBuilder();
        b.Build("Remove from Library", host, v2: true);

        BindAlbum(b, album, snooze: null);
        Assert.False(b.SnoozeForMonth.IsVisible);

        var ran = new List<Album?>();
        var snooze = new RelayCommand<Album?>(a => ran.Add(a));
        BindAlbum(b, album, snooze);
        Assert.True(b.SnoozeForMonth.IsVisible);
        Assert.Contains(b.Menu.Items.Cast<object>(), i => ReferenceEquals(i, b.SnoozeForMonth));
        Assert.Same(snooze, b.SnoozeForMonth.Command);
        Assert.Same(album, b.SnoozeForMonth.CommandParameter);

        b.SnoozeForMonth.Command!.Execute(b.SnoozeForMonth.CommandParameter);
        Assert.Same(album, Assert.Single(ran));
    }

    [AvaloniaFact]
    public void ArtistTileMenu_SnoozeRow_IsBoundToTheTileArtist()
    {
        EnsureAppStyles();
        var noop = new RelayCommand<Artist>(_ => { });
        var ran = new List<Artist?>();
        var snooze = new RelayCommand<Artist>(a => ran.Add(a));
        var menu = new LibraryArtistsView.ArtistTileMenu(new Border(), noop, noop, noop, noop, snooze);
        var artist = new Artist { Name = "Target" };

        menu.Bind(artist);

        Assert.True(menu.SnoozeForMonth.IsVisible);
        Assert.Same(snooze, menu.SnoozeForMonth.Command);
        Assert.Same(artist, menu.SnoozeForMonth.CommandParameter);
        menu.SnoozeForMonth.Command!.Execute(menu.SnoozeForMonth.CommandParameter);
        Assert.Same(artist, Assert.Single(ran));
    }

    [AvaloniaFact]
    public void ArtistHeaderMenu_SnoozeRow_IsBound_AndSnoozesThePageTracks()
    {
        EnsureAppStyles();
        var (lib, expected, _) = ArtistLibrary();
        var vm = new ArtistDetailViewModel("Target", lib, MakePlayer(lib));
        var view = new ArtistDetailView { DataContext = vm };
        var win = new Window { Width = 1280, Height = 900, Content = view };
        win.Show();
        Dispatcher.UIThread.RunJobs();

        // The "..." flyout's row binds its command when the flyout opens, so open it first.
        var owner = view.GetVisualDescendants().OfType<Button>()
            .Single(b => b.Flyout is MenuFlyout f && f.Items.OfType<MenuItem>().Any(m => Equals(m.Header, Loc.T("Menu.Snooze"))));
        var flyout = (MenuFlyout)owner.Flyout!;
        flyout.ShowAt(owner);
        Dispatcher.UIThread.RunJobs();

        var item = flyout.Items.OfType<MenuItem>().Single(m => Equals(m.Header, Loc.T("Menu.Snooze")));
        Assert.Same(vm.SnoozeArtistForMonthCommand, item.Command);
        item.Command!.Execute(item.CommandParameter);
        flyout.Hide();

        var call = Assert.Single(lib.SnoozeCalls);
        Assert.Equal(Sorted(expected), Sorted(call.Tracks));
        win.Close();
    }
}
