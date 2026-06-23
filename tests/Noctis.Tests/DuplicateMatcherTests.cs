using System;
using System.Linq;
using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

public class DuplicateMatcherTests
{
    private static Track T(string artist, string title, int durationSec, string path,
        int bitrate = 320, bool lossless = false, int bits = 16, int rate = 44100, long size = 1_000_000)
        => new()
        {
            Artist = artist,
            Title = title,
            Duration = TimeSpan.FromSeconds(durationSec),
            FilePath = path,
            Bitrate = bitrate,
            BitsPerSample = bits,
            SampleRate = rate,
            FileSize = size,
            Codec = lossless ? "flac" : "mp3"
        };

    [Fact]
    public void SameArtistTitle_NearDuration_GroupsTogether()
    {
        var groups = DuplicateMatcher.FindDuplicates(new[]
        {
            T("Adele", "Hello", 295, @"C:\a\hello.mp3"),
            T("Adele", "Hello", 296, @"C:\b\hello.flac", lossless: true)
        });

        Assert.Single(groups);
        Assert.Equal(2, groups[0].Tracks.Count);
    }

    [Fact]
    public void DifferentDuration_BeyondTolerance_DoesNotGroup()
    {
        var groups = DuplicateMatcher.FindDuplicates(new[]
        {
            T("Adele", "Hello", 200, @"C:\a\hello.mp3"),
            T("Adele", "Hello", 295, @"C:\b\hello.mp3")
        });
        Assert.Empty(groups);
    }

    [Fact]
    public void Normalization_IgnoresCaseAndPunctuation()
    {
        var groups = DuplicateMatcher.FindDuplicates(new[]
        {
            T("The Beatles", "Hey Jude!", 425, @"C:\a\x.mp3"),
            T("the beatles", "hey jude", 425, @"C:\b\y.mp3")
        });
        Assert.Single(groups);
    }

    [Fact]
    public void SuggestedKeep_PrefersLossless()
    {
        var lossy = T("A", "B", 200, @"C:\a\x.mp3", bitrate: 320, lossless: false);
        var flac = T("A", "B", 200, @"C:\b\y.flac", bitrate: 1000, lossless: true, bits: 16);
        var groups = DuplicateMatcher.FindDuplicates(new[] { lossy, flac });

        Assert.Single(groups);
        Assert.Equal(flac.Id, groups[0].SuggestedKeepId);
    }

    [Fact]
    public void SuggestedKeep_AmongLossy_PrefersHigherBitrate()
    {
        var low = T("A", "B", 200, @"C:\a\low.mp3", bitrate: 128);
        var high = T("A", "B", 200, @"C:\b\high.mp3", bitrate: 320);
        var groups = DuplicateMatcher.FindDuplicates(new[] { low, high });
        Assert.Equal(high.Id, groups[0].SuggestedKeepId);
    }

    [Fact]
    public void SingleCopy_IsNotADuplicate()
    {
        var groups = DuplicateMatcher.FindDuplicates(new[] { T("A", "B", 200, @"C:\a\x.mp3") });
        Assert.Empty(groups);
    }

    [Fact]
    public void BlankArtistOrTitle_IsIgnored()
    {
        var groups = DuplicateMatcher.FindDuplicates(new[]
        {
            T("", "", 200, @"C:\a\x.mp3"),
            T("", "", 200, @"C:\b\y.mp3")
        });
        Assert.Empty(groups);
    }
}
