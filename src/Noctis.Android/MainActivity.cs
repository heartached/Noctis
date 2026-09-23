using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Content.Res;
using Android.OS;
using Android.Views;
using Avalonia.Android;
using Noctis.Services;

namespace Noctis.Android;

[Activity(
    Label = "Noctis",
    Theme = "@style/NoctisTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTask,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.UiMode | ConfigChanges.FontScale)]
public class MainActivity : AvaloniaMainActivity
{
    private const int PickFolderRequest = 4242;
    private const int PostNotificationsRequest = 4243;
    private const int CreateDocumentRequest = 4244;

    /// <summary>The live activity, for services that need to start system UI (the SAF picker).</summary>
    public static MainActivity? Current { get; private set; }

    private TaskCompletionSource<string?>? _pickFolder;
    private TaskCompletionSource<string?>? _createDocument;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        Current = this;
        base.OnCreate(savedInstanceState);
        // After base.OnCreate: AvaloniaActivity registers its OnBackPressedCallback there, and
        // this event is raised from it.
        BackRequested += OnBackRequested;
        RequestNotificationPermission();
    }

    /// <summary>
    /// Now Playing and Queue are full-screen overlays inside the one activity, not activities of
    /// their own, so the system default — finish() — makes Back from either look like the app
    /// quit. Hand the press to the shell first; it consumes one if an overlay is open, and
    /// leaving <see cref="AndroidBackRequestedEventArgs.Handled"/> false falls through to the
    /// platform default unchanged.
    ///
    /// This replaced an <c>OnBackPressed</c> override, which was wrong on Android 16: predictive
    /// back is enabled by default for apps targeting SDK 36 (net10.0-android resolves to 36), and
    /// the system then dispatches Back through OnBackInvokedDispatcher and never calls
    /// <c>onBackPressed()</c> at all — so Back would have closed the app instead of the overlay,
    /// silently, on every Android 16 device. AvaloniaActivity already owns both routes (an
    /// AndroidX OnBackPressedCallback, which ComponentActivity forwards to the system dispatcher
    /// on API 33+) and funnels both into this one event, so subscribing here is correct from
    /// minSdk 26 upwards without a version check of our own.
    /// </summary>
    private void OnBackRequested(object? sender, AndroidBackRequestedEventArgs e)
    {
        if (AndroidApp.Current?.TryHandleBack() == true) e.Handled = true;
    }

    /// <summary>
    /// Asks for POST_NOTIFICATIONS, which from API 33 is a runtime permission: declaring it in
    /// the manifest is not enough, and without it Media3's media notification and the
    /// lock-screen card it backs are silently dropped. The activity is the only place that can
    /// ask — a service cannot show the dialog — and this is the earliest point it exists.
    /// Refusal is not handled beyond this: the service still runs in the foreground (a
    /// foreground service whose notification cannot be shown is still a foreground service),
    /// so playback and its process priority are unaffected, only the card is missing. Android
    /// itself auto-denies after two refusals, so re-asking on a later launch costs nothing and
    /// picks the permission up if the user grants it in Settings.
    /// </summary>
    private void RequestNotificationPermission()
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.Tiramisu) return;   // install-granted before 33
        try
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) == Permission.Granted) return;
            RequestPermissions(new[] { global::Android.Manifest.Permission.PostNotifications }, PostNotificationsRequest);
        }
        catch (Exception ex)
        {
            // Never take the app down over a notification: a permission request can throw if
            // the activity is already finishing when it lands here.
            DebugLog.Write("Android", $"POST_NOTIFICATIONS request failed: {ex.Message}");
        }
    }

    /// <summary>
    /// FontScale is in ConfigurationChanges so a system font-size change arrives here instead
    /// of recreating the activity; the lyrics page resizes from it (Avalonia itself ignores
    /// the system font scale). UiMode likewise: the dark-mode switch re-themes a "System"
    /// appearance in place.
    /// </summary>
    public override void OnConfigurationChanged(Configuration newConfig)
    {
        base.OnConfigurationChanged(newConfig);
        AndroidApp.Current?.ApplyFontScale(newConfig.FontScale);
        AndroidApp.Current?.OnConfigurationChanged();
    }

    /// <summary>
    /// The hardware volume keys change STREAM_MUSIC without telling the app; after the key is
    /// handled, have the Now Playing slider re-read it.
    /// </summary>
    public override bool DispatchKeyEvent(KeyEvent e)
    {
        var handled = base.DispatchKeyEvent(e);
        if (e.Action == KeyEventActions.Up && e.KeyCode is Keycode.VolumeUp or Keycode.VolumeDown or Keycode.VolumeMute)
            AndroidApp.Current?.OnVolumeKey();
        return handled;
    }

    protected override void OnResume()
    {
        base.OnResume();
        // Another app, or the notification shade, may have moved the volume while we were away.
        AndroidApp.Current?.OnVolumeKey();
    }

    protected override void OnPause()
    {
        base.OnPause();
        // Screen off / home / task switch: checkpoint the queue and position now, since
        // Android may kill the process without any further callback.
        AndroidApp.Current?.OnBackgrounded();
    }

    protected override void OnDestroy()
    {
        // A config change or low-memory kill while the system picker is foreground (our
        // ConfigurationChanges covers orientation, screen size and layout, UI mode and font scale; a locale or
        // density change still recreates us) recreates the activity before
        // OnActivityResult fires. That callback lands on the new instance, where
        // _pickFolder is null, so the old completion source would otherwise be abandoned
        // unresolved and the caller (LibraryViewModel.AddFolderAsync, an AsyncRelayCommand
        // that disallows concurrent execution) would stay stuck "running" forever.
        // Resolving with null here — before Current is cleared — keeps that button usable.
        _pickFolder?.TrySetResult(null);
        _pickFolder = null;
        // Same for a pending log export: its create-document result would land on the new
        // instance and leave Settings' ExportLogsCommand running forever.
        _createDocument?.TrySetResult(null);
        _createDocument = null;
        BackRequested -= OnBackRequested;
        if (ReferenceEquals(Current, this)) Current = null;
        base.OnDestroy();
    }

    /// <summary>Shows the system document-tree picker; resolves to the persisted tree URI or null.</summary>
    public Task<string?> PickFolderAsync()
    {
        _pickFolder?.TrySetResult(null);
        _pickFolder = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(Intent.ActionOpenDocumentTree);
        intent.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.GrantPersistableUriPermission);
        StartActivityForResult(intent, PickFolderRequest);
        return _pickFolder.Task;
    }

    /// <summary>Shows the system "save as" picker; resolves to the new document's URI or null.</summary>
    public Task<string?> CreateDocumentAsync(string fileName, string mimeType)
    {
        _createDocument?.TrySetResult(null);
        _createDocument = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(Intent.ActionCreateDocument);
        intent.AddCategory(Intent.CategoryOpenable);
        intent.SetType(mimeType);
        intent.PutExtra(Intent.ExtraTitle, fileName);
        StartActivityForResult(intent, CreateDocumentRequest);
        return _createDocument.Task;
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);
        if (requestCode == CreateDocumentRequest)
        {
            var pendingDocument = _createDocument;
            _createDocument = null;
            pendingDocument?.TrySetResult(resultCode == Result.Ok ? data?.Data?.ToString() : null);
            return;
        }
        if (requestCode != PickFolderRequest) return;

        // Take the field before doing anything else: if the activity was recreated mid-pick,
        // OnDestroy already resolved (and nulled) the old completion source, and this result
        // is landing on a fresh instance that never issued the request. In that case there is
        // no one left to hand the URI to, so the grant below must not be taken either.
        var pending = _pickFolder;
        _pickFolder = null;
        if (pending == null) return;

        var uri = resultCode == Result.Ok ? data?.Data : null;
        if (uri != null)
        {
            // Only taken when a URI is actually about to be returned: Android caps persisted
            // grants per app (128 pre-12, 512 on 12+), and taking one for a result that gets
            // discarded (see above) would slowly burn slots with nothing referencing them.
            // Without this the grant dies with the activity and the next launch's scan
            // finds an unreadable root.
            ContentResolver!.TakePersistableUriPermission(uri, ActivityFlags.GrantReadUriPermission);
        }
        pending.TrySetResult(uri?.ToString());
    }
}
