using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Rendering;
using Avalonia.Threading;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Models;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › Appearance › Smooth Motion (beta tester: "no 120 Hz option, not snappy"): on, the
/// app asks for the screen's fastest mode at its resolution; off (Standard), for 60 Hz. The
/// choice is saved and reaches the platform host; an idle screen asks Avalonia for no frames,
/// so asking for 120 Hz costs nothing while nothing moves.
/// </summary>
public class MobileSmoothMotionTests
{
    private sealed class FakeFrameRate : IFrameRateHost
    {
        public List<bool> Requests { get; } = new();
        public void Request(bool smoothMotion) => Requests.Add(smoothMotion);
    }

    // A Galaxy S23-like panel: FHD+ and QHD+, each at 60 and 120 Hz, plus 48 and 96 at FHD+.
    private static readonly DisplayModeInfo[] Modes =
    {
        new(1, 1080, 2340, 60f), new(2, 1080, 2340, 120f), new(3, 1440, 3120, 60f),
        new(4, 1440, 3120, 120f), new(5, 1080, 2340, 48f), new(6, 1080, 2340, 96f),
    };

    [Fact]
    public void SmoothMotion_PicksTheFastestModeAtTheCurrentResolution()
    {
        var fhd = FrameRatePolicy.PickMode(Modes, Modes[0], smoothMotion: true);
        Assert.Equal(2, fhd!.Value.Id);
        Assert.Equal(120f, FrameRatePolicy.SurfaceRate(fhd, smoothMotion: true));

        // At QHD+ the QHD+ 120 Hz mode, never a mode that would rescale the screen.
        Assert.Equal(4, FrameRatePolicy.PickMode(Modes, Modes[2], smoothMotion: true)!.Value.Id);
    }

    [Fact]
    public void Standard_AsksFor60()
    {
        var mode = FrameRatePolicy.PickMode(Modes, Modes[1], smoothMotion: false);
        Assert.Equal(1, mode!.Value.Id);
        Assert.Equal(60f, FrameRatePolicy.SurfaceRate(mode, smoothMotion: false));

        // No 60 Hz mode: the nearest, the slower one on a tie.
        var odd = new DisplayModeInfo[] { new(1, 720, 1600, 90f), new(2, 720, 1600, 48f), new(3, 720, 1600, 72f) };
        Assert.Equal(2, FrameRatePolicy.PickMode(odd, odd[0], smoothMotion: false)!.Value.Id);
        Assert.Equal(60f, FrameRatePolicy.SurfaceRate(null, smoothMotion: false));
    }

    [Fact]
    public void NoModeAtTheCurrentSize_AsksForNoMode_AndCastsNoSmoothVote()
    {
        var current = new DisplayModeInfo(9, 800, 600, 60f);
        Assert.Null(FrameRatePolicy.PickMode(Modes, current, smoothMotion: true));
        Assert.Equal(0f, FrameRatePolicy.SurfaceRate(null, smoothMotion: true));
    }

    [Fact]
    public void Setting_IsOnByDefault_AlsoForAnOlderSettingsFile()
    {
        Assert.True(new AppSettings().MobileSmoothMotion);
        Assert.True(JsonSerializer.Deserialize<AppSettings>("{}")!.MobileSmoothMotion);
    }

    /// <summary>The switch reaches the host at once and is saved; the next Settings shows the saved
    /// value without asking the host again (the app applied it at start).</summary>
    [AvaloniaFact]
    public async Task Switch_ReachesTheHost_AndPersists()
    {
        var host = new FakeFrameRate();
        using var rig = MobileFixtures.MakeRig(frameRate: host);
        var shell = rig.Shell;
        var window = MobileFixtures.Mount(shell, out _);
        try
        {
            shell.OpenSettingsCommand.Execute(null);
            var vm = Assert.IsType<SettingsPageViewModel>(shell.CurrentPage);
            await vm.Loaded;
            MobileFixtures.Land(window);
            Assert.True(vm.SmoothMotion);
            Assert.Empty(host.Requests);

            var page = MobileFixtures.PageView((Noctis.Mobile.Views.ShellView)window.Content!, vm);
            var toggle = MobileFixtures.Named<ToggleButton>(page, "SmoothMotionSwitch");
            Assert.True(toggle.IsEffectivelyVisible);
            Assert.Equal("Smooth Motion", Avalonia.Automation.AutomationProperties.GetName(toggle));
            Assert.True(MobileFixtures.Named<TextBlock>(page, "SmoothMotionFootnoteLabel").IsEffectivelyVisible);

            toggle.IsChecked = false;
            await vm.PendingSmoothMotionSave;
            Assert.Equal(new[] { false }, host.Requests);
            Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileSmoothMotion);

            var again = new SettingsPageViewModel(shell);
            await again.LoadAsync();
            Assert.False(again.SmoothMotion);
            Assert.Equal(new[] { false }, host.Requests);   // shown, not re-applied
            again.OnClosed();

            vm.SmoothMotion = true;
            await vm.PendingSmoothMotionSave;
            Assert.Equal(new[] { false, true }, host.Requests);
            Assert.True((await rig.Persistence.LoadSettingsAsync()).MobileSmoothMotion);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>A tap before the saved value was read is kept, not overwritten by the load.</summary>
    [AvaloniaFact]
    public async Task ATapBeforeTheLoad_IsKept()
    {
        var host = new FakeFrameRate();
        using var rig = MobileFixtures.MakeRig(frameRate: host, seed: p => p.UpdateSettingsAsync(s => s.MobileSmoothMotion = true));
        var vm = new SettingsPageViewModel(rig.Shell);
        vm.SmoothMotion = false;
        await vm.LoadAsync();
        await vm.PendingSmoothMotionSave;
        Assert.False(vm.SmoothMotion);
        Assert.False((await rig.Persistence.LoadSettingsAsync()).MobileSmoothMotion);
        vm.OnClosed();
    }

    private static readonly FieldInfo Running =
        typeof(IRenderLoop).Assembly.GetType("Avalonia.Rendering.DefaultRenderLoop")!
            .GetField("_running", BindingFlags.Instance | BindingFlags.NonPublic)!;

    // AvaloniaLocator.Current is private API, hidden from the reference assembly.
    private static object RenderLoop
    {
        get
        {
            var locator = typeof(AvaloniaLocator).GetProperty("Current", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null)!;
            return locator.GetType().GetMethod("GetService", new[] { typeof(Type) })!.Invoke(locator, new object[] { typeof(IRenderLoop) })!;
        }
    }

    private static bool LoopRunning() => (bool)Running.GetValue(RenderLoop)!;

    /// <summary>
    /// The idle screen draws nothing: once the first frames are out, Avalonia's render loop puts
    /// its timer to sleep (on Android: no Choreographer callbacks, no buffers, so a 120 Hz request
    /// keeps nothing busy) and nothing wakes it over two seconds of real time with the dispatcher
    /// running every timer the app has (Home, then Settings, nothing playing).
    /// </summary>
    [AvaloniaFact]
    public void IdleScreens_ScheduleNoFrames()
    {
        using var rig = MobileFixtures.MakeRig(new[] { MobileFixtures.Song("S0"), MobileFixtures.Song("S1"), MobileFixtures.Song("S2") });
        var window = MobileFixtures.Mount(rig.Shell, out _);
        try
        {
            Assert.Equal(0, AwakeSamplesWhileIdle(window));
            rig.Shell.OpenSettingsCommand.Execute(null);
            Assert.Equal(0, AwakeSamplesWhileIdle(window));
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>Settles the screen (a second of real time for the mount's own frames and finite
    /// transitions, such as Settings' switches moving to their loaded values), then samples the
    /// render loop every 10 ms for two seconds.</summary>
    private static int AwakeSamplesWhileIdle(Window window)
    {
        MobileFixtures.Land(window);
        RunFor(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 120 && LoopRunning(); i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Assert.False(LoopRunning(), "the render loop never went to sleep on an idle screen");

        var awake = 0;
        var sampler = new DispatcherTimer(TimeSpan.FromMilliseconds(10), DispatcherPriority.Background, (_, _) =>
        {
            if (LoopRunning()) awake++;
        });
        sampler.Start();
        RunFor(TimeSpan.FromSeconds(2));
        sampler.Stop();
        return awake;
    }

    /// <summary>Runs the dispatcher, timers included, for <paramref name="time"/> of real time.</summary>
    private static void RunFor(TimeSpan time)
    {
        var frame = new DispatcherFrame();
        DispatcherTimer.RunOnce(() => frame.Continue = false, time);
        Dispatcher.UIThread.PushFrame(frame);
    }
}
