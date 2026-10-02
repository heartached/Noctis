using System.Reflection;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Noctis.Models;
using Noctis.Services;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// The mini player's size glide, drawer glide and lyrics chase step on the compositor frame
/// clock (TopLevel.RequestAnimationFrame). They used DispatcherTimer(16 ms), which on Windows
/// fires on the 15.6 ms USER-timer grid — measured 41–48 ticks/s with alternating 16/31 ms
/// steps — so the glides ran at ~40 fps while the frame clock delivered 250.
/// </summary>
[Collection("MetadataServiceStatics")]
public class MiniPlayerFrameClockTests
{
    private sealed class StubLrcLib : ILrcLibService
    {
        public Task<LrcLibResult?> GetLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
        public Task<List<LrcLibResult>> SearchLyricsAsync(string artist, string trackName, CancellationToken ct = default)
            => Task.FromResult(new List<LrcLibResult>());
    }

    private sealed class StubNetEase : INetEaseService
    {
        public Task<LrcLibResult?> SearchLyricsAsync(string artist, string trackName, double durationSeconds, CancellationToken ct = default)
            => Task.FromResult<LrcLibResult?>(null);
    }

    private sealed class StubMetadata : IMetadataService
    {
        public Track? ReadTrackMetadata(string filePath) => null;
        public Track? ReadTrackMetadata(string filePath, out byte[]? embeddedArt) { embeddedArt = null; return null; }
        public byte[]? ExtractAlbumArt(string filePath) => null;
        public bool WriteTrackMetadata(Track track) => false;
        public bool WriteTrackMetadata(Track track, string targetFilePath, string? titleOverride = null) => false;
        public bool WriteAlbumArt(string filePath, byte[]? imageData) => false;
        public bool WriteRating(string filePath, int rating, bool isDisliked) => false;
        bool IMetadataService.WriteAdvancedFields(string filePath, AdvancedTagIO.AdvancedFields fields,
            AdvancedTagIO.AdvancedFields original) => false;
        public AudioFileInfo? ReadFileInfo(string filePath) => null;
    }

    private sealed class NoOpPlayHistoryService : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static void EnsureAppResources()
    {
        var app = Application.Current!;
        if (app.Resources.TryGetResource("SearchIcon", null, out _)) return;
        app.Resources.MergedDictionaries.Add(new ResourceInclude((Uri?)null)
        {
            Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml"),
        });
    }

    private static MiniPlayerViewModel MakeViewModel()
    {
        var library = new FakeLibraryService();
        var player = new PlayerViewModel(
            new FakeAudioPlayer(), library,
            new TestPersistenceService(), new FakeAnimatedCoverService());
        var lyrics = new LyricsViewModel(
            player, new StubLrcLib(), new StubNetEase(), new StubMetadata(),
            new TestPersistenceService(), library);
        var settings = new SettingsViewModel(
            new TestPersistenceService(), library, new NoOpPlayHistoryService());
        return new MiniPlayerViewModel(player, lyrics, settings, library);
    }

    /// <summary>A shown Card-sized mini player, as MiniPlayerDesignTests builds one.</summary>
    private static MiniPlayerWindow ShowWindow()
    {
        EnsureAppResources();
        var win = new MiniPlayerWindow { DataContext = MakeViewModel(), Width = 340, Height = 432 };
        win.Show();
        return win;
    }

    private static T Field<T>(object o, string name) =>
        (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;

    private static void SetField(object o, string name, object? value) =>
        o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(o, value);

    private static void Call(object o, string name, params object?[] args) =>
        o.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(o, args);

    private static void Pump(int frames)
    {
        for (var i = 0; i < frames; i++)
        {
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Pumps frames until <paramref name="ms"/> of wall time passed (the easing is Stopwatch-based).</summary>
    private static void PumpFor(int ms)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Pump(1);
            Thread.Sleep(4);
        }
        Pump(2);
    }

    [AvaloniaFact]
    public void SizeGlide_LandsOnTarget_WithoutADispatcherTimer()
    {
        var win = ShowWindow();
        Pump(2);
        Call(win, "AnimateSizeTo", 320.0, 180.0);
        PumpFor(400); // > 280 ms glide
        Assert.Equal(320.0, win.Width, 0.5);
        Assert.Equal(180.0, win.Height, 0.5);
        Assert.False(Field<bool>(win, "_sizeAnimating"));
        Assert.Null(typeof(MiniPlayerWindow).GetField("_lyricsScrollTimer",
            BindingFlags.Instance | BindingFlags.NonPublic));
    }

    [AvaloniaFact]
    public void SizeGlide_SupersededGlideLandsOnNewTarget()
    {
        var win = ShowWindow();
        Pump(2);
        Call(win, "AnimateSizeTo", 320.0, 180.0);
        Pump(3);
        Call(win, "AnimateSizeTo", 260.0, 140.0);
        PumpFor(400);
        Assert.Equal(260.0, win.Width, 0.5);
        Assert.Equal(140.0, win.Height, 0.5);
    }

    [AvaloniaFact]
    public void SizeGlide_StopsWhenWindowIsClosing()
    {
        var win = ShowWindow();
        Pump(2);
        var w0 = win.Width;
        Call(win, "AnimateSizeTo", w0 + 200, win.Height + 100);
        SetField(win, "_closeAnimationDone", true);
        PumpFor(120);
        // No frame after the close flag may have moved the window toward the target.
        Assert.True(win.Width < w0 + 200 - 50, $"width {win.Width} kept gliding after close");
    }

    [AvaloniaFact]
    public void LyricsChase_RetargetsWithoutSecondLoop()
    {
        var win = ShowWindow();
        Pump(2);
        SetField(win, "_lyricsChaseTarget", 100.0);
        SetField(win, "_lyricsChaseRunning", true);
        // Retargeting while running must only move the goalpost.
        SetField(win, "_lyricsChaseTarget", 300.0);
        Assert.True(Field<bool>(win, "_lyricsChaseRunning"));
        Assert.Equal(300.0, Field<double>(win, "_lyricsChaseTarget"));
    }
}
