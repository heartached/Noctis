using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Noctis.Localization;
using Noctis.Models;
using Noctis.ViewModels;
using Noctis.Views;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Count strings that put a number before a fixed noun ("{0} أغانٍ", "منذ {0} أشهر", "{1} треков")
/// read right only for some numbers (Arabic review, 11-19). They go through Loc.Plural now, with
/// Arabic zero|one|two|few|many|other, Russian one|few|many where the noun follows the number,
/// and English one|other where the call site can show 1.
/// </summary>
[Collection("Localization")] // Loc.Instance is process-wide: these tests must not interleave
public class CountStringPluralTests
{
    /// <summary>Every key the code now reads with Loc.Plural, with the extra args its call site passes.</summary>
    private static readonly (string Key, object[] Args)[] PluralKeys =
    {
        ("Playlist.DragCount", Array.Empty<object>()),
        ("Home.Plays", Array.Empty<object>()),
        ("LibraryAlbums.Plays", Array.Empty<object>()),
        ("LibrarySongs.SummarySongs", Array.Empty<object>()),
        ("LibrarySongs.SummaryHours", Array.Empty<object>()),
        ("LibrarySongs.SummaryMinutes", Array.Empty<object>()),
        ("ArtistInfoSearch.AlbumsCount", Array.Empty<object>()),
        ("Stats.MonthsAgo", Array.Empty<object>()),
        ("Stats.YearsAgo", Array.Empty<object>()),
        ("Time.MinutesAgo", Array.Empty<object>()),
        ("Time.HoursAgo", Array.Empty<object>()),
        ("CreateSmartPlaylist.TracksSortedBy", Array.Empty<object>()),
        ("Main.DropMovedMany", new object[] { "Music" }),
        ("Metadata.ArtistsSelected", Array.Empty<object>()),
        ("Metadata.SongsSelected", Array.Empty<object>()),
        ("BulkArtwork.TitleMany", Array.Empty<object>()),
        ("BulkLyrics.StudioOfferMany", Array.Empty<object>()),
        ("Settings.ArtistCreditsUpdated", Array.Empty<object>()),
        ("Metadata.WriteFailedMany", new object[] { "a.flac" }),
    };

    private static void AssertClean(string text, string what)
    {
        Assert.False(text.Contains('|'), $"{what}: leftover '|' in \"{text}\"");
        Assert.False(text.Contains('{'), $"{what}: leftover '{{' in \"{text}\"");
        Assert.False(string.IsNullOrWhiteSpace(text), $"{what}: empty");
    }

    [AvaloniaFact]
    public void EveryLanguage_RendersEveryCountKey_WithoutLeftovers()
    {
        try
        {
            foreach (var culture in Loc.KnownCultures.Prepend("en"))
            {
                Loc.Instance.SetCulture(culture);
                foreach (var (key, args) in PluralKeys)
                    foreach (var n in new long[] { 0, 1, 2, 3, 5, 11, 21, 100, 1234 })
                        AssertClean(Loc.Plural(key, n, args), $"{culture} {key} n={n}");
            }
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    [AvaloniaFact]
    public void Arabic_HasSixForms_AndEachCountTakesItsOwn()
    {
        try
        {
            Loc.Instance.SetCulture("ar");
            foreach (var (key, args) in PluralKeys)
            {
                Assert.Equal(6, Loc.T(key).Split('|').Length);
                foreach (var n in new long[] { 0, 1, 2, 3, 11, 100 })
                    AssertClean(Loc.Plural(key, n, args), $"ar {key} n={n}");
            }

            string P(string key, long n, params object[] a) => Loc.Plural(key, n, a);
            // zero, one, two, few (3–10), many (11–99), other (100+)
            Assert.Equal(new[] { "0 أغنية", "1 أغنية", "2 أغنيتان", "3 أغانٍ", "11 أغنية", "100 أغنية" },
                new long[] { 0, 1, 2, 3, 11, 100 }.Select(n => P("Playlist.DragCount", n)));
            Assert.Equal(new[] { "مرة تشغيل", "مرة تشغيل", "مرتا تشغيل", "مرات تشغيل", "مرة تشغيل", "مرة تشغيل" },
                new long[] { 0, 1, 2, 3, 11, 100 }.Select(n => P("Home.Plays", n)));
            Assert.Equal("مرات تشغيل", P("LibraryAlbums.Plays", 3));
            Assert.Equal(new[] { "0 أغنية", "2 أغنيتان", "3 أغانٍ", "11 أغنية", "100 أغنية" },
                new long[] { 0, 2, 3, 11, 100 }.Select(n => P("LibrarySongs.SummarySongs", n)));
            Assert.Equal(new[] { "2 ساعتان", "3 ساعات", "11 ساعة", "100 ساعة" },
                new long[] { 2, 3, 11, 100 }.Select(n => P("LibrarySongs.SummaryHours", n)));
            Assert.Equal(new[] { "0 دقيقة", "2 دقيقتان", "3 دقائق", "11 دقيقة" },
                new long[] { 0, 2, 3, 11 }.Select(n => P("LibrarySongs.SummaryMinutes", n)));
            Assert.Equal(new[] { "2 ألبومان", "3 ألبومات", "11 ألبومًا", "100 ألبوم" },
                new long[] { 2, 3, 11, 100 }.Select(n => P("ArtistInfoSearch.AlbumsCount", n)));
            Assert.Equal(new[] { "منذ شهر", "منذ شهرين", "منذ 3 أشهر", "منذ 11 شهرًا", "منذ 100 شهر" },
                new long[] { 1, 2, 3, 11, 100 }.Select(n => P("Stats.MonthsAgo", n)));
            Assert.Equal(new[] { "منذ سنتين", "منذ 3 سنوات", "منذ 11 سنة", "منذ 100 سنة" },
                new long[] { 2, 3, 11, 100 }.Select(n => P("Stats.YearsAgo", n)));
            Assert.Equal(new[] { "منذ دقيقة", "منذ دقيقتين", "منذ 3 دقائق", "منذ 11 دقيقة" },
                new long[] { 1, 2, 3, 11 }.Select(n => P("Time.MinutesAgo", n)));
            Assert.Equal(new[] { "منذ ساعة", "منذ ساعتين", "منذ 3 ساعات", "منذ 11 ساعة" },
                new long[] { 1, 2, 3, 11 }.Select(n => P("Time.HoursAgo", n)));
            Assert.Equal(new[] { "مقطع، مرتبة حسب", "مقطعان، مرتبة حسب", "مقاطع، مرتبة حسب", "مقطعًا، مرتبة حسب", "مقطع، مرتبة حسب" },
                new long[] { 1, 2, 3, 11, 100 }.Select(n => P("CreateSmartPlaylist.TracksSortedBy", n)));
            Assert.Equal(new[] { "تم نقل ملفين إلى Music", "تم نقل 3 ملفات إلى Music", "تم نقل 11 ملفًا إلى Music", "تم نقل 100 ملف إلى Music" },
                new long[] { 2, 3, 11, 100 }.Select(n => P("Main.DropMovedMany", n, "Music")));
            Assert.Equal(new[] { "تم تحديد فنان واحد", "تم تحديد فنانَين", "تم تحديد 3 فنانين", "تم تحديد 11 فنانًا" },
                new long[] { 1, 2, 3, 11 }.Select(n => P("Metadata.ArtistsSelected", n)));
            Assert.Equal(new[] { "تم تحديد أغنيتين", "تم تحديد 3 أغانٍ", "تم تحديد 11 أغنية", "تم تحديد 100 أغنية" },
                new long[] { 2, 3, 11, 100 }.Select(n => P("Metadata.SongsSelected", n)));
            Assert.Equal(new[] { "البحث عن أغلفة متحركة لألبومين", "البحث عن أغلفة متحركة لـ 3 ألبومات", "البحث عن أغلفة متحركة لـ 11 ألبومًا" },
                new long[] { 2, 3, 11 }.Select(n => P("BulkArtwork.TitleMany", n)));
            Assert.StartsWith("أغنيتان لهما كلمات عادية فقط.", P("BulkLyrics.StudioOfferMany", 2));
            Assert.StartsWith("3 أغانٍ لها كلمات عادية فقط.", P("BulkLyrics.StudioOfferMany", 3));
            Assert.StartsWith("11 أغنية لها كلمات عادية فقط.", P("BulkLyrics.StudioOfferMany", 11));
            Assert.Equal(new[] { "تم تحديث بيانات الفنانين في مقطعين.", "تم تحديث بيانات الفنانين في 3 مقاطع.", "تم تحديث بيانات الفنانين في 11 مقطعًا." },
                new long[] { 2, 3, 11 }.Select(n => P("Settings.ArtistCreditsUpdated", n)));
            Assert.StartsWith("تعذّرت كتابة الوسوم في ملفين (مثل \"a.flac\").", P("Metadata.WriteFailedMany", 2, "a.flac"));
            Assert.StartsWith("تعذّرت كتابة الوسوم في 11 ملفًا (مثل \"a.flac\").", P("Metadata.WriteFailedMany", 11, "a.flac"));

            // Two counts in one sentence: Loc.Plural picks by one count only, so the Arabic
            // reads them as labels, right for any number.
            var confirm = Loc.T("Main.DropCreatePlaylistConfirm", "Mix", 11, 2);
            AssertClean(confirm, "ar Main.DropCreatePlaylistConfirm");
            Assert.Contains("(المقاطع: 11، الألبومات: 2)", confirm);
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    [AvaloniaFact]
    public void Russian_NounsAfterTheCount_FollowOneFewMany()
    {
        try
        {
            Loc.Instance.SetCulture("ru");
            foreach (var (key, args) in PluralKeys)
                foreach (var n in new long[] { 1, 2, 5, 21 })
                    AssertClean(Loc.Plural(key, n, args), $"ru {key} n={n}");

            Assert.Equal(new[] { "1 год назад", "2 года назад", "5 лет назад", "21 год назад" },
                new long[] { 1, 2, 5, 21 }.Select(n => Loc.Plural("Stats.YearsAgo", n)));
            Assert.Equal(new[] { "трека, сортировка по", "треков, сортировка по", "треков, сортировка по", "трека, сортировка по" },
                new long[] { 1, 2, 5, 21 }.Select(n => Loc.Plural("CreateSmartPlaylist.TracksSortedBy", n)));
            Assert.StartsWith("У 21 песни есть только обычный текст.", Loc.Plural("BulkLyrics.StudioOfferMany", 21));
            Assert.StartsWith("У 2 песен есть только обычный текст.", Loc.Plural("BulkLyrics.StudioOfferMany", 2));
            Assert.StartsWith("У 5 песен есть только обычный текст.", Loc.Plural("BulkLyrics.StudioOfferMany", 5));
            Assert.Equal("Выбрано песен: 21", Loc.Plural("Metadata.SongsSelected", 21));
            Assert.Equal("Исполнители обновлены у треков: 2.", Loc.Plural("Settings.ArtistCreditsUpdated", 2));

            var confirm = Loc.T("Main.DropCreatePlaylistConfirm", "Mix", 22, 2);
            Assert.StartsWith("«Mix» содержит треки из разных альбомов (треков: 22, альбомов: 2).", confirm);
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    [AvaloniaFact]
    public void English_OneAndOther()
    {
        try
        {
            Loc.Instance.SetCulture("en");
            foreach (var (key, args) in PluralKeys)
                foreach (var n in new long[] { 1, 2 })
                    AssertClean(Loc.Plural(key, n, args), $"en {key} n={n}");

            Assert.Equal("1 artist selected", Loc.Plural("Metadata.ArtistsSelected", 1));
            Assert.Equal("2 artists selected", Loc.Plural("Metadata.ArtistsSelected", 2));
            Assert.Equal("track, sorted by", Loc.Plural("CreateSmartPlaylist.TracksSortedBy", 1));
            Assert.Equal("tracks, sorted by", Loc.Plural("CreateSmartPlaylist.TracksSortedBy", 2));
            Assert.Equal("2 songs", Loc.Plural("Playlist.DragCount", 2));
            Assert.Equal("plays", Loc.Plural("Home.Plays", 2));
            Assert.Equal("Moved 2 files to Music", Loc.Plural("Main.DropMovedMany", 2, "Music"));
            Assert.Equal("1,234 songs", Loc.Plural("LibrarySongs.SummarySongs", 1234));
            Assert.Equal("Artist credits updated on 1,234 tracks.", SettingsViewModel.ArtistCreditsUpdatedText(1234));
            Assert.Equal("2 min ago", SettingsViewModel.Ago(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc), new DateTime(2026, 1, 1, 12, 2, 0, DateTimeKind.Utc)));
        }
        finally { Loc.Instance.SetCulture("en"); }
    }

    /// <summary>The smart playlist's limit sentence: "Limit to [1] track, sorted by", the words
    /// following the number box, and re-read on a language switch.</summary>
    [AvaloniaFact]
    public void SmartPlaylistLimitWords_FollowTheNumber_AndTheLanguage()
    {
        var lib = new FakeLibraryService();
        lib.TrackList.Add(new Track { Id = Guid.NewGuid(), Title = "A", Artist = "B", Album = "C" });
        var vm = new CreateSmartPlaylistDialogViewModel(lib);
        var win = new CreateSmartPlaylistDialog { DataContext = vm, Width = 1100, Height = 820 };
        try
        {
            Loc.Instance.SetCulture("en");
            win.Show();
            Dispatcher.UIThread.RunJobs();
            var words = win.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "LimitWords");

            vm.LimitCount = 1;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("track, sorted by", words.Text);
            vm.LimitCount = 25;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("tracks, sorted by", words.Text);

            Loc.Instance.SetCulture("ar");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("مقطعًا، مرتبة حسب", words.Text);
            vm.LimitCount = 3;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("مقاطع، مرتبة حسب", words.Text);
        }
        finally
        {
            Loc.Instance.SetCulture("en");
            win.Close();
        }
    }
}
