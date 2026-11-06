namespace Noctis.Services;

/// <summary>
/// A share render that paints video frames on demand: the karaoke card and the Lyrics Page
/// video both feed the clip encoder and the live preview through this.
/// </summary>
public interface IShareFrameAnimator : IDisposable
{
    /// <summary>Frame width in pixels.</summary>
    int PixelWidth { get; }

    /// <summary>Frame height in pixels.</summary>
    int PixelHeight { get; }

    /// <summary>Renders the frame at <paramref name="tSeconds"/> (track time) into
    /// <paramref name="dest"/> (BGRA8888 premul, <see cref="PixelWidth"/> ×
    /// <see cref="PixelHeight"/>, <paramref name="destRowBytes"/> per row).</summary>
    void RenderFrame(double tSeconds, IntPtr dest, int destRowBytes);
}
