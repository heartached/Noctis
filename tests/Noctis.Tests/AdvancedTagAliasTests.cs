using Noctis.Converters;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// ffmpeg-style spellings (SORT_ALBUM, SORT_NAME …) used to appear as Custom Tags right
/// under an empty "album sort" box. They now fill the dedicated Advanced field and stay
/// out of the custom list; editing the box clears the alias so the two can't disagree.
/// </summary>
public class AdvancedTagAliasTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), "NoctisTests", Guid.NewGuid().ToString("N"));

    public AdvancedTagAliasTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    private string CreateFlacLikeWav()
    {
        // A WAV carries an ID3v2 tag in TagLib#, and TXXX frames are what the alias
        // reader consults there — same code path the Xiph/APE branches feed.
        var path = Path.Combine(_dir, "alias.wav");
        using (var fs = File.Create(path))
            SilentWavFile.Write(fs, seconds: 1, sampleRate: 8000, channels: 1);
        using (var f = TagLib.File.Create(path))
        {
            f.Tag.Title = "Te Mudaste";
            if (f.GetTag(TagLib.TagTypes.Id3v2, true) is TagLib.Id3v2.Tag id3)
            {
                var frame = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "SORT_ALBUM", true);
                frame.Text = new[] { "ULTIMO TOUR DEL MUNDO" };
                var custom = TagLib.Id3v2.UserTextInformationFrame.Get(id3, "MAJOR_BRAND", true);
                custom.Text = new[] { "M4A" };
            }
            f.Save();
        }
        return path;
    }

    [Fact]
    public void Alias_fills_dedicated_field_and_stays_out_of_custom_tags()
    {
        var path = CreateFlacLikeWav();
        var fields = AdvancedTagIO.ReadAll(path);

        Assert.Equal("ULTIMO TOUR DEL MUNDO", fields.AlbumSort);
        Assert.DoesNotContain(fields.CustomTags, kv => kv.Key.Equals("SORT_ALBUM", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(fields.CustomTags, kv => kv.Key == "MAJOR_BRAND"); // unrelated tags still listed
    }

    [Fact]
    public void Editing_the_dedicated_field_retires_the_alias()
    {
        var path = CreateFlacLikeWav();
        var original = AdvancedTagIO.ReadAll(path);
        var updated = AdvancedTagIO.ReadAll(path);
        updated.AlbumSort = "Last Tour";
        Assert.True(AdvancedTagIO.WriteAll(path, updated, original));

        var reread = AdvancedTagIO.ReadAll(path);
        Assert.Equal("Last Tour", reread.AlbumSort);

        // Clearing the box afterwards must not resurrect the old ffmpeg spelling.
        var cleared = AdvancedTagIO.ReadAll(path);
        cleared.AlbumSort = string.Empty;
        Assert.True(AdvancedTagIO.WriteAll(path, cleared, reread));
        Assert.Equal(string.Empty, AdvancedTagIO.ReadAll(path).AlbumSort);
    }

    [Theory]
    [InlineData("EL ÚLTIMO TOUR DEL MUNDO", "EL ÚLTIMO TOUR DEL ", "MUNDO")]
    [InlineData("Vete", "", "Vete")]
    [InlineData("", "", "")]
    [InlineData("Two words ", "Two ", "words")]
    public void Title_split_keeps_last_word_for_the_badge(string title, string head, string last)
    {
        var (h, l) = TitleSplitConverter.Split(title.TrimEnd());
        Assert.Equal(head, h);
        Assert.Equal(last, l);
    }
}
