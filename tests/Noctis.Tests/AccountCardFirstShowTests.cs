using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Controls;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-26: "when i open [Account &amp; Devices], for a split second it shows the create
/// account part but i already have an account." The page's account card picks between the
/// create form and the signed-in card from <see cref="SettingsViewModel.HasPrimaryAccount"/>;
/// neither card may show (or glide) for a frame when the other is the real state.
/// </summary>
public class AccountCardFirstShowTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_root, true); } catch { }
    }

    private sealed class NoOpHistory : IPlayHistoryService
    {
        public IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
        public Task PreloadAsync() => Task.CompletedTask;
        public void RecordPlay(Track track) { }
        public void RecordSkip(Track track) { }
        public Task FlushAsync() => Task.CompletedTask;
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>The fold around a named control, and whether it takes room on screen.</summary>
    private static bool Shows(CollapsibleContent fold) =>
        fold.IsEffectivelyVisible && fold.Bounds.Height > 0.5 && fold.Opacity > 0.01;

    private static CollapsibleContent FoldOf(SettingsView view, string name)
    {
        Avalonia.StyledElement? at = view.FindControl<Control>(name);
        while (at is not null and not CollapsibleContent) at = at.Parent;
        return (CollapsibleContent)at!;
    }

    public static readonly TheoryData<bool, bool, bool> Cases = new()
    {
        // account, phone server on, page warmed before the click
        { true, false, false },
        { true, false, true },
        { true, true, false },
        { true, true, true },
        { false, false, false },
        { false, false, true },
    };

    [AvaloniaTheory]
    [MemberData(nameof(Cases))]
    public async Task AccountCard_ShowsTheRealStateFromTheFirstFrame(bool hasAccount, bool serverOn, bool warmed)
    {
        Assert.SkipUnless(AccountFeatures.Enabled, "account features are parked (AccountFeatures off)");
        var persistence = new PersistenceService(_root);
        if (hasAccount)
            new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db")).Create("me@example.com", "correct horse", isAdmin: true);
        await persistence.SaveSettingsAsync(new AppSettings { NoctisServerEnabled = serverOn, NoctisServerPort = FreePort() });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpHistory());
        var keep = SettingsView.WarmUpEnabled;
        SettingsView.WarmUpEnabled = false; // warmed by hand below
        EnsureAppStyles();
        var window = new Window { Width = 1000, Height = 1400 };
        try
        {
            await vm.LoadAsync();
            var view = new SettingsView { DataContext = vm };
            window.Content = view;
            window.Show();
            Pump(200);
            if (warmed)
            {
                while (view.RunWarmSliceForTests()) Dispatcher.UIThread.RunJobs();
                Pump(200);
            }

            vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;
            var create = FoldOf(view, "CreateAccountForm");
            var actions = FoldOf(view, "AccountActions");
            var wrong = hasAccount ? create : actions;
            var right = hasAccount ? actions : create;

            var frames = 0;
            var sw = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();
            while (sw.ElapsedMilliseconds < 700)
            {
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                frames++;
                Assert.False(Shows(wrong), $"frame {frames}: the {(hasAccount ? "create form" : "signed-in card")} showed (h={wrong.Bounds.Height:0.#}, opacity={wrong.Opacity:0.##})");
                // The real card is there at full size from the first frame: no glide in.
                Assert.True(Shows(right) && right.Reveal >= 0.999,
                    $"frame {frames}: the real card was not settled (reveal={right.Reveal:0.###})");
                Thread.Sleep(5);
            }
            Assert.Equal(hasAccount, vm.HasPrimaryAccount);
        }
        finally
        {
            window.Close();
            SettingsView.WarmUpEnabled = keep;
            await vm.StopNoctisServerAsync();
        }
    }

    private static void EnsureAppStyles()
    {
        var app = Avalonia.Application.Current!;
        if (app.Resources.TryGetResource("HeartFillIcon", null, out _)) return;
        app.Resources["InterSemiBold"] = Avalonia.Media.FontFamily.Default;
        app.Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
        app.Styles.Add(new Avalonia.Markup.Xaml.Styling.StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
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
}
