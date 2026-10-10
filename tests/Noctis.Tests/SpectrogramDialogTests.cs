using System;
using System.Collections.Generic;
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
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.AudioAnalysis;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: the Spectrogram as the rounded pill pop-up (blurred app behind, the shared
/// open/close animation), a tidy header with the stream facts as chips, ONE progress readout
/// centred in the plot (it used to be repeated in the footer), a plot frame that keeps its
/// size when the image lands and fades the plot in, and a close that stops the analysis at
/// once. The decode is a fake; no ffmpeg.
/// </summary>
public class SpectrogramDialogTests : IDisposable
{
    private readonly string _dir;
    private readonly string _file;

    public SpectrogramDialogTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "NoctisTests", "spectrogram-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _file = Path.Combine(_dir, "False Prophet.m4a");
        File.WriteAllBytes(_file, new byte[16]); // RunAsync only checks that it exists
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private sealed class StubConverter : IAudioConverterService
    {
        public string? Path = "ffmpeg";
        public string? GetFfmpegPath() => Path;
        public Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default)
            => Task.FromResult<string?>(null);
        public Task<ConvertSummary> ConvertAsync(IReadOnlyList<Track> tracks, AudioConvertOptions options,
            IProgress<ConvertProgress> progress, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>Reports 15 %, then waits for <see cref="Release"/> or the token (as the real
    /// decode's ReadAsync does), recording whether it saw the cancel.</summary>
    private sealed class FakeAnalysis
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token;
        public volatile bool SawCancel;

        public async Task<SpectrogramData> Run(string ffmpeg, Track track, int columns, IProgress<double> progress, CancellationToken ct)
        {
            Token = ct;
            progress.Report(0.15);
            Started.TrySetResult();
            try { await Release.Task.WaitAsync(ct); }
            catch (OperationCanceledException) { SawCancel = true; throw; }
            var db = new float[columns * SpectrogramRenderer.Bins];
            Array.Fill(db, -60f);
            return new SpectrogramData
            {
                Columns = columns, Bins = SpectrogramRenderer.Bins, Db = db, SampleRate = 44100,
                Duration = track.Duration,
            };
        }
    }

    private Track MakeTrack() => new()
    {
        Id = Guid.NewGuid(), Title = "False Prophet (feat. 6ix9ine)", Artist = "Yng D-Fly",
        Album = "False Prophet (feat. 6ix9ine) - Single", FilePath = _file, Codec = "MPEG-4 Audio (alac)",
        SampleRate = 44100, BitsPerSample = 24, Bitrate = 2476, Duration = TimeSpan.FromSeconds(131),
    };

    private static void EnsureAppStyles()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
    }

    private static bool PumpUntil(Func<bool> condition, int budgetMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (condition()) return true;
            Thread.Sleep(5);
        }
        Dispatcher.UIThread.RunJobs();
        return condition();
    }

    private static List<TextBlock> VisibleTexts(Visual root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && !string.IsNullOrEmpty(t.Text)).ToList();

    private (SpectrogramWindow Win, SpectrogramViewModel Vm, FakeAnalysis Fake, PillDialogHost Host) Open(
        double width = 1300, double height = 900, ThemeVariant? theme = null)
    {
        var fake = new FakeAnalysis();
        var vm = new SpectrogramViewModel(MakeTrack(), new StubConverter(), fake.Run);
        var win = new SpectrogramWindow(vm) { Width = width, Height = height, RequestedThemeVariant = theme ?? ThemeVariant.Dark };
        win.Show();
        var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
        Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
            "open animation never settled");
        return (win, vm, fake, host);
    }

    private static void CloseIfOpen(Window win)
    {
        if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
    }

    // ── The pop-up ──

    [AvaloniaFact]
    public void Spectrogram_OpensInPillHost_WithChips_AndOneProgressReadout()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var (win, vm, fake, host) = Open();
            try
            {
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                Assert.True(host.BlurBackdrop);

                // Close is the shared quiet pill, not the old red accent button.
                var all = win.GetVisualDescendants().ToList();
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("accent-btn"));
                var close = all.OfType<Button>().Single(b => b.Command == vm.CloseCommand);
                Assert.Contains("pill-secondary", close.Classes);

                // Header: title, artist · album, the stream facts as chips.
                Assert.Equal("False Prophet (feat. 6ix9ine)", win.FindControl<TextBlock>("TitleText")!.Text);
                Assert.Equal("Yng D-Fly · False Prophet (feat. 6ix9ine) - Single", win.FindControl<TextBlock>("SubtitleText")!.Text);
                var chipList = win.FindControl<ItemsControl>("ChipList")!;
                var chips = chipList.GetVisualDescendants().OfType<Border>().Where(b => b.Classes.Contains("sg-chip"))
                    .Select(b => b.GetVisualDescendants().OfType<TextBlock>().Single().Text).ToList();
                var khz = 44.1.ToString("0.#", System.Globalization.CultureInfo.CurrentCulture) + " kHz";
                Assert.Equal(new[] { "ALAC", khz, "24-bit", "2476 kbps", "2:11" }, chips);

                // FFT details live in the quiet footer caption, not in the header.
                var caption = win.FindControl<TextBlock>("CaptionText")!;
                Assert.Contains("FFT 2048", caption.Text);
                Assert.True(caption.Opacity < 0.6);
                var header = (Visual)win.FindControl<TextBlock>("TitleText")!.GetVisualAncestors().OfType<Border>().First();
                Assert.DoesNotContain(VisibleTexts(header), t => t.Text!.Contains("FFT"));
                // The grey "Spectrogram" corner label is gone.
                Assert.DoesNotContain(VisibleTexts(win), t => t.Text == Loc.T("Spectrogram.Spectrogram"));

                // Loading: exactly one progress readout, in the plot, and the footer doesn't repeat it.
                Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted && vm.ProgressText.Contains("15%")), "no progress");
                PumpUntil(() => false, 30);
                var percent = VisibleTexts(win).Where(t => t.Text!.Contains('%')).ToList();
                Assert.Single(percent);
                Assert.Equal(Loc.T("Spectrogram.AnalyzingPercent", 15), percent[0].Text);
                var plotFrame = win.FindControl<Border>("PlotFrame")!;
                Assert.True(plotFrame.IsVisualAncestorOf(percent[0]));
                var bar = win.FindControl<ProgressBar>("ProgressBar")!;
                Assert.True(bar.IsEffectivelyVisible);
                Assert.Equal(15, bar.Value, 3);
                Assert.Single(all.OfType<ProgressBar>());
            }
            finally { CloseIfOpen(win); }
        });
    }

    [AvaloniaFact]
    public void PlotFrame_KeepsItsSize_WhenTheImageLands_AndThePlotFadesIn()
    {
        EnsureAppStyles();
        var (win, vm, fake, host) = Open();
        try
        {
            Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted));
            PumpUntil(() => false, 30);
            var image = win.FindControl<Image>("PlotImage")!;
            var loading = win.FindControl<StackPanel>("LoadingPanel")!;
            var frame = win.FindControl<Border>("PlotFrame")!;
            Assert.Equal(0, image.Opacity);
            Assert.Equal(1, loading.Opacity);
            var cardBefore = host.Card!.Bounds.Size;
            var frameBefore = frame.Bounds.Size;
            Assert.True(frameBefore.Height > 300, $"plot frame collapsed: {frameBefore}");

            fake.Release.TrySetResult();
            Assert.True(PumpUntil(() => vm.IsReady && vm.Image != null), "analysis never landed");
            // Not snapped: the plot fades in over the transition, then sits fully shown.
            Assert.True(image.Opacity < 1, "plot appeared without a fade");
            Assert.True(PumpUntil(() => image.Opacity > 0.999 && loading.Opacity < 0.001, 2000), "fade never finished");

            Assert.Equal(cardBefore, host.Card!.Bounds.Size);
            Assert.Equal(frameBefore, frame.Bounds.Size);
            Assert.False(vm.IsBusy);
            Assert.DoesNotContain(VisibleTexts(win), t => t.Text!.Contains('%'));
        }
        finally { CloseIfOpen(win); }
    }

    [AvaloniaFact]
    public void SmallWindow_CardAndPlotScaleDown_ToFit()
    {
        EnsureAppStyles();
        var (win, _, _, host) = Open(width: 760, height: 560);
        try
        {
            PumpUntil(() => false, 30);
            var card = host.Card!.Bounds;
            Assert.True(card.Width <= 760 - 48 + 0.5, $"card {card.Width} wider than the window allows");
            Assert.True(card.Height <= 560 - 48 + 0.5, $"card {card.Height} taller than the window allows");
            Assert.True(card.X >= 0 && card.Y >= 0);
            // The plot frame keeps the composed image's aspect when scaled down.
            var slot = win.FindControl<Panel>("PlotSlot")!;
            Assert.Equal(SpectrogramViewModel.ComposedSize, slot.Bounds.Size);
            var viewbox = slot.GetVisualAncestors().OfType<Viewbox>().First();
            Assert.True(viewbox.Bounds.Width < SpectrogramViewModel.ComposedSize.Width);
        }
        finally { CloseIfOpen(win); }
    }

    [AvaloniaFact]
    public void LightTheme_TextOverThePlot_StaysLight()
    {
        EnsureAppStyles();
        var (win, _, fake, _) = Open(theme: ThemeVariant.Light);
        try
        {
            Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted));
            var label = win.FindControl<TextBlock>("ProgressLabel")!;
            Assert.Equal(Color.Parse("#E6FFFFFF"), AccentTestHarness.ColorOf(label.Foreground));
            Assert.Equal(Colors.Black, AccentTestHarness.ColorOf(win.FindControl<Border>("PlotFrame")!.Background));
        }
        finally { CloseIfOpen(win); }
    }

    // ── Closing stops the analysis ──

    [AvaloniaFact]
    public void Escape_MidAnalysis_CancelsAtOnce_AndClosesOnce()
    {
        EnsureAppStyles();
        var (win, vm, fake, host) = Open();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted));
            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);

            // The close animation is still playing, and the decode is already told to stop.
            Assert.True(host.IsClosing);
            Assert.True(win.IsVisible);
            Assert.True(fake.Token.IsCancellationRequested);
            Assert.True(SpinUntil(() => fake.SawCancel), "the decode never saw the cancel");

            Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
            PumpUntil(() => false, 250);
            Assert.Equal(1, closed);
            Assert.Null(vm.Image);
            Assert.False(vm.IsReady);
        }
        finally { CloseIfOpen(win); }
    }

    /// <summary>Alt+F4 / a direct Close() goes through Window.Closing too: the decode stops
    /// before the close animation, not when the window is finally gone.</summary>
    [AvaloniaFact]
    public void DirectClose_MidAnalysis_CancelsBeforeTheAnimationEnds()
    {
        EnsureAppStyles();
        var (win, vm, fake, host) = Open();
        try
        {
            Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted));
            win.Close();
            Assert.True(host.IsClosing);
            Assert.True(vm.IsCancelled);
            Assert.True(SpinUntil(() => fake.SawCancel));
            Assert.True(PumpUntil(() => !win.IsVisible, 2000));

            // Releasing the fake afterwards must not resurrect an image on the closed dialog.
            fake.Release.TrySetResult();
            PumpUntil(() => false, 100);
            Assert.Null(vm.Image);
        }
        finally { CloseIfOpen(win); }
    }

    [AvaloniaFact]
    public void NoFfmpeg_ShowsTheReason_InThePlot_NotTwice()
    {
        EnsureAppStyles();
        var vm = new SpectrogramViewModel(MakeTrack(), new StubConverter { Path = null }, new FakeAnalysis().Run);
        var win = new SpectrogramWindow(vm) { Width = 1300, Height = 900 };
        win.Show();
        try
        {
            Assert.True(PumpUntil(() => vm.HasError));
            PumpUntil(() => false, 30);
            var message = Loc.T("Spectrogram.NeedsFfmpeg");
            var shown = VisibleTexts(win).Where(t => t.Text == message).ToList();
            Assert.Single(shown);
            Assert.True(win.FindControl<Border>("PlotFrame")!.IsVisualAncestorOf(shown[0]));
            Assert.False(win.FindControl<ProgressBar>("ProgressBar")!.IsEffectivelyVisible);
        }
        finally { CloseIfOpen(win); }
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): PNGs of the pop-up over a busy owner,
    /// loading and ready, Dark and Light, for eyeballing. Saved under the system temp folder.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheSpectrogramDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
        {
            AccentTestHarness.WithAccent("#E74856", theme, () =>
            {
                var stripes = new StackPanel();
                var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
                for (var i = 0; i < 20; i++)
                    stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
                var owner = new Window { Width = 1300, Height = 900, Content = stripes, RequestedThemeVariant = theme };
                owner.Show();
                PumpUntil(() => false, 100);

                var fake = new FakeAnalysis();
                var vm = new SpectrogramViewModel(MakeTrack(), new StubConverter(), fake.Run);
                var win = new SpectrogramWindow(vm) { Width = 1300, Height = 900, RequestedThemeVariant = theme };
                _ = win.ShowDialog(owner);
                try
                {
                    var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                    Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropBitmap != null && fake.Started.Task.IsCompleted, 3000));
                    PumpUntil(() => false, 100);
                    var loading = Path.Combine(Path.GetTempPath(), $"noctis-spectrogram-loading-{name}.png");
                    win.CaptureRenderedFrame()!.Save(loading, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);

                    fake.Release.TrySetResult();
                    var image = win.FindControl<Image>("PlotImage")!;
                    Assert.True(PumpUntil(() => vm.IsReady && image.Opacity > 0.999, 3000));
                    PumpUntil(() => false, 100);
                    var ready = Path.Combine(Path.GetTempPath(), $"noctis-spectrogram-ready-{name}.png");
                    win.CaptureRenderedFrame()!.Save(ready, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                }
                finally
                {
                    CloseIfOpen(win);
                    owner.Close();
                }
            });
        }
    }

    // ── View model ──

    /// <summary>Bug: an untagged artist read " · Album" (SpectrogramViewModel.Subtitle only
    /// checked the album).</summary>
    [Fact]
    public void Subtitle_LeavesOutBlankParts()
    {
        var conv = new StubConverter();
        Assert.Equal("Album", new SpectrogramViewModel(new Track { Artist = "", Album = "Album" }, conv).Subtitle);
        Assert.Equal("Artist", new SpectrogramViewModel(new Track { Artist = "Artist", Album = " " }, conv).Subtitle);
        Assert.Equal("Artist · Album", new SpectrogramViewModel(new Track { Artist = "Artist", Album = "Album" }, conv).Subtitle);
    }

    [Theory]
    [InlineData("MPEG-4 Audio (alac)", "x.m4a", "ALAC")]
    [InlineData("MPEG-4 Audio (mp4a)", "x.m4a", "AAC")]
    [InlineData("Some Codec (xyz)", "x.bin", "XYZ")]
    [InlineData("Flac Audio", "x.flac", "FLAC")]
    [InlineData("MPEG Version 1 Audio, Layer 3", "x.mp3", "MP3")]
    [InlineData("Opus Version 1 Audio", "x.opus", "Opus")]
    [InlineData("", "C:/m/x.wav", "WAV")]
    [InlineData("DSD", "x.dsf", "DSD")]
    public void FormatChip_IsTheShortName(string codec, string path, string expected)
        => Assert.Equal(expected, SpectrogramViewModel.ShortFormat(codec, path));

    [Fact]
    public void Chips_LeaveOutUnknownFacts()
    {
        var chips = SpectrogramViewModel.BuildChips(new Track { Codec = "FLAC", FilePath = "x.flac", Duration = TimeSpan.FromMinutes(62) });
        Assert.Equal(new[] { "FLAC", "1:02:00" }, chips);
    }

    /// <summary>Bug: with no duration SpectrogramRenderer never reports a fraction
    /// (expectedBytes is 0), so the bar sat at 0 % for the whole decode. It runs indeterminate.</summary>
    [Fact]
    public void UnknownDuration_RunsTheBarIndeterminate()
    {
        var conv = new StubConverter();
        Assert.True(new SpectrogramViewModel(new Track { Duration = TimeSpan.Zero }, conv).IsProgressIndeterminate);
        Assert.False(new SpectrogramViewModel(new Track { Duration = TimeSpan.FromSeconds(5) }, conv).IsProgressIndeterminate);
    }

    private static bool SpinUntil(Func<bool> condition, int budgetMs = 2000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < budgetMs)
        {
            if (condition()) return true;
            Thread.Sleep(5);
        }
        return condition();
    }
}
