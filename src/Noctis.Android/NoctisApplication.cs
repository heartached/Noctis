using Android.App;
using Android.Runtime;
using Avalonia;
using Avalonia.Android;

namespace Noctis.Android;

/// <summary>Android application object: owns the Avalonia app across activity restarts.</summary>
[Application]
public class NoctisApplication : AvaloniaAndroidApplication<AndroidApp>
{
    protected NoctisApplication(nint javaReference, JniHandleOwnership transfer)
        : base(javaReference, transfer)
    {
#if DEBUG
        PreloadRacyAssemblies();
#endif
    }

#if DEBUG
    /// <summary>
    /// Debug builds JIT; Release is fully AOT-compiled and never does. During the library load
    /// several thread-pool threads JIT methods that reference System.Runtime.Serialization.Formatters
    /// at the same moment, each mapping that assembly ("already mmapped by another thread"), and
    /// the .NET Android runtime then crashed in mono_assembly_request_byname (SIGSEGV in
    /// strcasecmp on a .NET TP Worker), about 1 launch in 6 on the emulator (11-23). Loading it
    /// once here, on the main thread before any background work, leaves nothing to race over.
    /// </summary>
    private static void PreloadRacyAssemblies()
    {
        try { System.Reflection.Assembly.Load("System.Runtime.Serialization.Formatters"); }
        catch (Exception ex) { global::Android.Util.Log.Warn("Noctis", $"Preload failed: {ex.Message}"); }
    }
#endif

    // Arabic-script text (Persian lyrics and titles) draws in the bundled Vazirmatn, as on the
    // desktop (#269); Android finds every other missing glyph on its own.
    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
        => base.CustomizeAppBuilder(builder).WithInterFont().With(Noctis.Helpers.ScriptFonts.Options());
}
