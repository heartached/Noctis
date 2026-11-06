using System.Globalization;
using Avalonia.Media.Transformation;
using Noctis.Mobile.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// A transform built by formatting a number into CSS text breaks in a decimal-comma culture:
/// "translateY(12,5px)" does not parse. The album note sheet's drag did that (AlbumNoteSheet.Follow);
/// it now uses Motion, which formats invariantly.
/// </summary>
public class MobileMotionCultureTests
{
    [Fact]
    public void CommaDecimalTransformText_DoesNotParse_ButMotionWorksInAnyCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            var offset = 12.5;
            Assert.ThrowsAny<Exception>(() => TransformOperations.Parse($"translateY({offset}px)"));
            Assert.Equal(12.5, Motion.TranslateY(offset).Value.M32, 3);
            Assert.Equal(-7.25, Motion.TranslateX(-7.25).Value.M31, 3);
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }
}
