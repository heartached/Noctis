namespace Noctis.Mobile.Services;

/// <summary>
/// The device media volume (Android STREAM_MUSIC) as 0..1. The Now Playing slider drives the
/// same volume the hardware keys do, as phone music apps do, rather than an in-app gain on
/// top of it. <see cref="Changed"/> fires when something else moved it (the keys, another app).
/// </summary>
public interface IVolumeControl
{
    double Level { get; set; }
    event EventHandler? Changed;
}
