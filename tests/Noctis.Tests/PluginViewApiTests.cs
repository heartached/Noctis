using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Models;
using Noctis.Plugins;
using Noctis.Services.Plugins;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Plugin API 1.3: views of a plugin's own (RegisterView/OpenView, the Settings "Open" button,
/// guarded CreateView, withdrawn on stop), ShowAlbum, PlayTracks and the new TrackInfo fields.
/// </summary>
public class PluginViewApiTests : IDisposable
{
    private readonly PluginSandbox _box = new();

    public void Dispose() => _box.Dispose();

    private const string Id = "dev.test.views";

    private static string M(params string[] permissions) => PluginSandbox.Manifest(id: Id, apiVersion: "1.3", permissions: permissions);

    private sealed class View : IPluginViewProvider
    {
        public string Name { get; set; } = "Town";
        public Func<Control?> Create = () => new Border();
        public int Created;
        public Control CreateView() { Created++; return Create()!; }
    }

    private (ScriptedPlugin Script, LoadedPlugin Plugin, PluginHost Host) Start(Action<IPluginHost> init, PlayerViewModel? player = null,
        FakeLibraryService? library = null, params string[] permissions)
    {
        _box.Approve((Id, permissions));
        var host = _box.NewHost(player, library);
        var script = new ScriptedPlugin { OnInit = init };
        var plugin = _box.AddInProcess(host, script, M(permissions));
        Assert.Equal(PluginStatus.Running, plugin.Status);
        return (script, plugin, host);
    }

    [Fact]
    public void Kit_Is13()
    {
        Assert.True(PluginApi.Minor >= 3);
        Assert.Null(PluginManifest.Parse(M()).CheckCompatibility("1.6.6"));
    }

    [Fact]
    public void TrackInfo_Carries13Fields()
    {
        var added = new DateTime(2024, 5, 1, 12, 0, 0, DateTimeKind.Utc);
        var played = new DateTime(2026, 10, 1, 8, 30, 0, DateTimeKind.Utc);
        var track = new Track
        {
            Title = "One", AlbumId = Guid.NewGuid(), AlbumArtworkPath = "C:/art/a.jpg", ReleaseType = ReleaseType.Single,
            DateAdded = added, LastPlayed = played,
        };
        var info = PluginHost.ToTrackInfo(track);
        Assert.Equal(track.AlbumId.ToString("D"), info.AlbumId);
        Assert.Equal("C:/art/a.jpg", info.ArtworkPath);
        Assert.Equal("Single", info.ReleaseType);
        Assert.Equal(new DateTimeOffset(added), info.DateAdded);
        Assert.Equal(new DateTimeOffset(played), info.LastPlayed);

        var bare = PluginHost.ToTrackInfo(new Track { Title = "Two", AlbumId = Guid.Empty });
        Assert.Equal("", bare.AlbumId);
        Assert.Null(bare.ArtworkPath);
        Assert.Null(bare.LastPlayed);
        Assert.Equal("Album", bare.ReleaseType);
    }

    [AvaloniaFact]
    public void RegisterView_ShowsAnOpenButton_AndOpenHandsTheControlToThePresenter()
    {
        var view = new View();
        var (script, plugin, host) = Start(h => h.RegisterView(view));
        var shown = new List<(PluginViewEntry Entry, Control Control)>();
        host.ViewPresenter = (e, c) => { shown.Add((e, c)); return true; };

        var entry = Assert.Single(plugin.Views);
        Assert.True(plugin.HasViews);
        Assert.Same(entry, Assert.Single(host.Views));
        Assert.Equal("Open Town", entry.OpenText);
        Assert.Contains("View: Town", plugin.Extensions);
        Assert.Equal(0, view.Created); // nothing is built until the user opens it

        Assert.True(entry.Open());
        Assert.Equal(1, view.Created);
        Assert.Same(entry, Assert.Single(shown).Entry);

        // While its window is open, opening again brings that window forward: no second control.
        var reactivated = 0;
        entry.Reactivate = () => { reactivated++; return true; };
        Assert.True(script.Host!.OpenView(view));
        Assert.Equal((1, 1), (view.Created, reactivated));
        Assert.False(script.Host.OpenView(new View())); // not registered
    }

    [AvaloniaFact]
    public void StoppingThePlugin_WithdrawsItsViews_SoWindowsClose()
    {
        var view = new View();
        var (_, plugin, host) = Start(h => h.RegisterView(view));
        var entry = plugin.Views.Single();
        var withdrawn = 0;
        entry.Withdrawn += (_, _) => withdrawn++;
        host.ViewPresenter = (_, _) => true;

        host.SetEnabled(plugin, false);

        Assert.Equal(1, withdrawn);
        Assert.True(entry.IsWithdrawn);
        Assert.Empty(plugin.Views);
        Assert.False(plugin.HasViews);
        Assert.Empty(host.Views);
        Assert.False(entry.Open());
        Assert.Equal(0, view.Created);
    }

    [AvaloniaFact]
    public void DisposingTheRegistration_RemovesTheView()
    {
        IDisposable? registration = null;
        var (_, plugin, _) = Start(h => registration = h.RegisterView(new View()));
        var entry = plugin.Views.Single();
        registration!.Dispose();
        Assert.Empty(plugin.Views);
        Assert.True(entry.IsWithdrawn);
        Assert.DoesNotContain("View:", plugin.Extensions);
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void CreateView_ThatThrowsOrReturnsNull_FailsThePlugin_NotNoctis(bool throws)
    {
        var view = new View { Create = throws ? () => throw new InvalidOperationException("boom") : () => null };
        var (_, plugin, host) = Start(h => h.RegisterView(view));
        var shown = 0;
        host.ViewPresenter = (_, _) => { shown++; return true; };

        Assert.False(plugin.Views.Single().Open());

        Assert.Equal(0, shown);
        Assert.Equal(PluginStatus.Failed, plugin.Status);
        Assert.Contains(throws ? "boom" : "returned null", plugin.Error);
    }

    [AvaloniaFact]
    public void OpenView_WithoutAPresenter_IsFalse()
    {
        var view = new View();
        var (script, _, _) = Start(h => h.RegisterView(view));
        Assert.False(script.Host!.OpenView(view));
        Assert.Equal(0, view.Created);
    }

    [AvaloniaFact]
    public void ShowAlbum_GoesThroughTheAppsAlbumOpener()
    {
        var (script, _, host) = Start(_ => { });
        var opened = new List<Guid>();
        Assert.False(script.Host!.ShowAlbum(Guid.NewGuid().ToString("D"))); // no opener (tests, headless)
        host.AlbumOpener = id => { opened.Add(id); return true; };
        var album = Guid.NewGuid();

        Assert.True(script.Host.ShowAlbum(album.ToString("D")));
        Assert.False(script.Host.ShowAlbum("not a guid"));
        Assert.Equal(new[] { album }, opened);
    }

    [AvaloniaFact]
    public void PlayTracks_NeedsPlaybackControl_ThenQueuesInOrder_SkippingUnknownIds()
    {
        var audio = new FakeAudioPlayer();
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(audio, library, new TestPersistenceService(), new FakeAnimatedCoverService());
        var tracks = Enumerable.Range(0, 3).Select(i => new Track
        {
            Title = "t" + i, Artist = "A", Album = "B", Duration = TimeSpan.FromSeconds(100),
            FilePath = Path.Combine(Path.GetTempPath(), $"t{i}.mp3"),
        }).ToList();
        library.TrackList.AddRange(tracks);

        using (var other = new PluginSandbox())
        {
            other.Approve((Id, Array.Empty<string>()));
            var bare = new ScriptedPlugin();
            other.AddInProcess(other.NewHost(player, library), bare, M());
            Assert.Throws<PluginPermissionException>(() => bare.Host!.Playback);
        }

        var (script, _, _) = Start(_ => { }, player, library, "playback.control");
        var ids = new[] { tracks[0].Id.ToString("D"), "missing", tracks[2].Id.ToString("D") };

        script.Host!.Playback.PlayTracks(ids);
        Assert.Same(tracks[0], player.CurrentTrack);
        Assert.Equal(new[] { tracks[2] }, player.UpNext.ToArray());

        script.Host.Playback.PlayTracks(ids, startIndex: 2);
        Assert.Same(tracks[2], player.CurrentTrack);
    }
}
