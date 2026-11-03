using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Track = Noctis.Models.Track;
using SliderTrack = Avalonia.Controls.Primitives.Track;
using Xunit;

namespace Noctis.Tests;

/// <summary>The phone's Settings › Lyrics page (10-13): the sliders' thumb and shadow fit inside
/// the card, a drag glides and saves once, and every row changes the lyrics page and persists.</summary>
public class MobileLyricsSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, recursive: true); } catch { } }

    private const string SplitWordTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <body><div>
            <p begin="00:01.000" end="00:04.000">
              <span begin="00:01.000" end="00:01.400">Is </span>
              <span begin="00:01.400" end="00:01.800">a </span>
              <span begin="00:01.800" end="00:02.100">com</span>
              <span begin="00:02.100" end="00:02.400">pro</span>
              <span begin="00:02.400" end="00:03.400">mise</span>
            </p>
            <p begin="00:05.000" end="00:07.000"><span begin="00:05.000" end="00:07.000">end</span></p>
          </div></body>
        </tt>
        """;

    private const string BackgroundTtml = """
        <tt xmlns="http://www.w3.org/ns/ttml" xmlns:ttm="http://www.w3.org/ns/ttml#metadata">
          <body><div>
            <p begin="1.000" end="3.000"><span begin="1.000" end="2.000">lead</span><span ttm:role="x-bg"><span begin="2.000" end="2.800">(adlib)</span></span></p>
            <p begin="4.000" end="5.000"><span begin="4.000" end="5.000">next</span></p>
          </div></body>
        </tt>
        """;

    private static Track NewTrack(string name) =>
        new() { Id = Guid.NewGuid(), Title = name, FilePath = $"content://x/{name}", Duration = TimeSpan.FromSeconds(60) };

    private (LyricsPageViewModel Vm, NowPlayingViewModel Np, FakeAudioPlayer Player, FakeTrackFiles Files, PersistenceService Persistence) MakeLyrics()
    {
        var player = new FakeAudioPlayer();
        var persistence = new PersistenceService(_root);
        var np = new NowPlayingViewModel(player, new FakeLibraryService(), persistence, marshal: a => a());
        var files = new FakeTrackFiles();
        var vm = new LyricsPageViewModel(player, np, files, persistence, work => Task.FromResult(work()));
        return (vm, np, player, files, persistence);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline) await Task.Delay(15);
        Assert.True(condition());
    }

    /// <summary>Lets the pushed page's slide run to the end.</summary>
    private static void Settle(Window window, int ms = 520)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Thread.Sleep(5);
        }
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        MobileFixtures.Land(window);   // the sheet's rise can outlast the wait (its first frame is slow)
    }

    /// <summary>Settings mounted on its Lyrics page, the icon set merged as on the device.</summary>
    private static async Task OnLyricsPage(MobileFixtures.Rig rig, Func<SettingsPage, SettingsPageViewModel, Window, Task> check)
    {
        var app = Application.Current!;
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        var window = MobileFixtures.Mount(rig.Shell, out var view);
        try
        {
            rig.Shell.OpenSettingsCommand.Execute(null);
            var vm = (SettingsPageViewModel)rig.Shell.CurrentPage!;
            await vm.Loaded;
            vm.OpenPaneCommand.Execute(SettingsPane.Lyrics);
            Settle(window);
            await check(MobileFixtures.Find<SettingsPage>(view), vm, window);
        }
        finally
        {
            window.Close();
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    private static Rect InCard(Control control, Border card) =>
        new(control.TranslatePoint(new Point(0, 0), card)!.Value, control.Bounds.Size);

    /// <summary>Owner 10-13: the thumb's shadow was cut to a square at the circle's edges and, at
    /// the minimum, a sliver of accent showed left of the circle. The thumb is now a 44 dp box
    /// with the 26 dp circle centred in it (room for the shadow on every side), the circle sits on
    /// the bar's centre line and lines up with the row's 16 dp inset at both ends, and neither
    /// bar shows past the circle there.</summary>
    [AvaloniaFact]
    public async Task TextSizeSlider_ThumbAndShadowFit_AndNothingPeeksPastTheCircleAtTheEnds()
    {
        using var rig = MobileFixtures.MakeRig();
        await OnLyricsPage(rig, (page, vm, window) =>
        {
            var card = MobileFixtures.Named<Border>(page, "LyricsSizeCard");
            var slider = MobileFixtures.Named<SmoothSlider>(page, "LyricsSizeSlider");
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var circle = thumb.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "PART_Circle");
            var track = slider.GetVisualDescendants().OfType<SliderTrack>().Single();
            var fill = slider.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_DecreaseButton");
            var rest = slider.GetVisualDescendants().OfType<RepeatButton>().Single(b => b.Name == "PART_IncreaseButton");

            Assert.Equal(new Size(44, 44), thumb.Bounds.Size);
            Assert.Equal(new Size(26, 26), circle.Bounds.Size);
            Assert.Equal(new Point(9, 9), circle.TranslatePoint(new Point(0, 0), thumb)!.Value);
            // The shadow (0 2 6: 4 dp above, 8 dp below the circle) stays inside the box.
            var c = circle.TranslatePoint(new Point(0, 0), thumb)!.Value;
            Assert.True(c.Y - 4 >= 0 && c.Y + 26 + 8 <= thumb.Bounds.Height);
            // ...and inside the card (which clips).
            var circleInCard = InCard(circle, card);
            Assert.True(circleInCard.Bottom + 8 <= card.Bounds.Height, $"shadow bottom {circleInCard.Bottom + 8} > {card.Bounds.Height}");
            Assert.Equal(InCard(track, card).Center.Y, circleInCard.Center.Y, 1);

            vm.LyricsTextScale = 0.8;
            window.UpdateLayout();
            circleInCard = InCard(circle, card);
            Assert.Equal(16, circleInCard.Left, 1);
            var fillRect = InCard(fill, card);
            Assert.True(fillRect.Width < 0.01, $"accent at {fillRect} by the circle {circleInCard}");   // was 1 dp: a red sliver
            Assert.True(InCard(rest, card).Left <= circleInCard.Right - 0.5, "a gap between the circle and the grey bar");

            vm.LyricsTextScale = 1.6;
            window.UpdateLayout();
            circleInCard = InCard(circle, card);
            Assert.Equal(card.Bounds.Width - 16, circleInCard.Right, 1);
            var restRect = InCard(rest, card);
            Assert.True(restRect.Width < 0.01, $"grey at {restRect} by the circle {circleInCard}");
            Assert.True(InCard(fill, card).Right >= circleInCard.Left + 0.5, "a gap between the accent and the circle");
            return Task.CompletedTask;
        });
    }

    /// <summary>A finger drag: the thumb follows the finger move by move (it used to snap from
    /// notch to notch, 0.1 apart), the value label follows the step under it live, and the whole
    /// drag writes settings.json once after it settles (it used to write once per step).</summary>
    [AvaloniaFact]
    public async Task TextSizeSlider_Drag_FollowsTheFinger_LabelLive_SavesOnce()
    {
        using var rig = MobileFixtures.MakeRig();
        await OnLyricsPage(rig, async (page, vm, window) =>
        {
            var slider = MobileFixtures.Named<SmoothSlider>(page, "LyricsSizeSlider");
            var label = MobileFixtures.Named<TextBlock>(page, "LyricsSizeLabel");
            var thumb = slider.GetVisualDescendants().OfType<Thumb>().Single();
            var saves = vm.SaveCount;

            Point ThumbCentre() => thumb.TranslatePoint(new Point(22, 22), window)!.Value;
            var start = ThumbCentre();
            window.MouseDown(start, MouseButton.Left);
            var labels = new HashSet<string?>();
            for (var dx = 3; dx <= 240; dx += 3)
            {
                window.MouseMove(start + new Point(dx, 0));
                window.UpdateLayout();
                Assert.Equal(start.X + dx, ThumbCentre().X, 1);          // no lag, no notch jumps
                Assert.Equal(vm.LyricsTextScale.ToString("P0"), label.Text);
                Assert.Equal(Math.Round(vm.LyricsTextScale, 1), vm.LyricsTextScale, 6);   // the setting moves in steps
                Assert.Equal(vm.LyricsTextScale * LyricsPageViewModel.BaseFontSize, rig.Shell.Lyrics.LineFontSize, 6);
                labels.Add(label.Text);
            }
            window.MouseUp(start + new Point(240, 0), MouseButton.Left);
            Assert.True(labels.Count >= 4, $"{labels.Count} steps");

            await vm.PendingSave;
            Assert.Equal(saves + 1, vm.SaveCount);
            Assert.Equal(vm.LyricsTextScale, (await rig.Persistence.LoadSettingsAsync()).MobileLyricsTextScale, 6);
        });
    }

    /// <summary>Every row is wired: each switch flips its lyrics setting (and the top-level Lyrics
    /// row's value), the opacity slider moves the floor.</summary>
    [AvaloniaFact]
    public async Task LyricsRows_DriveTheLyricsSettings()
    {
        using var rig = MobileFixtures.MakeRig();
        await OnLyricsPage(rig, (page, vm, window) =>
        {
            var lyrics = rig.Shell.Lyrics;
            MobileFixtures.Named<ToggleButton>(page, "TranslationSwitch").IsChecked = false;
            Assert.False(lyrics.ShowTranslation);
            Assert.Equal("Romanization on", vm.LyricsValue);
            MobileFixtures.Named<ToggleButton>(page, "RomajiSwitch").IsChecked = false;
            Assert.False(lyrics.ShowRomanization);
            Assert.Equal("Off", vm.LyricsValue);
            MobileFixtures.Named<ToggleButton>(page, "BackgroundVocalsSwitch").IsChecked = false;
            Assert.False(lyrics.ShowBackgroundVocals);
            MobileFixtures.Named<ToggleButton>(page, "JoinWordsSwitch").IsChecked = true;
            Assert.True(lyrics.JoinSplitWords);

            var opacity = MobileFixtures.Named<SmoothSlider>(page, "LyricsOpacitySlider");
            Assert.Equal(AppSettings.LyricsMinLineOpacityDefault, opacity.SteppedValue);
            opacity.Value = 40.4;
            window.UpdateLayout();
            Assert.Equal(40, lyrics.MinLineOpacity);
            Assert.Equal("40%", MobileFixtures.Named<TextBlock>(page, "LyricsOpacityLabel").Text);
            return Task.CompletedTask;
        });
    }

    [AvaloniaFact]
    public async Task BackgroundVocals_Switch_HidesTheAdlibRow_OnTheLyricsPage()
    {
        using var rig = MobileFixtures.MakeRig();
        var files = new FakeTrackFiles { Sidecars = { [".ttml"] = BackgroundTtml } };
        var lyrics = new LyricsPageViewModel(rig.Player, rig.Shell.Player, files, rig.Persistence, work => Task.FromResult(work()));
        var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, lyrics) { TintFactory = rig.Shell.TintFactory };
        var window = MobileFixtures.Mount(shell, out var view);
        try
        {
            shell.Player.PlayTracks(new[] { NewTrack("bg") }, 0);
            await WaitForAsync(() => lyrics.HasLyrics);
            shell.OpenNowPlayingCommand.Execute(null);
            shell.ToggleLyricsCommand.Execute(null);
            window.UpdateLayout();
            var page = MobileFixtures.Find<LyricsPage>(view);
            TextBlock Adlib() => page.GetVisualDescendants().OfType<TextBlock>().First(t => t.Text == "(adlib)" && t.Classes.Contains("word-sweep"));
            Assert.True(Adlib().IsEffectivelyVisible);

            lyrics.ShowBackgroundVocals = false;
            window.UpdateLayout();
            Assert.False(Adlib().IsEffectivelyVisible);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "lead" && t.IsEffectivelyVisible);

            lyrics.ShowBackgroundVocals = true;
            window.UpdateLayout();
            Assert.True(Adlib().IsEffectivelyVisible);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>The new rows persist across a restart, read from the desktop's own settings keys.</summary>
    [Fact]
    public async Task BackgroundVocals_OpacityFloor_JoinWords_Persist()
    {
        var (vm, _, _, _, persistence) = MakeLyrics();
        await vm.InitializeAsync();
        Assert.True(vm.ShowBackgroundVocals);
        Assert.Equal(AppSettings.LyricsMinLineOpacityDefault, vm.MinLineOpacity);
        Assert.False(vm.JoinSplitWords);

        vm.ShowBackgroundVocals = false;
        await vm.PendingSave;
        vm.JoinSplitWords = true;
        await vm.PendingSave;
        vm.MinLineOpacity = 33;
        await vm.PendingSave;

        var saved = await persistence.LoadSettingsAsync();
        Assert.False(saved.LyricsShowBackgroundVocals);
        Assert.True(saved.LyricsJoinSplitWords);
        Assert.Equal(33, saved.LyricsMinLineOpacity);

        var (again, _, _, _, _) = MakeLyrics();
        await again.InitializeAsync();
        Assert.False(again.ShowBackgroundVocals);
        Assert.True(again.JoinSplitWords);
        Assert.Equal(33, again.MinLineOpacity);
    }

    /// <summary>The floor re-applies to the shown lines at once (as the desktop's re-floor does), and
    /// a slider drag across many steps writes settings.json once.</summary>
    [Fact]
    public async Task MinLineOpacity_RefloorsTheShownLines_AndADragSavesOnce()
    {
        var (vm, np, player, files, persistence) = MakeLyrics();
        files.Sidecars[".ttml"] = MobileLyricsViewModelTests.IssueTtml;
        await vm.InitializeAsync();
        np.PlayTracks(new[] { NewTrack("a") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        player.RaisePositionChanged(TimeSpan.FromSeconds(2.5));
        vm.OnFrame(1000);
        Assert.Equal(0, vm.ActiveLineIndex);
        Assert.Equal(0.55, vm.Lines[1].LineOpacity, 6);          // the ramp, one line away

        var saves = vm.SaveCount;
        for (var v = 16; v <= 60; v++) vm.MinLineOpacity = v;     // a drag up to 60 %
        Assert.Equal(0.60, vm.Lines[1].LineOpacity, 6);
        Assert.Equal(1.0, vm.Lines[0].LineOpacity, 6);
        await vm.PendingSave;
        Assert.Equal(saves + 1, vm.SaveCount);
        Assert.Equal(60, (await persistence.LoadSettingsAsync()).LyricsMinLineOpacity);

        vm.MinLineOpacity = 0;
        Assert.Equal(0.55, vm.Lines[1].LineOpacity, 6);
        await vm.PendingSave;
    }

    /// <summary>Joining changes the parse: flipping it re-reads the playing song's lyrics.</summary>
    [Fact]
    public async Task JoinSplitWords_Flipped_ReparsesThePlayingSong()
    {
        var (vm, np, _, files, _) = MakeLyrics();
        files.Sidecars[".ttml"] = SplitWordTtml;
        await vm.InitializeAsync();
        np.PlayTracks(new[] { NewTrack("j") }, 0);
        await WaitForAsync(() => vm.HasLyrics);
        Assert.Equal("Is a com pro mise", vm.Lines[0].Text);

        vm.JoinSplitWords = true;
        await WaitForAsync(() => vm.Lines.Count > 0 && vm.Lines[0].Text == "Is a compromise");
        Assert.Equal(3, vm.Lines[0].Words!.Count);

        vm.JoinSplitWords = false;
        await WaitForAsync(() => vm.Lines.Count > 0 && vm.Lines[0].Text == "Is a com pro mise");
        await vm.PendingSave;
    }
}
