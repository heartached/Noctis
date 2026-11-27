using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
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
/// Owner 11-26 Lyrics Studio redesign: pictures of the sidebar page (a 12-song queue with long
/// titles, one song open with 20 timed lines and the live preview, the empty review, and the
/// backdrops panel open) at a wide and a narrow width, dark theme. Only runs with real rendering
/// and NOCTIS_STUDIO_SHOTS_DIR set; NOCTIS_STUDIO_SHOTS_TAG prefixes the file names.
/// </summary>
public class LyricsStudioPageShotsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "studio-page-shots-" + Guid.NewGuid().ToString("N")[..8]);

    public LyricsStudioPageShotsTests() => Directory.CreateDirectory(_root);
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static readonly string[] Titles =
    {
        "Ballin", "Look Back at It (A Very Long Title That Has To Trim Before The Badge)", "Drowning", "Swervin",
        "Startender", "Way Too Fly (feat. Someone With A Long Name)", "Mood Swings", "Beast Mode", "Numb",
        "Demons and Angels", "Bag Talk", "Come Closer",
    };

    private static string Lrc()
    {
        var words = new[]
        {
            "Bentley truck, I'm ballin', yeah", "Got the money, got the cars", "Mama said I'm gonna be a star",
            "Now I'm ballin', ballin'", "Ain't no way they stoppin' me now", "From the bottom to the top, look how",
            "Every night we out, we loud", "Ballin', ballin', yeah",
        };
        var sb = new StringBuilder();
        for (var i = 0; i < 20; i++)
        {
            var t = TimeSpan.FromSeconds(14.44 + i * 3.1);
            sb.Append($"[{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}]{words[i % words.Length]}\n");
        }
        return sb.ToString();
    }

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("ChevronDownThinIcon", null, out _))
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        if (!app.Styles.OfType<StyleInclude>().Any(s => s.Source?.ToString().Contains("Noctis.UI/Assets/Styles.axaml") == true))
        {
            app.Resources["InterSemiBold"] = new FontFamily("avares://Noctis.UI/Assets/Fonts/Inter-SemiBold.ttf#Inter SemiBold");
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
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

    private LyricsStudioViewModel Studio()
    {
        var tracks = new List<Track>();
        for (var i = 0; i < Titles.Length; i++)
        {
            var path = Path.Combine(_root, $"song{i}.mp3");
            if (i == 0) File.WriteAllText(Path.ChangeExtension(path, ".lrc"), Lrc());
            tracks.Add(new Track
            {
                Title = Titles[i], Artist = i % 3 == 0 ? "A Boogie wit da Hoodie" : "Some Artist",
                FilePath = path, Duration = TimeSpan.FromMinutes(3), IsExplicit = i % 4 != 2,
            });
        }
        var player = new PlayerViewModel(new FakeAudioPlayer(), new FakeLibraryService(), new TestPersistenceService(), new FakeAnimatedCoverService());
        var vm = new LyricsStudioViewModel(tracks, new IdleEngine(_root), new LyricsWriter(null!, null), new FakeLibraryService(), player,
            () => new AppSettings { LyricsStudioWordTimings = true, LyricsStudioLivePreview = true }, _ => { });
        vm.Confirm = _ => Task.FromResult(true);
        vm.PickLyricsFile = () => Task.FromResult<string?>(null);
        vm.ShowOnLyricsPage = (_, _) => Task.CompletedTask;
        vm.UpdateLivePreview = (_, _) => { };
        return vm;
    }

    [AvaloniaFact]
    public void StudioPage_SavePngs()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_STUDIO_SHOTS_DIR");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        var tag = Environment.GetEnvironmentVariable("NOCTIS_STUDIO_SHOTS_TAG") ?? "shot";
        Directory.CreateDirectory(dir);
        EnsureAppStyles();

        foreach (var width in new[] { 1920, 1300, 1050 })
        {
            foreach (var scene in new[] { "review", "empty", "backdrops" })
            {
                var studio = Studio();
                var page = new LyricsStudioPageViewModel(new FakeLibraryService(), () => true, _ => studio)
                {
                    Counts = SettingsViewModel.BuildLyricsStudioCounts(
                        Enumerable.Repeat(LyricsFormat.Elrc, 36).Concat(Enumerable.Repeat(LyricsFormat.Lrc, 238))
                            .Concat(Enumerable.Repeat(LyricsFormat.Plain, 2)).Concat(Enumerable.Repeat(LyricsFormat.None, 2)).ToList()),
                    Studio = studio,
                    ShowBackdrops = scene == "backdrops",
                };
                AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
                {
                    var view = new LyricsStudioView { DataContext = page };
                    var win = new Window
                    {
                        Width = width, Height = 1000, Content = view,
                        RequestedThemeVariant = ThemeVariant.Dark,
                        Background = new SolidColorBrush(Color.Parse("#141414")),
                    };
                    win.Show();
                    try
                    {
                        Pump(200);
                        if (scene != "empty") studio.Selected = studio.Queue[0];
                        Pump(900);
                        win.CaptureRenderedFrame()!.Save(Path.Combine(dir, $"{tag}-{scene}-{width}.png"));
                    }
                    finally { win.Close(); }
                });
            }
        }
    }

    private sealed class IdleEngine(string root) : ILyricsStudioEngine
    {
        public bool HasFfmpeg => true;
        public WhisperModelManager Models { get; } = StudioTestModel.Installed(root);
        public IDisposable OpenSession(WhisperModelSize model) => new Handle();
        public Task<LyricsStudioResult> ProcessAsync(Track track, LyricsStudioOptions options, IProgress<LyricsStudioProgress>? progress, CancellationToken ct) =>
            throw new NotSupportedException("the shots never run a song");
        private sealed class Handle : IDisposable { public void Dispose() { } }
    }
}
