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

        StartActivationListener();
        return true;
    }

    /// <summary>
    /// Starts the activation listener without owning the mutex. Used by the fall-through
    /// launch: the mutex name is a bare, guessable, un-ACL'd string, so any other process
    /// in the session can create it first and leave <see cref="TryAcquire"/> returning
    /// false forever — an app that simply refuses to start, with no message. When the
    /// mutex is held but nothing answers the activation pipe, there is no live instance to
    /// surface and we launch anyway; this keeps the next launch able to reach us.
    /// </summary>
    public static void StartActivationListener()
    {
        if (Interlocked.Exchange(ref _listenerStarted, 1) != 0) return;
        _ = Task.Run(ListenForActivationAsync);
    }

    private static int _listenerStarted;

    /// <summary>
    /// Asks the already-running instance to show its window, forwarding any
    /// file paths this launch was asked to open. Best effort.
    /// </summary>
    /// <returns>True when the running instance acknowledged the signal.</returns>
    public static bool SignalFirstInstance(IReadOnlyList<string>? filesToOpen = null)
    {
        try
        {
            // CurrentUserOnly on both ends. On macOS/Linux .NET backs this with a Unix
            // domain socket in $TMPDIR created with the default umask and never chmod'd —
            // under a permissive umask any local account could connect and push a payload
            // that ReadForwardedFilesAsync turns into an OpenExternalFiles call, driving
            // the user's player into opening arbitrary files it can read. The flag also
            // makes the client verify the server's owner.
            using var client = new NamedPipeClientStream(
                ".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(2000);
            var payload = filesToOpen is { Count: > 0 }
                ? string.Join('\n', filesToOpen.Select(Path.GetFullPath))
                : string.Empty;
            var bytes = System.Text.Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            // The running instance didn't answer — it may be hung, or its listener gave
            // up after repeated pipe failures. Report it so the caller can tell the user
            // instead of the app just vanishing on a double-click.
            System.Diagnostics.Debug.WriteLine($"[SingleInstance] Signal failed: {ex.Message}");
            Services.DebugLog.Write("SingleInstance", $"Signal failed: {ex.Message}");
            return false;
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

        // CurrentUserOnly is the non-Windows equivalent of the ACL above: it chmods the
        // backing Unix domain socket to 0700. Without it the socket was created with the
        // default umask in a world-writable $TMPDIR and never restricted.
        return new NamedPipeServerStream(
            PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
    }
}
