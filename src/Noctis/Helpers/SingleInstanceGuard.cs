using System.IO.Pipes;
using System.Threading;

namespace Noctis.Helpers;

/// <summary>
/// Ensures only one Noctis instance runs per user. The first instance holds a
/// named mutex and listens on a named pipe; any later launch (taskbar/pinned
/// icon while the app sits in the tray) signals that pipe so the running
/// window can surface, then exits instead of starting a duplicate player.
/// </summary>
public static class SingleInstanceGuard
{
    // Mutex default namespace is per-session on Windows; the pipe namespace is
    // machine-global, so the user name keeps two logged-in users independent.
    private static readonly string MutexName = $"Noctis-SingleInstance-{Environment.UserName}";
    private static readonly string PipeName = $"Noctis-Activate-{Environment.UserName}";

    private static Mutex? _mutex;

    // Payload cap for the activation pipe: it only ever carries a handful of
    // file paths from our own second launch.
    private const int MaxActivationPayloadBytes = 64 * 1024;

    /// <summary>
    /// Raised on a background thread when another launch asks this instance to
    /// surface its window. Carries the audio-file paths that launch was given
    /// ("Open with Noctis" while running) — empty for a plain activation.
    /// </summary>
    public static event Action<IReadOnlyList<string>>? ActivationRequested;

    /// <summary>
    /// Returns true if this process is the first instance (and starts the
    /// activation listener); false if another instance already holds the mutex.
    /// </summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }
        }
        catch
        {
            // Mutex machinery unavailable (exotic platform/permissions) — fail
            // open so the app still starts rather than refusing to launch.
            return true;
        }

        _ = Task.Run(ListenForActivationAsync);
        return true;
    }

    /// <summary>
    /// Asks the already-running instance to show its window, forwarding any
    /// file paths this launch was asked to open. Best effort.
    /// </summary>
    public static void SignalFirstInstance(IReadOnlyList<string>? filesToOpen = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            var payload = filesToOpen is { Count: > 0 }
                ? string.Join('\n', filesToOpen.Select(Path.GetFullPath))
                : string.Empty;
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
        }
        catch
        {
            // The running instance didn't answer; still exit so a stuck pipe
            // can't spawn duplicate players.
        }
    }

    private static async Task ListenForActivationAsync()
    {
        var consecutiveFailures = 0;
        while (true)
        {
            try
            {
                using var server = CreateActivationServer();
                await server.WaitForConnectionAsync().ConfigureAwait(false);
                consecutiveFailures = 0;
                ActivationRequested?.Invoke(await ReadForwardedFilesAsync(server).ConfigureAwait(false));
            }
            catch
            {
                // Persistent failure (e.g. another process squatting the pipe
                // name) — retrying forever just burns a thread. Give up after a
                // few attempts: the mutex still prevents duplicate instances,
                // only surface-window-on-second-launch degrades.
                if (++consecutiveFailures >= 5)
                {
                    Services.DebugLogger.Error(Services.DebugLogger.Category.Error,
                        "SingleInstance.PipeGaveUp",
                        "activation pipe unavailable after 5 attempts; second launches won't surface the window");
                    return;
                }
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadForwardedFilesAsync(NamedPipeServerStream server)
    {
        try
        {
            using var ms = new MemoryStream();
            var buffer = new byte[4096];
            int read;
            while ((read = await server.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                if (ms.Length + read > MaxActivationPayloadBytes) return Array.Empty<string>();
                ms.Write(buffer, 0, read);
            }
            if (ms.Length == 0) return Array.Empty<string>();

            return System.Text.Encoding.UTF8.GetString(ms.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(File.Exists)
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>(); // malformed payload → plain activation
        }
    }

    private static NamedPipeServerStream CreateActivationServer()
    {
        if (OperatingSystem.IsWindows())
        {
            // Lock the pipe to the current user. Its name lives in the
            // machine-global pipe namespace, so the default ACL would let any
            // local process connect (spam activations) or squat the name.
            var security = new PipeSecurity();
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            security.AddAccessRule(new PipeAccessRule(
                identity.User!,
                PipeAccessRights.FullControl,
                System.Security.AccessControl.AccessControlType.Allow));
            return NamedPipeServerStreamAcl.Create(
                PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 0, outBufferSize: 0, security);
        }

        return new NamedPipeServerStream(
            PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }
}
