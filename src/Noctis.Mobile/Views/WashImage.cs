using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Converters;
using Noctis.Services;

namespace Noctis.Mobile.Views;

/// <summary>
/// The Colour Wash behind Now Playing and its Lyrics: <see cref="Art"/>'s wash
/// (PreBlurredArtworkConverter's "wash": the cover read down to 128 px, vivified and blurred),
/// made off the UI thread. As a binding converter it was made on the UI thread whenever a new
/// cover landed, once for Now Playing and once for the Lyrics page under it (hidden or not): a
/// read of the 768 px cover and a 45-tap blur, right in the frames where Previous / Next play their
/// press (beta 6, #mobile-beta: "the press lags when it switches songs, not when it restarts the
/// song"). A cover already washed (the converter keeps one per decoded cover) shows at once; a new
/// one is washed on the pool while the old wash stays up, then swapped in (BlurredCoverBackdrop's way:
/// the cover is held while the pool reads it).
/// </summary>
public sealed class WashImage : Image
{
    public static readonly StyledProperty<IImage?> ArtProperty =
        AvaloniaProperty.Register<WashImage, IImage?>(nameof(Art));

    /// <summary>The sharp cover's bitmap (the page's CachedImage), the one to wash.</summary>
    public IImage? Art { get => GetValue(ArtProperty); set => SetValue(ArtProperty, value); }

    private static readonly PreBlurredArtworkConverter Converter = new();
    private int _generation;

    /// <summary>The last wash started (tests await it, then run the UI jobs).</summary>
    internal Task PendingBuild { get; private set; } = Task.CompletedTask;

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ArtProperty) Rebuild();
    }

    private async void Rebuild()
    {
        var generation = ++_generation;
        if (Art is not Bitmap art)
        {
            Source = null;
            PendingBuild = Task.CompletedTask;
            return;
        }
        if (PreBlurredArtworkConverter.CachedWash(art) is { } washed)
        {
            Source = washed;
            PendingBuild = Task.CompletedTask;
            return;
        }
        // The cover stays alive while the pool reads it: CachedImage lets go of it on the next song.
        ArtworkCache.Acquire(art);
        IImage? wash = null;
        try
        {
            var build = Task.Run(() => Converter.Convert(art, typeof(IImage), PreBlurredArtworkConverter.WashParameter, CultureInfo.InvariantCulture) as IImage);
            PendingBuild = build;
            wash = await build;
        }
        catch (Exception ex)
        {
            // No wash beats a crash: the page's plain colour shows under the shade.
            DebugLog.Write("NowPlaying", $"Wash failed: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ArtworkCache.Release(art);
        }
        if (generation != _generation) return;   // a newer cover came in meanwhile (the converter keeps this one)
        Source = wash;
    }
}
