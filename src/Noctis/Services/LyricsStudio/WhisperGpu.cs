using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.X86;
using Noctis.Helpers;
using Whisper.net.LibraryLoader;
using Whisper.net.Logger;

namespace Noctis.Services.LyricsStudio;

/// <summary>
/// Experimental: Lyrics Studio on the graphics card through whisper.cpp's Vulkan backend
/// (Whisper.net.Runtime.Vulkan, built into the app only with -p:LyricsGpu=true). Off unless the
/// user turns it on. Measured 10-19 (l2-speed): see the branch report for speed and accuracy.
///
/// What can go wrong, each checked on this PC (D:\NoctisLyricsLab\v2\scratch\speed\gpu_fallback_tests.ps1):
/// no Vulkan driver or a Vulkan init error → whisper.cpp runs on the CPU inside the Vulkan build;
/// a missing vulkan-1.dll → Whisper.net's loader probes the Vulkan folder twice and the second
/// load ABORTS the process (GGML_ASSERT in ggml.cpp), so this class checks the Vulkan loader
/// itself first; a GPU memory allocation that fails → GGML_ASSERT, the process ends. A native
/// abort cannot be caught, so a marker file is written while the card works: if Noctis ends with
/// it in place (a model load or a song was on the card), the next start turns the GPU off and
/// says why. Shipping this for everyone needs the model in a worker process instead (report).
/// The native library is chosen once per process: a change applies after a restart.
/// </summary>
public static class WhisperGpu
{
    private static readonly object Gate = new();
    private static bool _configured;
    private static string? _markerPath;
    private static IDisposable? _logHook;

    /// <summary>The user's choice (Settings / Studio). Read when the first model loads.</summary>
    public static bool Requested { get; set; }

    /// <summary>Why the GPU is not used even though it was asked for (null: nothing in the way).</summary>
    public static string? BlockedReason { get; private set; }

    /// <summary>True when this build carries the Vulkan runtime and this PC's CPU can run its CPU half.</summary>
    public static bool Available => RuntimeFolder() is not null && CpuCanRunVulkanBuild;

    /// <summary>The Vulkan build's CPU part is compiled for AVX2/FMA/F16C (Whisper.net windows-scripts.ps1); Whisper.net checks that only for its plain CPU build.</summary>
    internal static bool CpuCanRunVulkanBuild =>
        RuntimeInformation.ProcessArchitecture == Architecture.X64 && Avx2.IsSupported && Fma.IsSupported;

    /// <summary>The library loaded in this process, once one is (Cpu, CpuNoAvx, Vulkan…).</summary>
    public static string? Loaded => RuntimeOptions.LoadedLibrary?.ToString();

    /// <summary>True when the loaded build runs on the GPU (the CPU build never does on Windows/Linux).</summary>
    public static bool UsingVulkan => RuntimeOptions.LoadedLibrary == RuntimeLibrary.Vulkan;

    /// <summary>Where the crash marker lives (the model folder); set by the engine.</summary>
    public static void SetMarkerFolder(string folder)
    {
        lock (Gate) _markerPath = Path.Combine(folder, "gpu-session.marker");
    }

    /// <summary>
    /// Picks the native build before the first model load: Vulkan first only when asked for and
    /// safe to try, else the CPU builds. Always explicit, so a stray runtimes/cuda or /vulkan
    /// folder can never be picked by Whisper.net's default order (CUDA, Vulkan, …, CPU).
    /// </summary>
    internal static void ConfigureBeforeFirstLoad()
    {
        lock (Gate)
        {
            if (_configured || RuntimeOptions.LoadedLibrary is not null) return;
            _configured = true;
            BlockedReason = Requested ? WhyNot() : null;
            var order = new List<RuntimeLibrary>();
            if (Requested && BlockedReason is null) order.Add(RuntimeLibrary.Vulkan);
            order.Add(RuntimeLibrary.Cpu);
            order.Add(RuntimeLibrary.CpuNoAvx);
            RuntimeOptions.RuntimeLibraryOrder = order;
            if (Requested)
                DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.Gpu", BlockedReason is null ? "asked for: trying Vulkan first" : $"asked for, not used: {BlockedReason}");
            HookNativeLog();
        }
    }

    private static string? WhyNot()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return "only Windows and Linux builds carry the Vulkan runtime";
        if (RuntimeFolder() is null) return "this build has no Vulkan runtime";
        if (!CpuCanRunVulkanBuild) return "this CPU lacks AVX2/FMA, which the Vulkan build needs";
        if (!VulkanLoaderPresent()) return "no Vulkan loader on this PC (vulkan-1.dll / libvulkan.so.1)";
        if (CrashedLastTime(out var when)) return $"Noctis closed unexpectedly while the GPU was in use ({when:yyyy-MM-dd HH:mm}); untick and tick the option to try again";
        return null;
    }

    private static string? RuntimeFolder()
    {
        var rid = OperatingSystem.IsWindows() ? "win-x64" : OperatingSystem.IsLinux() ? "linux-x64" : null;
        if (rid is null || RuntimeInformation.ProcessArchitecture != Architecture.X64) return null;
        var dir = Path.Combine(AppContext.BaseDirectory, "runtimes", "vulkan", rid);
        var whisper = OperatingSystem.IsWindows() ? "whisper.dll" : "libwhisper.so";
        return File.Exists(Path.Combine(dir, whisper)) ? dir : null;
    }

    /// <summary>The Vulkan loader the backend links against. Without it Whisper.net's fallback aborts the process (see the class notes).</summary>
    private static bool VulkanLoaderPresent()
    {
        var name = OperatingSystem.IsWindows() ? "vulkan-1.dll" : "libvulkan.so.1";
        if (!NativeLibrary.TryLoad(name, out var handle)) return false;
        NativeLibrary.Free(handle);
        return true;
    }

    private static bool CrashedLastTime(out DateTime when)
    {
        when = default;
        var path = _markerPath;
        if (path is null || !File.Exists(path)) return false;
        when = File.GetLastWriteTime(path);
        return true;
    }

    /// <summary>The user turned the GPU on again after a crash: forget the marker.</summary>
    public static void ClearCrashMarker()
    {
        lock (Gate)
        {
            try { if (_markerPath is not null) File.Delete(_markerPath); } catch { }
        }
    }

    private static int _gpuWork;

    /// <summary>
    /// Native GPU work starts (a model load or a song): leave a marker that only <see cref="EndGpuWork"/>
    /// removes. Only around real work, not while a loaded model idles, so closing Noctis normally
    /// never looks like a crash. Dispose the result when the work is over.
    /// </summary>
    internal static IDisposable BeginGpuWork()
    {
        if (!UsingVulkan) return NoWork.Instance;
        lock (Gate)
        {
            if (_gpuWork++ == 0)
                try { if (_markerPath is not null) File.WriteAllText(_markerPath, DateTime.Now.ToString("O")); } catch { }
        }
        return new Work();
    }

    private static void EndGpuWork()
    {
        lock (Gate)
        {
            if (--_gpuWork > 0) return;
            _gpuWork = 0;
            try { if (_markerPath is not null) File.Delete(_markerPath); } catch { }
        }
    }

    private sealed class Work : IDisposable
    {
        private int _done;
        public void Dispose() { if (Interlocked.Exchange(ref _done, 1) == 0) EndGpuWork(); }
    }

    private sealed class NoWork : IDisposable
    {
        public static readonly NoWork Instance = new();
        public void Dispose() { }
    }

    private static readonly HashSet<string> LoggedNative = new(StringComparer.Ordinal);

    /// <summary>
    /// whisper.cpp says which device it took ("using Vulkan0 backend", "no GPU found"); keep those
    /// lines in the session log, each distinct line once per process (whisper.cpp repeats them for
    /// every 30 s window, as each window gets a fresh state).
    /// </summary>
    private static void HookNativeLog()
    {
        _logHook ??= LogProvider.AddLogger((level, message) =>
        {
            if (string.IsNullOrWhiteSpace(message)) return;
            if (level == WhisperLogLevel.Error
                || message.Contains("whisper_backend_init_gpu", StringComparison.Ordinal)
                || message.StartsWith("ggml_vulkan: ", StringComparison.Ordinal)
                || message.Contains("dtw_token_timestamps", StringComparison.Ordinal))
            {
                // "encoder_begin_callback returned false" is the transcriber stopping each call after one window on purpose.
                if (message.Contains("encoder_begin_callback returned false", StringComparison.Ordinal)) return;
                var line = message.Trim();
                lock (LoggedNative)
                {
                    if (LoggedNative.Count > 200 || !LoggedNative.Add(line)) return;
                }
                DebugLogger.Info(DebugLogger.Category.Lyrics, "Whisper.Native", line);
            }
        });
    }
}
