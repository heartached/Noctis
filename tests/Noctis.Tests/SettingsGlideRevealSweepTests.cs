using System.Text.RegularExpressions;
using System.Xml.Linq;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Owner 11-14: every switch (or choice) in Settings that reveals rows, hints or notes under
/// it has to open AND close with the same glide, not pop in and out. A structural sweep over
/// SettingsView.axaml: the reveals are CollapsibleContent glides bound to the right property,
/// none of them is left as a bare IsVisible flip, and none sits where it would snap by a
/// panel's Spacing on the last frame of its close.
/// </summary>
public class SettingsGlideRevealSweepTests
{
    /// <summary>Every property that drives a reveal inside a settings card.</summary>
    public static readonly TheoryData<string> GlideBindings = new()
    {
        // General
        "LaunchAtStartupError", "FileTypesStatus",
        // Appearance
        "EnableAnimatedCovers", "ShowAnimatedArtworkCacheOptions", "AlbumPageTintEnabled",
        // Player
        "PlaybackBarShowStop", "PlaybackBarShowQuickPlaylist", "PlaybackBarShowTrash", "PlaybackBarShowSkipButtons",
        "ShowTrashFolderMissingHint", "ShowArtworkLyricsHint", "MiniPlayerFrostUnavailable",
        // Lyrics
        "LyricsPresetStatus", "LyricsVisualizerEnabled", "IsDriftStyle", "IsKawarpStyle",
        "MusicVideoUseBrowserCookies", "ShowMusicVideoToolInstall", "HasMusicVideoProblem",
        // Audio
        "ExclusiveAudioEnabled", "IsCrossfadeStyle", "PlayPauseFadeEnabled", "OutputDeviceMissing",
        "ExclusiveAudioStatus", "MultichannelOutputNeedsRestart", "HeadphoneSurroundNeedsRestart", "ChannelMixNote",
        // Library
        "HasHiddenLibraryFolders", "IgnoreLeadingWordsInArtistSort", "HideSmallArtists",
        // Advanced
        "IsDataFolderCopying", "IsDataFolderUseExistingVisible", "IsDataFolderInfoVisible", "IsDataFolderErrorVisible",
        "IsDataFolderRestartNeeded", "IsResetConfirmVisible", "DeveloperMode", "DevStatusText", "IsDevDownloading",
        "PreservedCrashBanner",
        // Account
        "HasPrimaryAccount", "NoctisServerStartFailed", "IsIssuedApiKeyOpen", "WebRemoteStartFailed",
        "LocalApiTokenRevealed", "LocalApiError",
        // Integrations
        "LastFmUsername", "LastFmQueueNote", "LastFmImportStatus", "IsLastFmConnected",
        "ListenBrainzUsername", "ListenBrainzServerDisplay", "ListenBrainzQueueNote", "ListenBrainzError",
        "ListenBrainzImportStatus", "IsListenBrainzConnected", "IsTidalConnected", "IsMediaServerConnected",
        // Plugins / About
        "PluginsStatus", "IsPermissionsExpanded", "UpdateStatusText",
    };

    /// <summary>
    /// Notes and reveals with no instant twin: their property must not appear as an IsVisible
    /// binding anywhere in the view. (Connected/disconnected button pairs swap in place and stay
    /// instant, so those properties are not listed here.)
    /// </summary>
    public static readonly TheoryData<string> NeverBareIsVisible = new()
    {
        "LaunchAtStartupError", "FileTypesStatus", "EnableAnimatedCovers", "ShowTrashFolderMissingHint",
        "ShowArtworkLyricsHint", "MiniPlayerFrostUnavailable", "LyricsPresetStatus", "LyricsVisualizerEnabled",
        "MusicVideoUseBrowserCookies", "ShowMusicVideoToolInstall", "HasMusicVideoProblem",
        "OutputDeviceMissing", "ExclusiveAudioStatus", "MultichannelOutputNeedsRestart",
        "HeadphoneSurroundNeedsRestart", "ChannelMixNote", "HasHiddenLibraryFolders", "IsDataFolderCopying",
        "IsDataFolderInfoVisible", "IsDataFolderErrorVisible", "IsDataFolderRestartNeeded", "DeveloperMode",
        "DevStatusText", "IsDevDownloading", "PreservedCrashBanner", "NoctisServerStartFailed",
        "WebRemoteStartFailed", "LocalApiError", "LastFmUsername", "LastFmQueueNote", "LastFmImportStatus",
        "ListenBrainzUsername", "ListenBrainzServerDisplay", "ListenBrainzQueueNote", "ListenBrainzError",
        "ListenBrainzImportStatus", "PluginsStatus", "UpdateStatusText",
    };

    private static readonly Lazy<(string Text, XDocument Doc)> View = new(() =>
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null && !Directory.Exists(Path.Combine(dir, "src", "Noctis")))
            dir = Path.GetDirectoryName(dir);
        Assert.NotNull(dir);
        var text = File.ReadAllText(Path.Combine(dir!, "src", "Noctis", "Views", "SettingsView.axaml"));
        return (text, XDocument.Parse(text));
    });

    private static IEnumerable<XElement> Collapsibles =>
        View.Value.Doc.Descendants().Where(e => e.Name.LocalName == "CollapsibleContent");

    /// <summary>"{Binding !Foo, Converter=...}" → "Foo".</summary>
    private static string Property(string? binding) =>
        Regex.Match(binding ?? "", @"\{Binding\s+!?([\w.]+)").Groups[1].Value;

    [Theory]
    [MemberData(nameof(GlideBindings))]
    public void Reveal_IsAGlideBoundToItsProperty(string property)
    {
        var bound = Collapsibles.Where(c => Property((string?)c.Attribute("IsOpen")) == property).ToList();
        Assert.True(bound.Count > 0, $"no CollapsibleContent opens on {property}");
        Assert.All(bound, c => Assert.Equal("Glide", (string?)c.Attribute("Motion")));
    }

    [Theory]
    [MemberData(nameof(NeverBareIsVisible))]
    public void Reveal_IsNotLeftAsABareIsVisibleFlip(string property)
    {
        var bare = Regex.Matches(View.Value.Text, @"IsVisible=""\{Binding\s+!?" + Regex.Escape(property) + @"[\s,}]");
        Assert.True(bare.Count == 0, $"{property} still flips IsVisible instantly ({bare.Count}x)");
    }

    [Fact]
    public void EveryCollapsible_InSettings_PlaysTheSameGlide()
    {
        var off = Collapsibles.Where(c => (string?)c.Attribute("Motion") != "Glide" || (string?)c.Attribute("Lift") != "10")
            .Select(c => (string?)c.Attribute("IsOpen")).ToList();
        Assert.True(off.Count == 0, "not on the 10px glide: " + string.Join(", ", off));
    }

    /// <summary>
    /// The known trap: a collapsible straight inside a StackPanel with Spacing snaps by that
    /// spacing on its close's last frame (the gap belongs to a visible child and vanishes when the
    /// shut collapsible turns IsVisible off). The gap has to live on the body's margin instead.
    /// </summary>
    [Fact]
    public void NoCollapsible_SitsInAPanelsSpacing()
    {
        var trapped = Collapsibles.Where(c =>
                c.Parent is { } p && p.Name.LocalName == "StackPanel"
                && p.Attribute("Spacing") is { } s && s.Value != "0"
                && (string?)p.Attribute("Orientation") != "Horizontal")
            .Select(c => (string?)c.Attribute("IsOpen")).ToList();
        Assert.True(trapped.Count == 0, "collapsibles inside a Spacing panel: " + string.Join(", ", trapped));
    }

    /// <summary>A whole setting-row that folds keeps its top hairline (it is its fold's first child).</summary>
    [Fact]
    public void FoldedSettingRows_KeepTheirHairline()
    {
        var rows = Collapsibles.Select(c => c.Elements().Single())
            .Where(b => b.Name.LocalName == "Border" && ((string?)b.Attribute("Classes") ?? "").Split(' ').Contains("setting-row"))
            .ToList();
        Assert.NotEmpty(rows);
        Assert.All(rows, b => Assert.Contains("divided", ((string?)b.Attribute("Classes"))!.Split(' ')));
    }

    /// <summary>Owner's screenshot: Artwork colour, Full spectrum and the three styles are ONE block.</summary>
    [Fact]
    public void AudioVisualizer_RevealsItsThreePartsAsOneGlide()
    {
        var block = Assert.Single(Collapsibles, c => Property((string?)c.Attribute("IsOpen")) == "LyricsVisualizerEnabled");
        var inner = block.Descendants().ToList();
        Assert.Contains(inner, e => (string?)e.Attribute("IsChecked") == "{Binding LyricsVisualizerArtworkColor}");
        Assert.Contains(inner, e => (string?)e.Attribute("IsChecked") == "{Binding LyricsVisualizerFullSpectrum}");
        foreach (var style in new[] { "Bars", "Mirror", "Wave" })
            Assert.Contains(inner, e => (string?)e.Attribute("IsChecked") == $"{{Binding IsVisualizerStyle{style}, Mode=TwoWay}}");
        // The master switch stays outside, above the block.
        Assert.DoesNotContain(inner, e => (string?)e.Attribute("IsChecked") == "{Binding LyricsVisualizerEnabled}");
    }

    /// <summary>Names the code-behind and the accessibility tree rely on survive the wrapping.</summary>
    [Theory]
    [InlineData("MiniPlayerFrostUnavailableNote", "MiniPlayerFrostUnavailable")]
    [InlineData("AccountIdentityLine", "HasPrimaryAccount")]
    [InlineData("ApiKeyPanel", "IsIssuedApiKeyOpen")]
    public void NamedNote_LivesInsideItsGlide(string name, string property)
    {
        var x = XNamespace.Get("http://schemas.microsoft.com/winfx/2006/xaml");
        var el = Assert.Single(View.Value.Doc.Descendants(), e => (string?)e.Attribute(x + "Name") == name);
        Assert.Null(el.Attribute("IsVisible"));
        var host = el.Ancestors().First(a => a.Name.LocalName == "CollapsibleContent");
        Assert.Equal(property, Property((string?)host.Attribute("IsOpen")));
    }
}
