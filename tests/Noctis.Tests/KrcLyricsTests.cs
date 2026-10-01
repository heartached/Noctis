using System.IO.Compression;
using System.Text;
using Noctis.Services;
using Noctis.Services.Lyrics;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Kugou KRC (issue #113): "krc1" + zlib XOR-ed with a fixed key, then a line format of
/// <c>[startMs,durMs]&lt;offsetMs,durMs,0&gt;word…</c>. The samples are cut from real
/// lyrics.kugou.com downloads (decrypted 2026-10-01); the converter must hand the lyrics
/// page ELRC it parses into word timings, and drop Kugou's credit lines at the top.
/// </summary>
public class KrcLyricsTests
{
    private static readonly byte[] Key = { 64, 71, 97, 119, 94, 50, 116, 71, 81, 54, 49, 45, 206, 210, 110, 105 };

    /// <summary>The inverse of the download's encoding: zlib, XOR with the key, "krc1" prefix.</summary>
    internal static byte[] Encrypt(string text)
    {
        using var compressed = new MemoryStream();
        using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true))
            z.Write(Encoding.UTF8.GetBytes(text));
        var body = compressed.ToArray();
        var output = new byte[body.Length + 4];
        Encoding.ASCII.GetBytes("krc1").CopyTo(output, 0);
        for (var i = 0; i < body.Length; i++) output[i + 4] = (byte)(body[i] ^ Key[i % Key.Length]);
        return output;
    }

    private const string DaftPunk =
        "[id:$00000000]\n[ti:one more time]\n[ar:daft punk]\n[al:320391]\n[by:]\n[language:eyJjb250ZW50IjogW119]\n" +
        "[30438,1300]<0,608,0>One <608,532,0>more <1140,153,0>time\n" +
        "[46314,1100]<0,500,0>One <500,418,0>more <918,173,0>time\n";

    [Fact]
    public void Decrypt_RoundTripsTheDownloadEncoding()
    {
        Assert.Equal(DaftPunk, KrcLyrics.Decrypt(Encrypt(DaftPunk)));
    }

    [Fact]
    public void Decrypt_RejectsAPayloadWithoutTheKrcMagic()
    {
        Assert.Throws<InvalidDataException>(() => KrcLyrics.Decrypt(Encoding.ASCII.GetBytes("nope, not krc")));
    }

    [Fact]
    public void ToElrc_TimesEveryWordFromTheLineStartPlusItsOffset()
    {
        var elrc = KrcLyrics.ToElrc(DaftPunk);

        Assert.Equal(
            "[00:30.43]<00:30.43>One <00:31.04>more <00:31.57>time<00:31.73>\n" +
            "[00:46.31]<00:46.31>One <00:46.81>more <00:47.23>time<00:47.40>",
            elrc);
    }

    [Fact]
    public void ToElrc_ParsesIntoWordTimingsOnTheLyricsPage()
    {
        var lines = LrcParser.Parse(KrcLyrics.ToElrc(DaftPunk)!);

        var first = lines.First(l => l.IsSynced);
        Assert.Equal("One more time", first.Text);
        Assert.Equal(TimeSpan.FromMilliseconds(30430), first.Timestamp);
        Assert.NotNull(first.Words);
        Assert.Equal(3, first.Words!.Count);
        Assert.Equal(TimeSpan.FromMilliseconds(31040), first.Words[1].Start);
    }

    [Fact]
    public void ToElrc_DropsKugousTitleAndCreditLinesAtTheTop()
    {
        // Real head of Adele - Hello: a "title (translation) - artist" line, then credits
        // with a full-width colon, each token-split with lone-space tokens.
        const string krc =
            "[ti:Hello]\n[ar:Adele]\n" +
            "[1800,1640]<0,102,0>Hello<102,102,0> <204,102,0>(<306,102,0>你<408,102,0>好<510,102,0>)<612,102,0> <714,102,0>-<816,102,0> <918,102,0>Adele\n" +
            "[3440,1650]<0,235,0>Lyrics<235,235,0> <470,235,0>by<705,235,0>：<940,235,0>Adele<1175,235,0> <1410,235,0>Adkins\n" +
            "[5090,1650]<0,150,0>Composed<150,150,0> <300,150,0>by<450,150,0>：<600,150,0>Greg\n" +
            "[6591,3110]<0,2660,0>Hello <2660,150,0>it's <2810,300,0>me\n";

        Assert.Equal("[00:06.59]<00:06.59>Hello <00:09.25>it's <00:09.40>me<00:09.70>", KrcLyrics.ToElrc(krc));
    }

    [Fact]
    public void ToElrc_FoldsLoneSpaceTokensIntoThePreviousWord()
    {
        const string krc = "[1000,800]<0,200,0>Hello<200,100,0> <300,500,0>world\n";

        Assert.Equal("[00:01.00]<00:01.00>Hello <00:01.30>world<00:01.80>", KrcLyrics.ToElrc(krc));
    }

    [Fact]
    public void ToElrc_KeepsUnspacedCjkWordsTogether()
    {
        const string krc = "[1000,900]<0,300,0>你<300,300,0>好<600,300,0>吗\n";

        Assert.Equal("[00:01.00]<00:01.00>你<00:01.30>好<00:01.60>吗<00:01.90>", KrcLyrics.ToElrc(krc));
    }

    [Fact]
    public void ToElrc_KeepsALineWithoutWordTagsAsPlainLineSync()
    {
        Assert.Equal("[00:05.00]just a line", KrcLyrics.ToElrc("[5000,1000]just a line\n"));
    }

    [Fact]
    public void ToElrc_ReturnsNullWhenThereAreNoLyricLines()
    {
        Assert.Null(KrcLyrics.ToElrc("[ti:x]\n[ar:y]\n"));
    }
}
