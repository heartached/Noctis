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
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Localization;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 10-08: Convert Album as the rounded pill pop-up (blurred app behind, filled pill
/// fields, solid accent Convert, quiet Cancel), with less clutter (tag chips + a live example
/// name instead of the token wall, short option labels), plus the bugs found on the way.
/// No real ffmpeg: the converter is faked.
/// </summary>
public class AudioConverterDialogTests
{
    private readonly ITestOutputHelper _o;
    public AudioConverterDialogTests(ITestOutputHelper o) => _o = o;

    // ── Fakes ──

    /// <summary>Behaves like AudioConverterService at its edges: reports "Converting…" then
    /// a result per file; with <see cref="Block"/> it waits for the token and then reports the
    /// file the way the real service does when ffmpeg is killed ("Failed: cancelled",
    /// returning normally when it was the last file).</summary>
    private sealed class FakeConverter : IAudioConverterService
    {
        public bool HasFfmpeg = true;
        public bool Block;
        public Exception? Throw;
        public int Runs;
        public AudioConvertOptions? LastOptions;
        public IReadOnlyList<Track> LastTracks = Array.Empty<Track>();
        public CancellationToken LastToken;
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? OnRun;

        public string? GetFfmpegPath() => HasFfmpeg ? "ffmpeg" : null;
        public Task<string?> ValidateFfmpegAsync(string? path = null, CancellationToken ct = default) => Task.FromResult<string?>(null);

        public async Task<ConvertSummary> ConvertAsync(IReadOnlyList<Track> tracks, AudioConvertOptions options,
            IProgress<ConvertProgress> progress, CancellationToken ct)
        {
            Runs++;
            LastOptions = options;
            LastTracks = tracks;
            LastToken = ct;
            OnRun?.Invoke();
            var summary = new ConvertSummary();
            foreach (var t in tracks)
            {
                progress.Report(new ConvertProgress { Track = t, Status = "Converting…" });
                if (Throw != null) { Started.TrySetResult(); throw Throw; }
                if (Block)
                {
                    Started.TrySetResult();
                    try { await Task.Delay(Timeout.Infinite, ct); }
                    catch (OperationCanceledException)
                    {
                        // AudioConverterService.RunFfmpegAsync swallows the cancel into a failure.
                        progress.Report(new ConvertProgress { Track = t, Status = "Failed: cancelled", Done = true, Failed = true });
                        summary.Failed++;
                        continue;
                    }
                }
                var outPath = Path.ChangeExtension(t.FilePath, ".mp3");
                progress.Report(new ConvertProgress { Track = t, Status = "Done", Done = true, OutputPath = outPath });
                summary.Converted++;
                summary.OutputPaths.Add(outPath);
            }
            return summary;
        }
    }

    private static Track[] Tracks(int n = 2) => Enumerable.Range(1, n)
        .Select(i => new Track
        {
            Id = Guid.NewGuid(), Title = $"Song {i}", Artist = "Bad Bunny", AlbumArtist = "Bad Bunny",
            Album = "Album", TrackNumber = i, FilePath = TestPaths.Primary("Music", $"song{i}.flac"),
        })
        .ToArray();

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

    // ── The dialog ──

    [AvaloniaFact]
    public void Converter_OpensInPillHost_WithResolvedStyles_AndBoundActions()
    {
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var vm = new AudioConverterViewModel(Tracks(1), new FakeConverter(), new FakeLibraryService());
            var win = new AudioConverterDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            win.Show();
            var closed = 0;
            win.Closed += (_, _) => closed++;
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropLayer!.Opacity > 0.999),
                    "open animation never settled");
                Assert.Equal(new CornerRadius(30), host.CornerRadius);
                Assert.Equal("Convert 1 track", vm.TitleText);

                var all = win.GetVisualDescendants().ToList();
                // The old outlined / accent-btn chrome is gone.
                Assert.DoesNotContain(all.OfType<Button>(), b => b.Classes.Contains("accent-btn") || b.Classes.Contains("text-btn"));
                Assert.DoesNotContain(all.OfType<TextBlock>(), t => t.Text == Loc.T("AudioConverter.TokensArtistAlbumartistAlbum"));

                // Filled pill fields (output folder + pattern), resolved from PillDialog.axaml.
                var fields = all.OfType<TextBox>().Where(t => t.Classes.Contains("pill-field")).ToList();
                Assert.Equal(2, fields.Count);
                foreach (var field in fields)
                {
                    var chrome = field.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PART_BorderElement");
                    Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(chrome.Background));
                    Assert.Equal(new CornerRadius(999), chrome.CornerRadius);
                }
                // Format + quality: pill ComboBoxes; only the bitrate one shows for MP3.
                var combos = all.OfType<ComboBox>().ToList();
                Assert.All(combos, c => Assert.Contains("pill-field", c.Classes));
                Assert.Equal(2, combos.Count(c => c.IsEffectivelyVisible));
                vm.SelectedFormat = "flac";
                PumpUntil(() => false, 30);
                Assert.Equal(2, combos.Count(c => c.IsEffectivelyVisible));
                Assert.True(combos.Single(c => ReferenceEquals(c.ItemsSource, vm.BitDepthOptions)).IsEffectivelyVisible);
                vm.SelectedFormat = "mp3";

                // Footer: Convert solid accent, Cancel quiet; both bound.
                var convert = all.OfType<Button>().Single(b => b.Command == vm.StartCommand);
                PillDialogHostTests.AssertSolidAccent(convert);
                Assert.True(convert.IsEffectivelyEnabled);
                var cancel = all.OfType<Button>().Single(b => b.Command == vm.CancelCommand);
                Assert.Contains("pill-secondary", cancel.Classes);
                Assert.Equal(Color.Parse("#1CFFFFFF"), AccentTestHarness.ColorOf(cancel.Background));
                Assert.Contains("pill-secondary", win.FindControl<Button>("BrowseButton")!.Classes);

                // Queue rows are filled wells with a state chip.
                var list = all.OfType<ItemsControl>().Single(ic => ReferenceEquals(ic.ItemsSource, vm.Jobs));
                Assert.Single(list.GetRealizedContainers());
                var row = list.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("pill-well"));
                Assert.Contains(row.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == Loc.T("AudioConverter.StatePending"));

                // Live example name follows the pattern and the format.
                Assert.Equal("Bad Bunny - Song 1.mp3", vm.ExampleName);
                Assert.Contains(win.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "Bad Bunny - Song 1.mp3" && t.IsEffectivelyVisible);

                // A tag chip appends its token, spaced from the token before it.
                var year = win.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.DataContext is AudioConverterViewModel.PatternToken { Token: "%year%" });
                Assert.Contains("pill-secondary", year.Classes);
                year.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("%artist% - %title% %year%", vm.FilenamePattern);
                vm.FilenamePattern = "%artist% - ";
                year.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal("%artist% - %year%", vm.FilenamePattern);

                // Esc closes like Cancel: animated, exactly once.
                win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                Assert.True(host.IsClosing);
                Assert.True(PumpUntil(() => closed > 0, 2000), "window never closed");
                PumpUntil(() => false, 250);
                Assert.Equal(1, closed);
            }
            finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
        });
    }

    /// <summary>Esc during a run stops the run and keeps the dialog (the rows show what
    /// happened); the next Esc closes it.</summary>
    [AvaloniaFact]
    public void Escape_DuringRun_StopsTheRun_ThenCloses()
    {
        EnsureAppStyles();
        var fake = new FakeConverter { Block = true };
        var vm = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService());
        var win = new AudioConverterDialog(vm) { Width = 1100, Height = 820 };
        win.Show();
        var closed = 0;
        win.Closed += (_, _) => closed++;
        try
        {
            var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
            Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 }));
            vm.StartCommand.Execute(null);
            Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted && vm.Jobs[0].IsWorking), "run never started");
            Assert.Equal(Loc.T("AudioConverter.Stop"), vm.CancelLabel);
            // Settings are locked while it runs.
            Assert.False(win.FindControl<TextBox>("PatternBox")!.IsEffectivelyEnabled);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => !vm.IsConverting), "run never stopped");
            Assert.False(host.IsClosing);
            Assert.Equal(0, closed);
            Assert.Equal(Loc.T("AudioConverter.Cancelled"), vm.StatusMessage);
            Assert.Equal(AudioConverterViewModel.JobState.Cancelled, vm.Jobs[0].State);
            Assert.Equal(Loc.T("AudioConverter.Close"), vm.CancelLabel);

            win.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            Assert.True(PumpUntil(() => closed > 0, 2000));
            PumpUntil(() => false, 250);
            Assert.Equal(1, closed);
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    /// <summary>Bug: Alt+F4 (any close but Cancel) left ffmpeg converting behind a closed
    /// dialog — nothing cancelled the run.</summary>
    [AvaloniaFact]
    public void ClosingTheWindow_DuringRun_CancelsTheConversion()
    {
        EnsureAppStyles();
        var fake = new FakeConverter { Block = true };
        var vm = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService());
        var win = new AudioConverterDialog(vm) { Width = 1100, Height = 820 };
        win.Show();
        try
        {
            vm.StartCommand.Execute(null);
            Assert.True(PumpUntil(() => fake.Started.Task.IsCompleted));
            win.Close();
            Assert.True(fake.LastToken.IsCancellationRequested, "closing the window left the conversion running");
            Assert.True(PumpUntil(() => !vm.IsConverting && !win.IsVisible));
        }
        finally { if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); } }
    }

    // ── View model bugs ──

    /// <summary>Bug: cancelling the only (or last) file read "Finished · 0 converted · 1
    /// failed" with a red row — the service turns a killed ffmpeg into a failure and returns
    /// normally when no file follows.</summary>
    [Fact]
    public async Task Cancel_OnTheOnlyTrack_ReadsCancelled_NotFinishedWithAFailure()
    {
        var fake = new FakeConverter { Block = true };
        var vm = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService());
        var run = vm.StartCommand.ExecuteAsync(null);
        await fake.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(AudioConverterViewModel.JobState.Working, vm.Jobs[0].State);

        vm.CancelCommand.Execute(null);
        await run.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(Loc.T("AudioConverter.Cancelled"), vm.StatusMessage);
        Assert.Equal(AudioConverterViewModel.JobState.Cancelled, vm.Jobs[0].State);
        Assert.False(vm.Jobs[0].IsFailedState);
        Assert.False(vm.IsConverting);
    }

    /// <summary>Bug: an exception from the run (an unwritable output folder throws out of
    /// Directory.CreateDirectory) left "Converting…" in the footer and the row spinning.</summary>
    [Fact]
    public async Task ServiceThrows_ShowsTheError_AndNothingKeepsSpinning()
    {
        var fake = new FakeConverter { Throw = new UnauthorizedAccessException("Access to the path 'X:\\out' is denied.") };
        var vm = new AudioConverterViewModel(Tracks(2), fake, new FakeLibraryService());

        await vm.StartCommand.ExecuteAsync(null);

        Assert.Equal(Loc.T("AudioConverter.FailedWith", "Access to the path 'X:\\out' is denied."), vm.StatusMessage);
        Assert.False(vm.IsConverting);
        Assert.DoesNotContain(vm.Jobs, j => j.IsWorking);
        Assert.Equal(AudioConverterViewModel.JobState.Failed, vm.Jobs[0].State);
        Assert.True(vm.StartCommand.CanExecute(null), "Convert stayed disabled after the failure");
    }

    /// <summary>Bug: the same track twice (a playlist holding it twice) converted it twice
    /// ("Song (2).mp3") and the second row never left "Pending" — both reports landed on the
    /// first row.</summary>
    [Fact]
    public async Task SameTrackTwice_ListedAndConvertedOnce()
    {
        var t = Tracks(1)[0];
        var fake = new FakeConverter();
        var vm = new AudioConverterViewModel(new[] { t, t }, fake, new FakeLibraryService());
        Assert.Single(vm.Jobs);
        Assert.Equal("Convert 1 track", vm.TitleText);

        await vm.StartCommand.ExecuteAsync(null);
        Assert.Single(fake.LastTracks);
        Assert.Equal(AudioConverterViewModel.JobState.Done, vm.Jobs[0].State);
        Assert.Equal(Loc.T("AudioConverter.Finished", 1), vm.StatusMessage);
    }

    /// <summary>Bug: literal characters in the pattern are not sanitized (only tag values
    /// are), so "%artist%: %title%" reached ffmpeg as "Artist: Title.mp3" — on NTFS an
    /// alternate data stream of a file called "Artist". Convert is now off with a hint.</summary>
    [Fact]
    public void PatternWithInvalidCharacters_BlocksConvert_WithAHint()
    {
        var fake = new FakeConverter();
        var vm = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService());
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("Bad Bunny - Song 1.mp3", vm.ExampleName);

        vm.FilenamePattern = "%artist%: %title%";
        Assert.True(vm.HasPatternError);
        Assert.Equal(Loc.T("AudioConverter.PatternInvalid"), vm.PatternError);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(string.Empty, vm.ExampleName);

        vm.FilenamePattern = "%tracknumber2% %title%";
        Assert.False(vm.HasPatternError);
        Assert.True(vm.StartCommand.CanExecute(null));
        Assert.Equal("01 Song 1.mp3", vm.ExampleName);
        vm.SelectedFormat = "alac";
        Assert.Equal("01 Song 1.m4a", vm.ExampleName);
    }

    /// <summary>Bug: a typed relative folder ("Music") resolved against the app's working
    /// directory, so files landed somewhere the user never chose.</summary>
    [Fact]
    public async Task RelativeOutputFolder_BlocksConvert_FullPathPasses()
    {
        var fake = new FakeConverter();
        var vm = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService());
        vm.OutputFolder = "Music";
        Assert.True(vm.HasFolderError);
        Assert.False(vm.StartCommand.CanExecute(null));
        await vm.StartCommand.ExecuteAsync(null); // the guard holds even when invoked directly
        Assert.Equal(0, fake.Runs);

        var full = TestPaths.Primary("Converted");
        vm.OutputFolder = "  " + full + " ";
        Assert.False(vm.HasFolderError);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(full, fake.LastOptions!.OutputFolder);
    }

    /// <summary>Bug: "Embed artwork" stayed ticked for formats that can't hold a cover
    /// (WAV, Ogg, Opus…), where the service silently drops it.</summary>
    [Fact]
    public async Task EmbedArtwork_ShowsOff_ForFormatsWithoutCovers_AndKeepsThePreference()
    {
        var fake = new FakeConverter();
        var vm = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService());
        Assert.True(vm.EmbedArtworkEffective);
        vm.SelectedFormat = "ogg";
        Assert.False(vm.ArtworkApplies);
        Assert.False(vm.EmbedArtworkEffective);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.False(fake.LastOptions!.EmbedArtwork);
        vm.SelectedFormat = "flac";
        Assert.True(vm.EmbedArtworkEffective);
    }

    /// <summary>Bug: WAV/AIFF offered "Auto" bit depth, which the encoder writes as 16-bit —
    /// a 24-bit source lost its depth under a label that promised the source's. They get an
    /// explicit 16/24 choice, 24 when the selection holds a hi-res file.</summary>
    [Fact]
    public async Task WavAndAiff_OfferExplicitBitDepth_DefaultingToTheSourceDepth()
    {
        var fake = new FakeConverter();
        var tracks = Tracks(2);
        tracks[1].BitsPerSample = 24;
        var vm = new AudioConverterViewModel(tracks, fake, new FakeLibraryService());

        vm.SelectedFormat = "wav";
        Assert.True(vm.PcmBitDepthApplies);
        Assert.False(vm.BitDepthApplies);
        Assert.False(vm.BitrateApplies);
        Assert.DoesNotContain("Auto", vm.PcmBitDepthOptions);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("24", fake.LastOptions!.BitDepth);

        vm.SelectedFormat = "flac";
        Assert.True(vm.BitDepthApplies);
        Assert.False(vm.PcmBitDepthApplies);
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal("Auto", fake.LastOptions!.BitDepth);

        var cd = new AudioConverterViewModel(Tracks(1), fake, new FakeLibraryService()) { SelectedFormat = "aiff" };
        Assert.Equal("16", cd.SelectedPcmBitDepth);
    }

    /// <summary>Bug: a second run (another format, say) kept the first run's Done/Failed on
    /// every row until its turn came round.</summary>
    [Fact]
    public async Task SecondRun_StartsFromCleanRows()
    {
        var fake = new FakeConverter();
        var vm = new AudioConverterViewModel(Tracks(2), fake, new FakeLibraryService());
        await vm.StartCommand.ExecuteAsync(null);
        Assert.All(vm.Jobs, j => Assert.Equal(AudioConverterViewModel.JobState.Done, j.State));
        Assert.Equal(Loc.T("AudioConverter.Close"), vm.CancelLabel);

        AudioConverterViewModel.JobState? secondRowAtStart = null;
        fake.OnRun = () => secondRowAtStart = vm.Jobs[1].State;
        vm.SelectedFormat = "flac";
        await vm.StartCommand.ExecuteAsync(null);
        Assert.Equal(AudioConverterViewModel.JobState.Pending, secondRowAtStart);
    }

    [Fact]
    public void NoFfmpeg_DisablesConvert_AndSaysWhy()
    {
        var vm = new AudioConverterViewModel(Tracks(3), new FakeConverter { HasFfmpeg = false }, new FakeLibraryService());
        Assert.Equal("Convert 3 tracks", vm.TitleText);
        Assert.False(vm.StartCommand.CanExecute(null));
        Assert.Equal(Loc.T("AudioConverter.NoFfmpeg"), vm.StatusMessage);
    }

    /// <summary>Real Skia only (NOCTIS_TEST_SKIA=1): a PNG of the dialog over a busy owner,
    /// for eyeballing the layout. Saved under the system temp folder.</summary>
    [AvaloniaFact]
    public void Probe_SavesTheConverterDialog()
    {
        if (!HeadlessTestApp.RealRendering)
            Assert.Skip("needs real Skia rendering (NOCTIS_TEST_SKIA=1)");
        EnsureAppStyles();
        AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
        {
            var stripes = new StackPanel();
            var colors = new[] { "#E74856", "#2D7DD2", "#F4D35E", "#3BB273", "#7B2CBF", "#FF8C42" };
            for (var i = 0; i < 18; i++)
                stripes.Children.Add(new Border { Height = 50, Background = new SolidColorBrush(Color.Parse(colors[i % colors.Length])) });
            var owner = new Window { Width = 1100, Height = 820, Content = stripes, RequestedThemeVariant = ThemeVariant.Dark };
            owner.Show();
            PumpUntil(() => false, 100);

            var vm = new AudioConverterViewModel(Tracks(4), new FakeConverter(), new FakeLibraryService());
            var win = new AudioConverterDialog(vm) { RequestedThemeVariant = ThemeVariant.Dark, Width = 1100, Height = 820 };
            _ = win.ShowDialog(owner);
            try
            {
                var host = win.GetVisualDescendants().OfType<PillDialogHost>().Single();
                Assert.True(PumpUntil(() => host.Card is { Opacity: > 0.999 } && host.BackdropBitmap != null, 3000));
                vm.Jobs[0].Apply(new ConvertProgress { Track = vm.Jobs[0].Track, Status = "Done", Done = true });
                vm.Jobs[1].Apply(new ConvertProgress { Track = vm.Jobs[1].Track, Status = "Converting…" });
                vm.Jobs[2].Apply(new ConvertProgress { Track = vm.Jobs[2].Track, Status = "Skipped (exists)", Done = true });
                PumpUntil(() => false, 150);
                var path = Path.Combine(Path.GetTempPath(), "noctis-audio-converter-dialog.png");
                win.CaptureRenderedFrame()!.Save(path, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                _o.WriteLine(path);
            }
            finally
            {
                if (win.IsVisible) { win.Close(); PumpUntil(() => !win.IsVisible); }
                owner.Close();
            }
        });
    }
}
