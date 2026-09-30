using Noctis.Services.LyricsStudio;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Which language Lyrics Studio tells Whisper to listen for. Benchmark 09-29: a Spanish song came
/// out as Russian (WER 0.996, 59 of 63 lines unanchored) because the first window that yielded
/// text named the language for the whole song; known lyrics name it far more reliably.
/// </summary>
public class LyricsStudioLanguageTests
{
    [Fact]
    public void Spanish_Lyrics_AreSpanish()
    {
        var g = LyricsLanguage.FromLines(new[]
        {
            "Mañana vamos al mercado con mi hermano",
            "El tren sale de la estación a las ocho",
            "Ella compró pan y leche para la cena",
            "No encuentro las llaves de la casa",
        });

        Assert.Equal("es", g.Code);
        Assert.True(g.Confident);
    }

    [Fact]
    public void English_Lyrics_AreEnglish()
    {
        var g = LyricsLanguage.FromLines(new[]
        {
            "We walked to the station and waited for the train",
            "The kids are playing in the garden with their dog",
            "I think it is going to rain this afternoon",
            "She left her keys on the kitchen table",
        });

        Assert.Equal("en", g.Code);
        Assert.True(g.Confident);
    }

    [Theory]
    [InlineData("오늘 날씨가 정말 좋네요", "ko")]
    [InlineData("明日は駅で友達に会います", "ja")]
    [InlineData("我们明天去图书馆看书", "zh")]
    [InlineData("мы завтра поедем на дачу", "ru")]
    [InlineData("ми їдемо до міста завтра і повернемося ввечері", "uk")]
    [InlineData("ο καιρός είναι καλός σήμερα", "el")]
    [InlineData("מחר נלך לשוק", "he")]
    [InlineData("कल हम बाज़ार जाएंगे", "hi")]
    [InlineData("วันนี้อากาศดีมาก", "th")]
    public void NonLatinScript_NamesItsLanguage(string line, string code)
    {
        var g = LyricsLanguage.FromLines(new[] { line, line, line });

        Assert.Equal(code, g.Code);
        Assert.True(g.Confident);
    }

    [Fact]
    public void KoreanSongWithEnglishLines_IsKorean()
    {
        var g = LyricsLanguage.FromLines(new[]
        {
            "We start the meeting at nine",
            "Please send me the report today",
            "회의는 아홉 시에 시작해요",
            "보고서를 오늘 보내 주세요",
        });

        Assert.Equal("ko", g.Code);
        Assert.True(g.Confident);
    }

    [Fact]
    public void BilingualLatinSong_LeavesTheChoiceToTheAudio_BetweenTheCloseOnes()
    {
        var g = LyricsLanguage.FromLines(new[]
        {
            "I want to go to the park and the beach",
            "Quiero ir al parque y a la playa con mi hermano",
            "We can take the bus to the city",
            "Porque el coche de mi padre no funciona",
        });

        Assert.False(g.Confident);
        Assert.Contains("en", g.Candidates);
        Assert.Contains("es", g.Candidates);
    }

    [Fact]
    public void LatinTextWithoutTelltaleWords_OffersTheLatinLanguages()
    {
        var g = LyricsLanguage.FromLines(new[] { "Skrrt skrrt", "Brr brr", "Ooh ooh" });

        Assert.False(g.Confident);
        Assert.Contains("en", g.Candidates);
        Assert.DoesNotContain("ru", g.Candidates);
    }

    [Fact]
    public void NoText_NoGuess()
    {
        Assert.Null(LyricsLanguage.FromLines(null).Code);
        Assert.Null(LyricsLanguage.FromLines(new[] { "123", "..." }).Code);
    }

    // ── Where the audio is asked ──────────────────────────────────────────────

    [Fact]
    public void LanguageWindows_AreSpreadOverTheMiddleOfTheSong()
    {
        const int sr = 16000;
        var windows = WhisperTranscriber.LanguageWindows(200 * sr, 3).ToList();

        Assert.Equal(new[] { (35 * sr, 30 * sr), (85 * sr, 30 * sr), (135 * sr, 30 * sr) }, windows);
    }

    [Fact]
    public void LanguageWindows_ShortSong_IsOneWindowOfAllOfIt()
    {
        Assert.Equal(new[] { (0, 20 * 16000) }, WhisperTranscriber.LanguageWindows(20 * 16000, 3).ToArray());
    }
}
