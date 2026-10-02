using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services.Lyrics;
using Noctis.Services.LyricsStudio;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// PNG probe of the Lyrics Studio review (10-01 polish), for an eye check: the review header
/// at the dialog's width and a range of page widths, a line's word strip open, and the Choose
/// songs picker with explicit songs. Runs only with NOCTIS_TEST_SKIA=1; PNGs go to
/// NOCTIS_RENDER_OUT (default: a temp folder, printed to the test output).
/// </summary>
public class LyricsStudioPolishProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-polish-probe-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _out;

    public LyricsStudioPolishProbeTests()
    {
        Directory.CreateDirectory(_root);
        var outDir = Environment.GetEnvironmentVariable("NOCTIS_RENDER_OUT");
        _out = string.IsNullOrWhiteSpace(outDir) ? Path.Combine(Path.GetTempPath(), "noctis-studio-polish-shots") : outDir;
    }

    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private const string Lrc =
        "[01:51.80]Bentley truck, I'm ballin', yeah\n[01:54.10]Got the money, got the cars\n[01:57.30]Mama said I'm gonna be a star\n" +
        "[02:00.60]Now I'm ballin', ballin'\n[02:03.20]Ain't no way they stoppin' me now\n[02:06.40]From the bottom to the top, look how\n" +
        "[02:09.90]Every night we out, we loud\n[02:12.50]Ballin', ballin', yeah\n";

    [AvaloniaFact]
    public void Probe_RenderReviewHeaderWordStripAndPicker()
    {
        if (!HeadlessTestApp.RealRendering) return; // needs real Skia rendering
        Directory.CreateDirectory(_out);
        EnsureAppResources();

        // The per-song dialog: 1000 wide, its own header, no Preview.
        Capture(Review(preview: false), "review-dialog-1000", 1000, 720, showHeader: true);
        // The sidebar page: no header, Preview on, at the widths a page gets.
        foreach (var width in new[] { 700, 820, 940, 1100, 1300, 1600 })
            Capture(Review(preview: true), $"review-page-{width}", width, 720, showHeader: false);

        // A line's word strip open.
        {
            var vm = Review(preview: true);
            vm.ToggleWordsCommand.Execute(vm.ReviewLines[0]);
            vm.SelectWordCommand.Execute(vm.ReviewLines[0].Words[1]);
            Capture(vm, "review-words-open-1100", 1100, 720, showHeader: false);
        }

        // Choose songs with explicit songs and an explicit album.
        {
            var lib = new FakeLibraryService();
            var albumId = Guid.NewGuid();
            var tracks = new[]
            {
                new Track { Title = "Ballin", Artist = "A Boogie wit da Hoodie", Album = "Hoodie SZN", AlbumId = albumId, IsExplicit = true, FilePath = @"C:\m\1.mp3" },
                new Track { Title = "Look Back at It (A Very Long Title That Has To Trim Before The Badge)", Artist = "A Boogie wit da Hoodie", Album = "Hoodie SZN", AlbumId = albumId, IsExplicit = true, FilePath = @"C:\m\2.mp3" },
                new Track { Title = "Clean Song", Artist = "A Boogie wit da Hoodie", Album = "Hoodie SZN", AlbumId = albumId, FilePath = @"C:\m\3.mp3" },
            };
            lib.TrackList.AddRange(tracks);
            ((List<Album>)lib.Albums).Add(new Album { Id = albumId, Name = "Hoodie SZN", Artist = "A Boogie wit da Hoodie", Tracks = tracks.ToList() });
            var picker = new LyricsStudioPickerViewModel(lib, wordTimings: true, detectFormats: t => t.Select(_ => LyricsFormat.Lrc).ToList());
            picker.SearchText = "a boogie";
            var end = DateTime.UtcNow.AddSeconds(5);
            while (picker.Results.Count == 0 && DateTime.UtcNow < end) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(10); }
            picker.FormatScan.Wait(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            var dialog = new LyricsStudioPickerDialog { DataContext = picker, Width = 640, Height = 720, RequestedThemeVariant = ThemeVariant.Dark };
            Tint(dialog);
            dialog.Show();
            try { Save(dialog, "picker-explicit"); }
            finally { dialog.Close(); }
        }
    }

    private LyricsStudioViewModel Review(bool preview)
    {
        var path = Path.Combine(_root, Guid.NewGuid().ToString("N")[..8] + ".mp3");
        File.WriteAllText(Path.ChangeExtension(path, ".lrc"), Lrc);
        var tracks = new List<Track>
        {
            new() { Title = "Ballin", Artist = "A Boogie wit da Hoodie", FilePath = path, Duration = TimeSpan.FromMinutes(3), IsExplicit = true },
            new() { Title = "Look Back at It", Artist = "A Boogie wit da Hoodie", FilePath = Path.Combine(_root, "other.mp3"), Duration = TimeSpan.FromMinutes(3) },
        };
        var vm = new LyricsStudioViewModel(tracks, new IdleEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), null, () => new AppSettings(), _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.PickLyricsFile = () => Task.FromResult<string?>(null);
        if (preview) vm.ShowOnLyricsPage = (_, _) => Task.CompletedTask;
        vm.Selected = vm.Queue[0];
        return vm;
    }

    private void Capture(LyricsStudioViewModel vm, string name, double width, double height, bool showHeader)
    {
        var panel = new LyricsStudioPanel { DataContext = vm, ShowHeader = showHeader, ShowChooseSongs = !showHeader };
        var card = new Border { Background = new SolidColorBrush(Color.Parse("#252525")), Child = panel };
        var win = new Window
        {
            Width = width, Height = height, Content = card,
            RequestedThemeVariant = ThemeVariant.Dark,
            Background = new SolidColorBrush(Color.Parse("#161616")),
        };
        Tint(win);
        win.Show();
        try { Save(win, name); }
        finally { win.Close(); }
    }

    private static void Tint(Window win)
    {
        var accent = new SolidColorBrush(Color.Parse("#E74856"));
        win.Resources["AccentColorBrush"] = accent;
        win.Resources["AccentButtonBackground"] = accent;
        win.Resources["AccentForegroundBrush"] = Brushes.White;
    }

    private void Save(Window win, string name)
    {
        var end = Environment.TickCount64 + 700;
        while (Environment.TickCount64 < end)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(8);
        }
        var frame = win.CaptureRenderedFrame();
        Assert.NotNull(frame);
        frame!.Save(Path.Combine(_out, name + ".png"));
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
            });
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().Contains("Noctis.UI/Assets/Styles.axaml") == true))
        {
            app.Resources["InterSemiBold"] = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/"))
            {
                Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml"),
            });
        }
    }

    private sealed class IdleEngine(string root) : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = StudioTestModel.Installed(root);
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException("the probe never runs a song");
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
