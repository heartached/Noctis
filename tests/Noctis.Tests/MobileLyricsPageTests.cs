using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.Presenters;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone lyrics page mounts, shows the layers behind their toggles, scales with
/// the system font, and anchors the active line.</summary>
public class MobileLyricsPageTests
{
    private sealed class NoPicker : IFolderPicker
    {
        public Task<string?> PickFolderAsync() => Task.FromResult<string?>(null);
    }

    private static (ShellViewModel Shell, FakeAudioPlayer Player, FakeTrackFiles Files, string Root) MakeShell()
    {
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var library = new FakeLibraryService();
        var persistence = new PersistenceService(root);
        var player = new FakeAudioPlayer();
        var files = new FakeTrackFiles();
        var nowPlaying = new NowPlayingViewModel(player, library, persistence, marshal: a => a());
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new NoPicker(), marshal: a => a()),
            nowPlaying,
            new LyricsPageViewModel(player, nowPlaying, files, persistence, work => Task.FromResult(work())));
        return (shell, player, files, root);
    }

    private static (ShellView View, Window Window) Mount(ShellViewModel shell)
    {
        var view = new ShellView { DataContext = shell };
        var window = new Window { Width = 412, Height = 915, Content = view };
        window.Show();
        window.UpdateLayout();
        return (view, window);
    }

    private static Track NewTrack() =>
        new() { Id = Guid.NewGuid(), Title = "Song", Artist = "Tester", FilePath = "content://x/1", Duration = TimeSpan.FromSeconds(90) };

    /// <summary>Beta 2 (lead's live test): paused lyrics in landscape spilled off screen on a
    /// phone in a decimal-comma culture - the split was parsed from "{side},*", which formats as
    /// "444,27,*" there, three columns. Two columns in any culture, the transport inside the screen.</summary>
    [AvaloniaFact]
    public void Landscape_SplitsInTwo_InADecimalCommaCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
        try
        {
            var (shell, _, files, _) = MakeShell();
            files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
            var view = new ShellView { DataContext = shell };
            var window = new Window { Width = 915, Height = 412, Content = view };
            window.Show();
            shell.Player.PlayTracks(new[] { NewTrack() }, 0);
            shell.Player.TogglePlayPauseCommand.Execute(null);   // paused: the transport shows
            shell.OpenNowPlayingCommand.Execute(null);
            shell.ToggleLyricsCommand.Execute(null);
            for (var i = 0; i < 3; i++)
            {
                Avalonia.Threading.Dispatcher.UIThread.RunJobs();
                window.UpdateLayout();
            }

            var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
            var split = page.FindControl<Grid>("Split")!;
            Assert.Equal(2, split.ColumnDefinitions.Count);
            Assert.True(page.FindControl<Control>("SideArtBox")!.IsVisible);
            var side = page.FindControl<Control>("LyricsSide")!;
            var left = side.TranslatePoint(default, page)!.Value.X;
            Assert.True(left > page.Bounds.Width / 3, $"lyrics side starts at {left}");
            Assert.Equal(page.Bounds.Width, left + side.Bounds.Width, 1);
            var next = page.FindControl<Control>("NextButton")!;
            Assert.True(next.IsEffectivelyVisible);
            Assert.True(next.TranslatePoint(new Point(next.Bounds.Width, 0), page)!.Value.X <= page.Bounds.Width);
            window.Close();
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [AvaloniaFact]
    public void IssueExample_ShowsWordsAndTranslation_AndTheToggleHidesIt()
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // PlaySong only plays tracks in the library list
        player.RaisePositionChanged(TimeSpan.FromSeconds(2.5));
        shell.Lyrics.OnFrame(1000);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.True(page.IsVisible);
        Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "例えば" && t.IsEffectivelyVisible);
        var translation = page.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "For example, if I were not myself");
        Assert.True(translation.IsEffectivelyVisible);
        Assert.True(page.FindControl<Control>("TranslationToggle")!.IsVisible);

        shell.Lyrics.ShowTranslation = false;
        window.UpdateLayout();
        Assert.False(translation.IsEffectivelyVisible);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void NoLyrics_ShowsTheStatus()
    {
        var (shell, _, _, root) = MakeShell();
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // PlaySong only plays tracks in the library list
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var status = page.FindControl<TextBlock>("StatusText")!;
        Assert.True(status.IsEffectivelyVisible);
        Assert.Equal("No Lyrics", status.Text);
        Assert.True(page.FindControl<TextBlock>("StatusDetail")!.IsEffectivelyVisible);
        Assert.True(page.FindControl<PathIcon>("EmptyGlyph")!.IsEffectivelyVisible);
        Assert.False(page.FindControl<ScrollViewer>("LyricsScroll")!.IsEffectivelyVisible);
        // Nothing to read: the transport stays up.
        page.HideControlsIfPlaying();
        Assert.True(page.ControlsShown);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void SystemFontScale_SizesTheLyrics()
    {
        var (shell, _, _, root) = MakeShell();
        var (view, window) = Mount(shell);

        shell.Lyrics.FontScale = 1.5;
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.Equal(LyricsPageViewModel.BaseFontSize * 1.5, page.FindControl<ItemsControl>("LyricsList")!.FontSize, 6);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>The index only advances on frames, and frames only run while the page is
    /// visible, so opening it mid-song must catch up before the first snap — otherwise the
    /// page snaps to a stale line and then glides (playing) or stays wrong (paused).</summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void OpeningMidSong_ShowsTheCurrentLine(bool pauseBeforeOpening)
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        var (_, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // lyrics page closed throughout
        player.RaisePositionChanged(TimeSpan.FromSeconds(5.5));   // second line starts at 5.0 s
        if (pauseBeforeOpening) shell.Player.TogglePlayPauseCommand.Execute(null);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        Assert.Equal(1, shell.Lyrics.ActiveLineIndex);
        Assert.Equal(1.0, shell.Lyrics.Lines[1].LineOpacity, 6);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    [AvaloniaFact]
    public void ScrollToLine_PutsTheLineOnTheAnchor()
    {
        var (shell, _, files, root) = MakeShell();
        files.Sidecars[".lrc"] = string.Join('\n', Enumerable.Range(0, 60).Select(i => $"[00:{i:00}.50]line {i}"));
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);   // PlaySong only plays tracks in the library list
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var scroll = page.FindControl<ScrollViewer>("LyricsScroll")!;
        var list = page.FindControl<ItemsControl>("LyricsList")!;
        page.ScrollToLine(30, animate: false);
        window.UpdateLayout();

        var container = list.ContainerFromIndex(30)!;
        var top = container.TranslatePoint(new Point(0, 0), scroll)!.Value.Y;
        var expected = scroll.Viewport.Height * LyricsPage.AnchorRatio - container.Bounds.Height / 2;
        Assert.InRange(top, expected - 2, expected + 2);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static Control LineContainer(LyricsPage page, int index) =>
        page.FindControl<ItemsControl>("LyricsList")!.ContainerFromIndex(index)!;

    /// <summary>The desktop's word cell: each word is one KaraokeWordText drawing unsung and sung
    /// text together (so a finished line cannot stack a sweep on its base, the old two-layer
    /// cell's bug), and only the sung line's word layer is in the karaoke look (dim base).</summary>
    [AvaloniaFact]
    public void Words_UseTheDesktopCell_OnlyTheSungLineIsInKaraokeLook()
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);
        player.RaisePositionChanged(TimeSpan.FromSeconds(5.5));   // line 0 done, line 1 active
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();
        Assert.Equal(1, shell.Lyrics.ActiveLineIndex);

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        List<KaraokeWordText> Sweeps(int line) => LineContainer(page, line).GetVisualDescendants().OfType<KaraokeWordText>()
            .Where(t => t.Classes.Contains("word-sweep")).ToList();
        ItemsControl WordLayer(int line) => LineContainer(page, line).GetVisualDescendants().OfType<ItemsControl>()
            .First(i => i.Classes.Contains("word-layer"));
        Assert.Equal(5, Sweeps(0).Count);
        Assert.All(Sweeps(0), t => Assert.True(t.IsEffectivelyVisible));
        Assert.All(Sweeps(0), t => Assert.Equal(1.0, t.BaseOpacity, 6));   // a line not being sung reads as plain text
        Assert.Contains(Sweeps(0), t => t.Text == "例えば");
        Assert.Single(Sweeps(1));
        Assert.DoesNotContain("active", WordLayer(0).Classes);
        Assert.Contains("active", WordLayer(1).Classes);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>The desktop's depth: the sung line sharp and at full size, the others blurred by
    /// distance and resting at 0.96; browsing the lyrics sharpens every line.</summary>
    [AvaloniaFact]
    public void ActiveLineIsSharp_NeighboursBlurred_BrowsingSharpensAll()
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".lrc"] = string.Join('\n', Enumerable.Range(0, 12).Select(i => $"[00:{i * 3:00}.50]line {i}"));
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);
        player.RaisePositionChanged(TimeSpan.FromSeconds(15.6));   // line 5
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var lyrics = shell.Lyrics;
        Assert.Equal(5, lyrics.ActiveLineIndex);
        Assert.Equal(0, lyrics.Lines[5].BlurRadius);
        Assert.Equal(5, lyrics.Lines[6].BlurRadius);    // next line
        Assert.Equal(8, lyrics.Lines[4].BlurRadius);    // the line just sung recedes a step faster
        Assert.Equal(8, lyrics.Lines[7].BlurRadius);
        Assert.Equal(12, lyrics.Lines[11].BlurRadius);
        Assert.True(lyrics.Lines[4].LineOpacity < lyrics.Lines[6].LineOpacity);
        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var activeButton = LineContainer(page, 5).GetVisualDescendants().OfType<Button>().First();
        Assert.Contains("active", activeButton.Classes);
        Assert.IsType<BlurEffect>(LineContainer(page, 4).GetVisualDescendants().OfType<Button>().First().Effect);

        page.BeginBrowse();
        Assert.True(lyrics.IsBrowsing);
        Assert.All(lyrics.Lines, l => Assert.Equal(0, l.BlurRadius));
        Assert.All(lyrics.Lines, l => Assert.True(l.LineOpacity >= LyricsPageViewModel.BrowseOpacityFloor));
        page.EndBrowse(scrollBack: false);
        Assert.False(lyrics.IsBrowsing);
        Assert.Equal(8, lyrics.Lines[4].BlurRadius);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>An explicit song carries the E badge after its title in the header.</summary>
    [AvaloniaFact]
    public void Header_ShowsTheEBadge_ForAnExplicitSong()
    {
        var (shell, _, files, root) = MakeShell();
        files.Sidecars[".lrc"] = "[00:00.50]one\n[00:03.00]two";
        var (view, window) = Mount(shell);
        var clean = NewTrack();
        var dirty = NewTrack();
        dirty.IsExplicit = true;

        shell.Player.PlayTracks(new[] { dirty, clean }, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var badge = page.FindControl<Border>("HeaderExplicit")!;
        Assert.True(badge.IsEffectivelyVisible);
        Assert.True(badge.Classes.Contains("explicit-badge"));

        shell.Player.NextCommand.Execute(null);
        window.UpdateLayout();
        Assert.False(badge.IsVisible);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>Apple's header: the playing song's title and artist over the wash.</summary>
    [AvaloniaFact]
    public void Header_ShowsTheSong()
    {
        var (shell, _, files, root) = MakeShell();
        files.Sidecars[".lrc"] = "[00:00.50]one\n[00:03.00]two";
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.Equal("Song", page.FindControl<TextBlock>("HeaderTitle")!.Text);
        Assert.Equal("Tester", page.FindControl<TextBlock>("HeaderArtist")!.Text);
        Assert.True(page.FindControl<Control>("LyricsMore")!.IsEffectivelyVisible);
        Assert.False(page.FindControl<Control>("EmptyState")!.IsEffectivelyVisible);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>While the song plays the page opens on the lines alone and the transport fades
    /// away again after a touch (Apple Music's lyrics screen); pausing brings it up and keeps it.</summary>
    [AvaloniaFact]
    public void Controls_HideWhilePlaying_ComeBackOnPause()
    {
        var (shell, _, files, root) = MakeShell();
        files.Sidecars[".lrc"] = "[00:00.50]one\n[00:03.00]two";
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Assert.True(shell.Player.IsPlaying);
        Assert.False(page.ControlsShown);   // Apple's: the lines alone while the song plays
        page.ShowControls();                // a touch
        Assert.True(page.ControlsShown);
        page.HideControlsIfPlaying();
        Assert.False(page.ControlsShown);

        shell.Player.TogglePlayPauseCommand.Execute(null);
        Assert.False(shell.Player.IsPlaying);
        Assert.True(page.ControlsShown);
        page.HideControlsIfPlaying();
        Assert.True(page.ControlsShown);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>Tapping a line must not flash Fluent's hover/press box or zoom it (Fluent's
    /// pressed scale, Noctis.UI's Button:pointerover scale(1.045), loaded here as on Android).
    /// A plain Button in the same window proves the pseudo-class simulation reaches both.</summary>
    [AvaloniaTheory]
    [InlineData(":pointerover")]
    [InlineData(":pressed")]
    public void LyricLine_NoChromeAndNoZoom_OnHoverOrPress(string pseudo)
    {
        var (shell, _, files, root) = MakeShell();
        files.Sidecars[".lrc"] = "[00:00.50]one\n[00:03.00]two";
        var view = new ShellView { DataContext = shell };
        var plain = new Button { Content = "plain" };
        var window = new Window { Width = 412, Height = 915, Content = new Panel { Children = { view, plain } } };
        // Styles.axaml resolves this StaticResource at load, from the app (as ArtistDetailViewMountTests).
        if (!Application.Current!.Resources.ContainsKey("InterSemiBold"))
            Application.Current.Resources["InterSemiBold"] = FontFamily.Default;
        window.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml")
        });
        window.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
        {
            Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml")
        });
        window.Show();

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        var line = LineContainer(page, 0).GetVisualDescendants().OfType<Button>().First();
        Assert.Contains("lyric-line", line.Classes);
        foreach (var b in new[] { line, plain }) b.Transitions = null;   // transitions are frame-driven
        window.UpdateLayout();
        var resting = line.RenderTransform?.Value ?? Matrix.Identity;   // 0.96 before the first line
        var presenter = line.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        presenter.Transitions = null;
        var plainPresenter = plain.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
        plainPresenter.Transitions = null;

        ((IPseudoClasses)line.Classes).Set(pseudo, true);
        ((IPseudoClasses)plain.Classes).Set(pseudo, true);
        window.UpdateLayout();

        Assert.NotEqual(Matrix.Identity, plain.RenderTransform!.Value);   // the simulation bites
        Assert.NotEqual(Colors.Transparent, Assert.IsAssignableFrom<ISolidColorBrush>(plainPresenter.Background).Color);
        Assert.Equal(resting, line.RenderTransform?.Value ?? Matrix.Identity);
        var bg = presenter.Background;
        Assert.True(bg is null || (bg is ISolidColorBrush solid && solid.Color.A == 0), $"presenter background {bg}");

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    /// <summary>A long intro gets LrcParser's placeholder line; it must show the dots rather
    /// than leave the page anchored on a blank slot.</summary>
    [AvaloniaFact]
    public void LongIntro_ShowsThreeDots()
    {
        var (shell, player, files, root) = MakeShell();
        files.Sidecars[".lrc"] = "[00:03.00]one\n[00:05.00]two";
        var (view, window) = Mount(shell);

        shell.Player.PlayTracks(new[] { NewTrack() }, 0);
        player.RaisePositionChanged(TimeSpan.FromSeconds(0.5));
        shell.OpenNowPlayingCommand.Execute(null);
        shell.ToggleLyricsCommand.Execute(null);
        window.UpdateLayout();

        Assert.True(shell.Lyrics.Lines[0].IsIntroPlaceholder);
        var page = view.GetLogicalDescendants().OfType<LyricsPage>().Single();
        Control Dots(int line) => LineContainer(page, line).GetVisualDescendants().OfType<StackPanel>().First(s => s.Name == "IntroDots");
        Assert.True(Dots(0).IsEffectivelyVisible);
        Assert.Equal(3, Dots(0).GetVisualChildren().OfType<Avalonia.Controls.Shapes.Ellipse>().Count());
        Assert.False(Dots(1).IsEffectivelyVisible);

        window.Close();
        try { Directory.Delete(root, recursive: true); } catch { }
    }
}
