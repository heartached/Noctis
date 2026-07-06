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

    /// <summary>Raised on a background thread when another launch asks this instance to surface its window.</summary>
    public static event Action? ActivationRequested;

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

    /// <summary>Asks the already-running instance to show its window. Best effort.</summary>
    public static void SignalFirstInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(2000);
            client.WriteByte(1);
        }
        catch
        {
            // The running instance didn't answer; still exit so a stuck pipe
            // can't spawn duplicate players.
        }
    }

    private static async Task ListenForActivationAsync()
    {
        while (true)
        {
            try
            {
                using var server = new NamedPipeServerStream(
                    PipeName, PipeDirection.In, maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync().ConfigureAwait(false);
                ActivationRequested?.Invoke();
            }
            catch
            {
                // Pipe hiccup — brief pause so a persistent failure can't spin the CPU.
                await Task.Delay(1000).ConfigureAwait(false);
            }
        }
    }
}
