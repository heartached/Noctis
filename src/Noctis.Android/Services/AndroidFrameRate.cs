using Android.App;
using Android.Hardware.Display;
using Android.OS;
using Android.Views;
using Noctis.Mobile.Services;
using Noctis.Services;

namespace Noctis.Android.Services;

/// <summary>
/// Settings › Appearance › Smooth Motion on Android. Nothing in the app used to ask for a rate,
/// so adaptive-refresh phones (Samsung, Pixel, Xiaomi, Realme) ran it at whatever their
/// heuristics guessed for its SurfaceView, often 60 Hz (a Galaxy S23's SurfaceFlinger was seen
/// voting anywhere between 24 and 120 Hz for it). Two requests, both re-made on resume, on
/// every new drawing surface and whenever the display changes (a resolution switch renumbers
/// the modes):
/// <list type="bullet">
/// <item>The window's preferredDisplayModeId (API 23+): the fastest mode at the current
/// resolution, or the one nearest 60 Hz for Standard (<see cref="FrameRatePolicy"/>). Below
/// the system's own limits: Battery Saver, the user's Standard refresh setting and thermal caps
/// still win (DisplayModeDirector ranks them higher).</item>
/// <item>Surface.setFrameRate on Avalonia's SurfaceView (API 30+), which is what draws every
/// frame: its own layer's vote, DEFAULT compatibility (UI, not video) and, on API 31+, only if
/// the switch is seamless, so changing rate never blanks the screen.</item>
/// </list>
/// Avalonia's render loop needs nothing: ChoreographerTimer ticks on the display's own vsync
/// (AChoreographer), so it draws at whatever rate the screen runs, and it stops asking for
/// callbacks when nothing changes (DefaultRenderLoop sets Tick to null), so an idle app posts no
/// buffers and the panel is free to drop its rate where the hardware can.
/// </summary>
internal sealed class AndroidFrameRate : Java.Lang.Object, IFrameRateHost, DisplayManager.IDisplayListener, ISurfaceHolderCallback
{
    public static AndroidFrameRate Instance { get; } = new();

    private bool _smoothMotion = true;
    private Activity? _activity;
    private SurfaceView? _surface;
    private DisplayManager? _displays;
    // The last vote, so the display listener (raised on every rate switch an adaptive panel
    // makes, not only on resolution changes) does not repeat it; a new surface clears it.
    private Surface? _votedSurface;
    private float _votedRate = -1;

    private AndroidFrameRate() { }

    /// <summary>Settings (and the saved value at start). Any thread; applied on the main thread.</summary>
    public void Request(bool smoothMotion)
    {
        _smoothMotion = smoothMotion;
        if (Looper.MyLooper() == Looper.MainLooper) Apply();
        else _activity?.RunOnUiThread(Apply);
    }

    /// <summary>MainActivity.OnResume: hook the display and the surface, then ask.</summary>
    public void Attach(Activity activity)
    {
        _activity = activity;
        if (_displays == null && activity.GetSystemService(global::Android.Content.Context.DisplayService) is DisplayManager displays)
        {
            _displays = displays;
            displays.RegisterDisplayListener(this, null);   // null: callbacks on this (main) thread
        }
        Apply();
    }

    /// <summary>MainActivity.OnPause / OnDestroy: let go of the display listener and the activity.</summary>
    public void Detach(Activity activity)
    {
        if (!ReferenceEquals(_activity, activity)) return;
        _displays?.UnregisterDisplayListener(this);
        _displays = null;
        _surface?.Holder?.RemoveCallback(this);
        _surface = null;
        _votedSurface = null;
        _activity = null;
    }

    /// <summary>MainActivity.OnWindowFocusChanged: the SurfaceView may only now be in the tree.</summary>
    public void Reapply() => Apply();

    private void Apply()
    {
        try
        {
            if (_activity?.Window is not { } window) return;
            var display = Build.VERSION.SdkInt >= BuildVersionCodes.R ? _activity.Display : _activity.WindowManager?.DefaultDisplay;
            DisplayModeInfo? mode = null;
            if (display != null && Build.VERSION.SdkInt >= BuildVersionCodes.M)
            {
                var current = Info(display.GetMode());
                var modes = (display.GetSupportedModes() ?? Array.Empty<Display.Mode>()).Select(Info).ToList();
                mode = FrameRatePolicy.PickMode(modes, current, _smoothMotion);
                var attributes = window.Attributes;
                var wanted = mode?.Id ?? 0;
                if (attributes != null && attributes.PreferredDisplayModeId != wanted)
                {
                    attributes.PreferredDisplayModeId = wanted;
                    window.Attributes = attributes;
                    DebugLog.Write("Display", $"Mode {wanted} ({mode?.RefreshRate:0.#} Hz) asked of {modes.Count} at {current.Width}x{current.Height}");
                }
            }
            HookSurface(window);
            VoteOnSurface(FrameRatePolicy.SurfaceRate(mode, _smoothMotion));
        }
        catch (Exception ex)
        {
            // A refresh rate is never worth a crash: the app simply keeps the system's choice.
            DebugLog.Write("Display", $"Frame rate request failed: {ex.Message}");
        }
    }

    private static DisplayModeInfo Info(Display.Mode? mode) =>
        mode == null ? default : new DisplayModeInfo(mode.ModeId, mode.PhysicalWidth, mode.PhysicalHeight, mode.RefreshRate);

    /// <summary>Avalonia's SurfaceView (internal to it, so found in the window's views); its
    /// holder's callbacks re-vote whenever the surface is recreated.</summary>
    private void HookSurface(Window window)
    {
        if (_surface is { IsAttachedToWindow: true }) return;
        _surface?.Holder?.RemoveCallback(this);
        _surface = Find(window.DecorView);
        _surface?.Holder?.AddCallback(this);
    }

    private static SurfaceView? Find(View? view)
    {
        if (view is SurfaceView surface) return surface;
        if (view is not ViewGroup group) return null;
        for (var i = 0; i < group.ChildCount; i++)
            if (Find(group.GetChildAt(i)) is { } found) return found;
        return null;
    }

    private void VoteOnSurface(float rate)
    {
        if (Build.VERSION.SdkInt < BuildVersionCodes.R) return;
        if (_surface?.Holder?.Surface is not { IsValid: true } surface) return;
        if (ReferenceEquals(surface, _votedSurface) && rate == _votedRate) return;
        _votedSurface = surface;
        _votedRate = rate;
        if (Build.VERSION.SdkInt >= BuildVersionCodes.S)
            surface.SetFrameRate(rate, (int)SurfaceFrameRateCompatibility.Default, (int)SurfaceChangeFrameRate.OnlyIfSeamless);
        else
            surface.SetFrameRate(rate, (int)SurfaceFrameRateCompatibility.Default);
    }

    // ── Display listener: a resolution switch or a new mode list re-picks. ──
    public void OnDisplayAdded(int displayId) { }
    public void OnDisplayRemoved(int displayId) { }
    public void OnDisplayChanged(int displayId)
    {
        if (_activity?.Display is { } shown && Build.VERSION.SdkInt >= BuildVersionCodes.R && shown.DisplayId != displayId) return;
        Apply();
    }

    // ── Surface callbacks: a new surface carries no vote until it is given one. ──
    public void SurfaceCreated(ISurfaceHolder holder) => Revote();
    public void SurfaceChanged(ISurfaceHolder holder, global::Android.Graphics.Format format, int width, int height) => Revote();
    public void SurfaceDestroyed(ISurfaceHolder holder) => _votedSurface = null;

    private void Revote()
    {
        _votedSurface = null;
        Apply();
    }
}
