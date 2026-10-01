namespace Noctis.Services;

/// <summary>
/// A background "keep the audio device warm" service: holds a silent stream so
/// the main player opens against an already-running endpoint instead of paying
/// the cold-device spin-up that drops the first buffers (the track-start clip).
/// <see cref="WasapiSilenceKeepAlive"/> is the Windows implementation;
/// <see cref="VlcSilenceKeepAlive"/> covers macOS/Linux. All members must be safe
/// to call from any thread and must never throw into the playback path.
/// </summary>
internal interface IAudioKeepAlive : IDisposable
{
    /// <summary>Mark playback activity: refreshes the idle deadline and resumes a parked stream.</summary>
    void NotifyActivity();

    /// <summary>
    /// <see cref="NotifyActivity"/> for a play about to open a new output stream. Returns
    /// the <see cref="Environment.TickCount64"/> before which that stream should not start
    /// (a parked keep-alive needs a head start to wake the device), or 0 to start at once.
    /// </summary>
    long WakeForPlayback()
    {
        NotifyActivity();
        return 0;
    }

    /// <summary>Force-park (true) or allow (false) the silent stream — used while exclusive output holds the endpoint.</summary>
    void SetSuspended(bool suspended);
}
