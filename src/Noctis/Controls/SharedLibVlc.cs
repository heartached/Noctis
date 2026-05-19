using LibVLCSharp.Shared;

namespace Noctis.Controls;

/// <summary>
/// The single process-wide <see cref="LibVLC"/> instance for all animated-cover
/// surfaces. Multiple LibVLC instances fight over global VLC subsystem state
/// (notably the audio device, which breaks the main audio player), so every
/// animated-cover control must share this one. Never disposed.
/// </summary>
internal static class SharedLibVlc
{
    private static LibVLC? _instance;
    private static readonly object _lock = new();

    public static LibVLC Instance
    {
        get
        {
            if (_instance != null) return _instance;
            lock (_lock)
            {
                if (_instance == null)
                {
                    Core.Initialize();
                    // --aout=none guarantees this LibVLC never opens an audio device.
                    _instance = new LibVLC("--quiet", "--no-video-title-show", "--aout=none");
                }
                return _instance;
            }
        }
    }
}
