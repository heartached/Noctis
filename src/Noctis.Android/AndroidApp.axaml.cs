using Android.Content;
using Android.Content.PM;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Noctis.Android.Services;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Mobile.Services;
using Noctis.Mobile.ViewModels;
using Noctis.Mobile.Views;
using Noctis.Services;
using AApplication = Android.App.Application;
using AResources = Android.Content.Res.Resources;
using AUiMode = Android.Content.Res.UiMode;
using ALog = Android.Util.Log;

namespace Noctis.Android;

public partial class AndroidApp : Avalonia.Application, IThemeHost
{
    /// <summary>logcat tag for the mirrored <see cref="DebugLog"/>: `adb logcat -s Noctis`.</summary>
    private const string LogTag = "Noctis";

    private readonly MobileTheme _theme = new();
    private (string Appearance, string DarkTheme, string Accent) _themeChoice = ("System", "Ink", MobileTheme.DefaultAccent);
    private PlatformThemeVariant? _appliedSystem;
    private ShellViewModel? _shell;
    private Media3AudioPlayer? _player;
    private AndroidVolumeControl? _volume;

    /// <summary>
    /// The running app, for the activity's lifecycle hooks. Declared <c>new</c> on purpose:
    /// the inherited <see cref="Avalonia.Application.Current"/> is typed <c>Application?</c>,
    /// so without this MainActivity's <c>AndroidApp.Current?.OnBackgrounded()</c> would bind
    /// to the base member and fail to compile.
    /// </summary>
    public static new AndroidApp? Current { get; private set; }

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Current = this;
        var context = AApplication.Context;

        // Every Core service files its data under AppPaths.DataRoot; on Android that is the
        // app's private files dir. DataRoot resolves lazily and permanently on first read and
        // a later override with a different root throws, so this must precede the first
        // service construction — nothing above may touch PersistenceService, PlayHistoryService
        // or AppWrittenSidecarRegistry.
        AppPaths.OverrideDataRoot(context.FilesDir!.AbsolutePath);

        // DebugLog is an in-memory ring with no output of its own, so every handled error the
        // app logs — startup, library scan, playback, SAF listing — would be invisible on a
        // phone. Mirror it to logcat, where `adb logcat -s Noctis` can read it. AttachSink
        // replays what is already buffered, so attaching here loses nothing logged earlier;
        // the reset callback exists for the desktop's disk mirror and has no analogue here.
        DebugLog.AttachSink(line => ALog.Info(LogTag, line), static () => { });

        var persistence = new PersistenceService();
        var metadata = new MetadataService();
        var index = new SqliteLibraryIndexService(persistence);
        var audit = new AuditTrailService(persistence);
        var library = new LibraryService(metadata, persistence, index, audit, fileSystem: new AndroidFileSystemSource(context));
        var history = new PlayHistoryService();
        // The application context, never the activity: the player outlives the activity
        // (it keeps playing in the background service) and holding the activity leaks it.
        _player = new Media3AudioPlayer(context, library, persistence);

        _volume = new AndroidVolumeControl(context);
        var nowPlaying = new NowPlayingViewModel(_player, library, persistence, history, volume: _volume);
        var lyrics = new LyricsPageViewModel(_player, nowPlaying, new SafTrackFileAccess(context), persistence)
        {
            // Avalonia sizes by density only; the lyrics page applies the system font scale itself.
            FontScale = context.Resources?.Configuration?.FontScale ?? 1f,
        };
        var shell = new ShellViewModel(
            new LibraryViewModel(library, persistence, new AndroidFolderPicker(), history),
            nowPlaying,
            lyrics)
        {
            Outputs = new AndroidOutputSwitcher(context),
            Theme = this,
            Logs = new AndroidLogExporter(context),
            VersionText = DescribeVersion(context),
        };
        _shell = shell;

        // Notification / lock screen / Bluetooth / headset transport. The session player raises
        // these instead of seeking ExoPlayer's own item list, so every transport path runs
        // through PlaybackQueue; they arrive on a Java binder thread, hence the dispatcher hop.
        _player.SessionNextRequested += (_, _) => Dispatcher.UIThread.Post(() => shell.Player.NextCommand.Execute(null));
        _player.SessionPreviousRequested += (_, _) => Dispatcher.UIThread.Post(() => shell.Player.PreviousCommand.Execute(null));
        // Whether those buttons are drawn enabled. Read synchronously from the same Java
        // thread, so they stay plain field reads on the ViewModel (see NowPlayingViewModel.
        // HasNext/HasPrevious) — ExoPlayer's own item list is the wrong source because it
        // holds at most the current track plus one prepared successor.
        _player.HasNextInQueue = () => shell.Player.HasNext;
        _player.HasPreviousInQueue = () => shell.Player.HasPrevious;

        if (ApplicationLifetime is IActivityApplicationLifetime activity)
            activity.MainViewFactory = () => new ShellView { DataContext = shell };

        _ = StartAsync(persistence, library, history);

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Settings → language/theme, then the library and the saved queue. The shell is
    /// already on screen; it fills in as these land.</summary>
    private async Task StartAsync(IPersistenceService persistence, ILibraryService library, IPlayHistoryService history)
    {
        try
        {
            var settings = await persistence.LoadSettingsAsync();
            Loc.Instance.SetCulture(settings.Language);
            ApplyTheme(settings.MobileAppearance, settings.Theme, settings.AccentColorHex);
            _shell!.Player.SetGapless(settings.GaplessPlaybackEnabled);
            await history.PreloadAsync();
            await library.LoadAsync();
            await _shell.InitializeAsync();
        }
        catch (Exception ex)
        {
            DebugLog.Write("Startup", $"Android startup failed: {ex}");
        }
    }

    /// <summary>
    /// Activity paused: checkpoint the queue and position. Best-effort only — this is
    /// fire-and-forget and the process can be killed before the write lands. What actually
    /// bounds the loss is NowPlayingViewModel's five-second save cadence plus its save on
    /// pause; this call just shortens the window on the common home/screen-off path.
    /// </summary>
    public void OnBackgrounded()
    {
        var save = _shell?.SaveStateAsync();
        // Matches NowPlayingViewModel.SaveStateNow: a background save that throws would
        // otherwise be an unobserved exception nobody ever sees.
        _ = save?.ContinueWith(
            t => DebugLog.Write("Queue", $"Background save failed: {t.Exception?.GetBaseException().Message}"),
            TaskContinuationOptions.OnlyOnFaulted);
    }

    /// <summary>Activity Back: give the shell first refusal so a full-screen overlay closes
    /// instead of the activity finishing. See <see cref="ShellViewModel.TryHandleBack"/>.</summary>
    public bool TryHandleBack() => _shell?.TryHandleBack() ?? false;

    /// <summary>The system font size changed (MainActivity.OnConfigurationChanged).</summary>
    public void ApplyFontScale(float scale)
    {
        if (_shell != null) _shell.Lyrics.FontScale = scale;
    }

    /// <summary>A volume key or a resume (MainActivity): the media volume may have moved.</summary>
    public void OnVolumeKey() => _volume?.NotifyChanged();

    /// <summary>Settings (and startup, and a system dark-mode switch) re-theme through here.</summary>
    public void ApplyTheme(string appearance, string darkTheme, string accentHex)
    {
        _themeChoice = (appearance, darkTheme, accentHex);
        var system = SystemVariant();
        _appliedSystem = system;
        var resolved = MobileTheme.Resolve(appearance, darkTheme, system);
        DebugLog.Write("Theme", $"{appearance}/{darkTheme} on a {system} system -> {resolved}");
        _theme.Apply(this, resolved, accentHex);
    }

    /// <summary>
    /// "System" appearance: Android's dark-mode switch arrives as a configuration change
    /// (UiMode is in MainActivity's ConfigurationChanges, so the activity is kept and this runs
    /// from its OnConfigurationChanged); re-theme when the device's night mode moved. Avalonia's
    /// ColorValuesChanged is not used: its CONFIGURATION_CHANGED receiver never fired on the
    /// API 35 emulator (09-23).
    /// </summary>
    public void OnConfigurationChanged()
    {
        if (SystemVariant() == _appliedSystem) return;   // rotation, font scale, our own night-mode echo
        ApplyTheme(_themeChoice.Appearance, _themeChoice.DarkTheme, _themeChoice.Accent);
    }

    /// <summary>
    /// The device's night mode, from the system resources. Not the activity's configuration
    /// (nor PlatformSettings, which reads a context): Avalonia's TopLevelImpl.SetFrameThemeVariant
    /// pushes the app's own theme into the activity with AppCompat SetLocalNightMode, so after
    /// picking Light on a dark phone the activity reports "not night" and "System" would stay Light.
    /// </summary>
    private static PlatformThemeVariant SystemVariant() =>
        AResources.System?.Configuration is { } config && (config.UiMode & AUiMode.NightMask) == AUiMode.NightNo
            ? PlatformThemeVariant.Light
            : PlatformThemeVariant.Dark;

    private static string DescribeVersion(Context context)
    {
        try
        {
            var info = context.PackageManager!.GetPackageInfo(context.PackageName!, (PackageInfoFlags)0)!;
            return $"Noctis {info.VersionName}";
        }
        catch (Exception ex)
        {
            DebugLog.Write("Android", $"Version lookup failed: {ex.Message}");
            return "Noctis";
        }
    }
}
