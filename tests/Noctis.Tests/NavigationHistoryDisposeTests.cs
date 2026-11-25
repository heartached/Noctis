using System.Reflection;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Services;
using Noctis.ViewModels;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A page left through the sidebar is kept for Back: it must not be disposed on the way out.
/// Navigate used to switch CurrentView (which disposes the outgoing view unless history holds
/// it) before pushing that view into history, so the page Back restored was already disposed,
/// and when it aged out of the 30-entry history it was disposed again: an artist page threw
/// ObjectDisposedException out of Navigate there (memory harness, 11-14).
/// </summary>
public class NavigationHistoryDisposeTests
{
    private static MainWindowViewModel BuildShell()
    {
        var services = new ServiceCollection();
        var program = typeof(App).Assembly.GetType("Noctis.Program")!;
        program.GetMethod("ConfigureServices", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [services]);
        services.AddSingleton<IAudioPlayer>(new FakeAudioPlayer());
        var provider = services.BuildServiceProvider();
        App.Services = provider;
        return provider.GetRequiredService<MainWindowViewModel>();
    }

    private static void Navigate(MainWindowViewModel vm, string key)
    {
        typeof(MainWindowViewModel).GetMethod("Navigate", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [key]);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsDisposed(ArtistDetailViewModel page) =>
        ((CancellationTokenSource)typeof(ArtistDetailViewModel)
            .GetField("_cts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!).IsCancellationRequested;

    [AvaloniaFact]
    public void ArtistPage_LeftThroughSidebar_StaysUsableForBack_AndAgesOutOfHistoryOnce()
    {
        var vm = BuildShell();
        try
        {
            vm.OpenArtistByName("Somebody");
            Dispatcher.UIThread.RunJobs();
            var page = Assert.IsType<ArtistDetailViewModel>(vm.CurrentView);

            Navigate(vm, "songs");
            Assert.False(IsDisposed(page)); // was disposed here, then kept for Back

            vm.GoBackInHistoryCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(page, vm.CurrentView);

            // Out again, then far enough that it leaves the 30-entry history: one Dispose, no throw.
            Navigate(vm, "songs");
            var keys = new[] { "home", "albums", "artists", "playlists" };
            for (var i = 0; i < 40; i++)
                Navigate(vm, keys[i % keys.Length]);
            Assert.True(IsDisposed(page));
        }
        finally
        {
            (App.Services as IDisposable)?.Dispose();
            App.Services = null;
        }
    }

    /// <summary>An artist page opened from Home highlights Artists; Back to it (from an album
    /// or another section) highlighted Home again, the section it was opened from (11-24).</summary>
    [AvaloniaFact]
    public void BackToAnArtistPage_HighlightsArtists_NotTheSectionItWasOpenedFrom()
    {
        var vm = BuildShell();
        try
        {
            Navigate(vm, "home");
            vm.OpenArtistByName("Somebody");
            Dispatcher.UIThread.RunJobs();
            var page = Assert.IsType<ArtistDetailViewModel>(vm.CurrentView);
            Assert.Equal("artists", vm.Sidebar.SelectedNavItem?.Key);

            Navigate(vm, "songs");
            Assert.Equal("songs", vm.Sidebar.SelectedNavItem?.Key);

            vm.GoBackInHistoryCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Same(page, vm.CurrentView);
            Assert.Equal("artists", vm.Sidebar.SelectedNavItem?.Key);

            vm.GoForwardInHistoryCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("songs", vm.Sidebar.SelectedNavItem?.Key);
        }
        finally
        {
            (App.Services as IDisposable)?.Dispose();
            App.Services = null;
        }
    }
}
