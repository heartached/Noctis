using Android.Content;
using Android.Media;
using Noctis.Mobile.Services;
using AStream = Android.Media.Stream;

namespace Noctis.Android.Services;

/// <summary>
/// STREAM_MUSIC as 0..1: the volume the hardware keys move. Android does not tell an app
/// when the keys change it, so MainActivity calls <see cref="NotifyChanged"/> after a volume
/// key and on resume, and the Now Playing slider re-reads.
/// </summary>
public sealed class AndroidVolumeControl : IVolumeControl
{
    private readonly AudioManager _audio;

    public AndroidVolumeControl(Context context)
    {
        _audio = (AudioManager)context.GetSystemService(Context.AudioService)!;
    }

    public double Level
    {
        get
        {
            var max = _audio.GetStreamMaxVolume(AStream.Music);
            return max <= 0 ? 0 : _audio.GetStreamVolume(AStream.Music) / (double)max;
        }
        set
        {
            var max = _audio.GetStreamMaxVolume(AStream.Music);
            _audio.SetStreamVolume(AStream.Music, (int)Math.Round(Math.Clamp(value, 0, 1) * max), (VolumeNotificationFlags)0);
        }
    }

    public event EventHandler? Changed;

    public void NotifyChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
