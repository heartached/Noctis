using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
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
using Avalonia.VisualTree;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Models;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.ViewModels;
using Noctis.Views;
using SkiaSharp;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Eyeball probe for the redesigned sign-in flows (owner 11-03): the desktop's Phone app card
/// rendered by the REAL SettingsView under Skia (Connect a phone by QR and by hand, Sync on and
/// off, Advanced open) and the phone's signed-out Account page. Runs only with NOCTIS_TEST_SKIA=1
/// and NOCTIS_PAIR_SHOTS=&lt;dir&gt;; starts a real server on loopback and a free port.
/// </summary>
public class AccountPairingShotsTests
{
    private readonly ITestOutputHelper _o;
    public AccountPairingShotsTests(ITestOutputHelper o) => _o = o;

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

    private sealed class NoOpHistory : IPlayHistoryService
    {
        public System.Collections.Generic.IReadOnlyList<PlayHistoryEvent> Events => Array.Empty<PlayHistoryEvent>();
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

    [AvaloniaFact]
    public async Task Shots_DesktopPhoneCard_AndPhoneSignIn()
    {
        var dir = Environment.GetEnvironmentVariable("NOCTIS_PAIR_SHOTS");
        if (!HeadlessTestApp.RealRendering || string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        await DesktopAsync(dir);
        PhoneSignIn(dir);
    }

    private async Task DesktopAsync(string dir)
    {
        var app = Application.Current!;
        if (!app.Resources.TryGetResource("HeartFillIcon", null, out _))
        {
            app.Resources["InterSemiBold"] = FontFamily.Default;
            app.Resources.MergedDictionaries.Add(new ResourceInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") });
            app.Styles.Add(new StyleInclude(new Uri("avares://Noctis/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") });
        }
        var root = Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));
        var persistence = new PersistenceService(root);
        var users = new ServerUserStore(Path.Combine(persistence.DataDirectory, "server", "users.db"));
        users.Create("ferlin@example.com", "correct horse", isAdmin: true);
        users.IssueDeviceKey("ferlin@example.com", "phone-0001", "samsung SM-S911U1");
        await persistence.SaveSettingsAsync(new AppSettings { NoctisServerEnabled = true, NoctisServerPort = FreePort(), SyncEnabled = true });
        var vm = new SettingsViewModel(persistence, new FakeLibraryService(), new NoOpHistory());
        try
        {
            await vm.LoadAsync();
            for (var i = 0; i < 200 && !vm.IsPhoneCardOpen; i++) { Dispatcher.UIThread.RunJobs(); await Task.Delay(50); }
            vm.SelectedSettingsTab = SettingsViewModel.TabAccountDevices;

            var window = new Window
            {
                Width = 1000, Height = 2600, RequestedThemeVariant = ThemeVariant.Dark,
                Background = new SolidColorBrush(Color.Parse("#141414")),
            };
            AccentTestHarness.WithAccent("#E74856", ThemeVariant.Dark, () =>
            {
                var view = new SettingsView { DataContext = vm };
                window.Content = view;
                window.Show();
                Pump(500);

                void Shot(string name)
                {
                    Pump(900);
                    var card = view.GetVisualDescendants().OfType<Border>().First(b => b.Name == "PhoneCard");
                    var origin = card.TranslatePoint(new Point(0, 0), window)!.Value;
                    var full = Path.Combine(dir, "_full.png");
                    window.CaptureRenderedFrame()!.Save(full);
                    var path = Path.Combine(dir, $"desktop-{name}.png");
                    Crop(full, path, new SKRectI((int)origin.X - 12, (int)origin.Y - 40,
                        (int)(origin.X + card.Bounds.Width) + 12, (int)(origin.Y + card.Bounds.Height) + 12));
                    File.Delete(full);
                    _o.WriteLine(path);
                }

                Shot("1-closed-sync-on");
                vm.TogglePairingCommand.Execute(null);
                Shot("2-connect-qr");
                vm.ShowManualPairingCommand.Execute(null);
                Shot("3-connect-manual");
                vm.TogglePairingCommand.Execute(null);
                vm.SyncEnabled = false;
                Shot("4-sync-off");
                vm.SyncEnabled = true;
                vm.TogglePhoneAdvancedCommand.Execute(null);
                Shot("5-advanced-open");
            });
            window.Close();
        }
        finally
        {
            await vm.StopNoctisServerAsync();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(root, true); } catch { }
        }
    }

    private sealed class Scanner : IQrScanner
    {
        public Task<QrScanResult> ScanAsync(CancellationToken ct = default) => Task.FromResult(new QrScanResult(QrScanOutcome.Cancelled));
    }

    private void PhoneSignIn(string dir)
    {
        var app = Application.Current!;
        try { FontManager.Current.AddFontCollection(new Avalonia.Fonts.Inter.InterFontCollection()); }
        catch (ArgumentException) { }
        var icons = new ResourceInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Noctis.Mobile/")) { Source = new Uri("avares://Noctis.UI/Assets/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        var theme = new MobileTheme();
        try
        {
            foreach (var name in new[] { "Light", "Dark" })
            {
                theme.Apply(app, name, MobileTheme.DefaultAccent);
                using var rig = MobileFixtures.MakeRig();
                var shell = new ShellViewModel(rig.Shell.Library, rig.Shell.Player, rig.Shell.Lyrics)
                {
                    Account = new MobileAccountTests.FakeAccount(),
                    Marshal = a => a(),
                    TintFactory = rig.Shell.TintFactory,
                    QrScanner = new Scanner(),
                };
                var window = MobileFixtures.Mount(shell, out _, 412, 1700);
                window.Background = Brushes.Transparent;
                try
                {
                    shell.OpenAccountCommand.Execute(null);
                    Pump(300);
                    Motion.FinishAll();
                    Dispatcher.UIThread.RunJobs();
                    var path = Path.Combine(dir, $"phone-signin-{name}.png");
                    window.CaptureRenderedFrame()!.Save(path);
                    _o.WriteLine(path);
                }
                finally { window.Close(); }
            }
        }
        finally
        {
            theme.Remove(app);
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
        }
    }

    private static void Crop(string source, string target, SKRectI rect)
    {
        using var bmp = SKBitmap.Decode(source);
        rect.Intersect(new SKRectI(0, 0, bmp.Width, bmp.Height));
        using var sub = new SKBitmap(rect.Width, rect.Height);
        bmp.ExtractSubset(sub, rect);
        using var img = SKImage.FromBitmap(sub);
        using var png = img.Encode(SKEncodedImageFormat.Png, 100);
        using var fs = File.OpenWrite(target);
        png.SaveTo(fs);
    }
}
