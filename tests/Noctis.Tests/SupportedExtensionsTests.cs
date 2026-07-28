using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Guards the library whitelist against DSD-style ghosts: every extension the
/// scanner accepts (except the documented not-yet-playable DSD pair) must map
/// to a codec badge, so a format can't be importable yet render unlabeled.
/// </summary>
public class SupportedExtensionsTests
{
    [Theory]
    [InlineData(".wma")]
    [InlineData(".asf")]
    [InlineData(".aac")]
    [InlineData(".oga")]
    public void Whitelist_ContainsExtension(string ext)
        => Assert.Contains(ext, MetadataService.SupportedExtensions);

    [Theory]
    [InlineData(".wma")]
    [InlineData(".asf")]
    [InlineData(".aac")]
    [InlineData(".oga")]
    public void LossyExtensions_AreNotLabeledLossless(string ext)
        => Assert.False(new Track { FilePath = "song" + ext }.IsLossless);

    [Fact]
    public void EverySupportedExtension_ExceptDsd_MapsToACodecShortName()
    {
        foreach (var ext in MetadataService.SupportedExtensions)
        {
            if (ext is ".dsf" or ".dff")
                continue; // surfaced in the library, playback pending (no VLC 3.x DSD codec)

            var track = new Track { FilePath = "song" + ext };
            Assert.False(string.IsNullOrEmpty(track.CodecShortName),
                $"'{ext}' is importable but maps to no codec label");
        }
    }
}
