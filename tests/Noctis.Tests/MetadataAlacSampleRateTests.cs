using System;
using System.IO;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

// Hi-res ALAC: TagLib# reads the MP4 sample entry's 16-bit rate field, which cannot hold
// 88.2–192 kHz — a 96 kHz file scanned as 48 kHz (and lost its Hi-Res badge). The ALAC
// decoder config atom is the authority; the scanner now prefers it whenever it is valid,
// not only when TagLib returned nothing.
public class MetadataAlacSampleRateTests
{
    private static string Fixture(string name) =>
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "Audio", name);

    [Fact]
    public void HiResAlac_SampleRateComesFromTheDecoderConfigAtom()
    {
        // 0.02 s mono 24-bit ALAC at 96 kHz (ffmpeg); TagLib# alone reports 48000 for it.
        var track = new MetadataService().ReadTrackMetadata(Fixture("alac_96k_tiny.m4a"));

        Assert.NotNull(track);
        Assert.Equal(96000, track!.SampleRate);
        Assert.Equal(24, track.BitsPerSample);
        Assert.Contains("alac", track.Codec, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(48000, 96000, 96000)] // atom wins over the overflowed sample-entry rate
    [InlineData(0, 44100, 44100)]     // TagLib read nothing: the old fallback still works
    [InlineData(44100, 0, 44100)]     // no usable atom: TagLib's value stays
    [InlineData(44100, 44100, 44100)]
    public void ResolveAlacSampleRate_PrefersAValidAtomRate(int tagLib, int atom, int expected) =>
        Assert.Equal(expected, MetadataService.ResolveAlacSampleRate(tagLib, atom));
}
