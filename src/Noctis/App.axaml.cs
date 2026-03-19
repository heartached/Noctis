using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Noctis.Services;
using Noctis.Views;
using Noctis.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace Noctis;

public partial class App : Application
{
    /// <summary>Global service provider, configured in Program.cs.</summary>
    public static IServiceProvider? Services { get; set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Global error capture — logs to DebugLogger when enabled
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            var ex = args.ExceptionObject as Exception;
            DebugLogger.Error(DebugLogger.Category.Error, "UnhandledException",
                $"terminating={args.IsTerminating}, msg={ex?.Message}");
        };

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            DebugLogger.Error(DebugLogger.Category.Error, "UnobservedTaskException",
                $"msg={args.Exception?.InnerException?.Message ?? args.Exception?.Message}");
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var mainVm = Services!.GetRequiredService<MainWindowViewModel>();

            desktop.MainWindow = new MainWindow
            {
                DataContext = mainVm
            };

            // Graceful shutdown: save state before exit
            desktop.ShutdownRequested += async (_, _) =>
            {
                await mainVm.ShutdownAsync();
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Switches the application theme between Dark and Light at runtime.
    /// </summary>
    public void SetTheme(bool isDark)
    {
        RequestedThemeVariant = isDark
            ? Avalonia.Styling.ThemeVariant.Dark
            : Avalonia.Styling.ThemeVariant.Light;
    }
}
