using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Noctis.Mobile.Views;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// What the phone tells people about its network use follows the code: the Android privacy
/// policy names every service the phone asks (the hosts are read from the code), Settings →
/// About says the same in one sentence, and the website's copy of the policy is the
/// Markdown word for word (checked when the website checkout sits beside this one).
/// </summary>
public class MobilePrivacyTextTests
{
    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Noctis.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate repo root from " + AppContext.BaseDirectory);
    }

    private static string Policy() =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "store", "android", "privacy-policy.md")).Replace("\r\n", "\n");

    [Fact]
    public void ThePolicy_NamesEveryServiceThePhoneAsks_AndLinksTheirPolicies()
    {
        var policy = Policy();
        foreach (var url in new[] { DeezerArtistPhotos.SearchUrl, DeezerArtistPhotos.ArtistUrl, LastFmApi.ApiBase })
            Assert.Contains(new Uri(url).Host, policy);
        Assert.Contains("cdn-images.dzcdn.net", policy);                       // where Deezer's photo URLs point
        Assert.Contains("**Deezer, for artist photos.**", policy);
        Assert.Contains("https://www.deezer.com/legal/personal-datas", policy);
        Assert.Contains("https://www.last.fm/legal/privacy", policy);
        Assert.Contains("It connects to three\nplaces.", policy);
        Assert.Contains("artist photos, favourites", policy);                 // the backup list
    }

    [AvaloniaFact]
    public void SettingsAbout_SaysWhatEachLookupSends()
    {
        var page = new SettingsPage();
        var note = page.FindControl<TextBlock>("PrivacyNote")!.Text!;
        Assert.Contains("album descriptions on Last.fm, sending only the artist and album name", note);
        Assert.Contains("artist photos on Deezer, sending only the artist's name", note);
    }

    /// <summary>The site's page (website/src/pages/privacy/android.astro in the Noctis-website
    /// checkout next to this one) against the Markdown, markup and the pages' own title and date
    /// lines left out.</summary>
    [Fact]
    public void TheWebsitesPolicy_IsTheMarkdownWordForWord()
    {
        var root = FindRepoRoot();
        var astroPath = Path.Combine(Path.GetDirectoryName(root)!, "Noctis-website", "website", "src", "pages", "privacy", "android.astro");
        if (!File.Exists(astroPath)) Assert.Skip("no Noctis-website checkout beside this one");

        var paragraphs = Policy().Split("\n\n");
        var md = string.Join("\n\n", paragraphs.Skip(2));                      // title, date note
        md = Regex.Replace(md, "^#+ ", "", RegexOptions.Multiline);
        md = Regex.Replace(md, @"^\s*- ", "", RegexOptions.Multiline);
        md = md.Replace("**", "").Replace("`", "");

        var astro = File.ReadAllText(astroPath).Replace("\r\n", "\n");
        var start = astro.IndexOf("<p class=\"lede pp__lede\">", StringComparison.Ordinal);
        var section = astro[start..astro.IndexOf("</section>", start, StringComparison.Ordinal)];
        section = Regex.Replace(section, "<p class=\"pp__meta\">.*?</p>", "", RegexOptions.Singleline);
        section = section.Replace("{links.issues}", "https://github.com/heartached/Noctis/issues");
        section = Regex.Replace(section, "<[^>]+>", " ");

        Assert.Equal(Words(md), Words(section));
    }

    private static string Words(string text)
    {
        text = WebUtility.HtmlDecode(text).Replace('’', '\'').Replace('‘', '\'');
        text = Regex.Replace(text, @"\s+", " ").Trim();
        return Regex.Replace(text, @" ([.,;:])", "$1");
    }
}
