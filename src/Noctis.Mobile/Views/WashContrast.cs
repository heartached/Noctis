using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Noctis.Converters;

namespace Noctis.Mobile.Views;

/// <summary>
/// The black Now Playing lays over its Colour Wash (WashDim): Settings › Now Playing Background's
/// darkening (ShellViewModel.NowPlayingDimOpacity), or more where Brightness has thinned the shade
/// (NowPlayingShadeOpacity) over a light cover. At +50 the shade is 40% of itself, and white text
/// under the transport over a white cover fell to about 2:1, where the stock Brightness keeps
/// 4.5:1. As Blurred Cover's Dim (<see cref="BlurredCoverBackdrop.ContrastDimFor"/>): the user's
/// value is the floor, and the wash's brightest pixel (<see cref="PreBlurredArtworkConverter.WashTonesOf"/>)
/// gets the least black that keeps white text at 4.5:1 there. Dark covers, the stock Brightness
/// and darker are drawn as before. Values: the user's darkening, the shade's opacity, the wash.
/// </summary>
public sealed class WashContrast : IMultiValueConverter
{
    public static readonly WashContrast Dim = new();

    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var user = values.Count > 0 && values[0] is double d ? d : 0;
        var shade = values.Count > 1 && values[1] is double s ? s : 1;
        var tones = values.Count > 2 && values[2] is Bitmap wash ? PreBlurredArtworkConverter.WashTonesOf(wash) : null;
        return tones is { } t ? DimFor(user, shade, t.Brightest) : user;
    }

    /// <summary>The black to draw: <paramref name="user"/>'s, or what a wash whose brightest pixel is
    /// <paramref name="brightest"/> needs under the shade at <paramref name="shadeOpacity"/>.</summary>
    internal static double DimFor(double user, double shadeOpacity, Color brightest) =>
        Math.Max(user, BlurredCoverBackdrop.ContrastDimFor(brightest, shadeOpacity));
}
