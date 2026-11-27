using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// Settings › About › What's new: the release body parser, on trimmed copies of real GitHub
/// release bodies (Fixtures/WhatsNew, fetched 11-26) and on small hand-written cases.
/// </summary>
public class WhatsNewParserTests
{
    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "WhatsNew", name + ".md"));

    private static IReadOnlyList<ReleaseNoteItem> Items(IReadOnlyList<ReleaseNoteSection> sections, ReleaseNoteKind kind) =>
        sections.Single(s => s.Kind == kind).Items;

    [Fact]
    public void AppRelease_GroupsInAddedImprovedFixedOrder_AndDropsTheDownloadPreamble()
    {
        var sections = ReleaseNotesParser.Parse(Fixture("v1.7.2"));

        // The body lists Fixed, Added, Improved; the dialog reads Added, Improved, Fixed. The
        // Discord badge, the download table, the macOS note with its code block and the
        // "Full Changelog" line are not changes, so there is no Other group.
        Assert.Equal(new[] { ReleaseNoteKind.Added, ReleaseNoteKind.Improved, ReleaseNoteKind.Fixed },
            sections.Select(s => s.Kind));
        Assert.Equal(4, Items(sections, ReleaseNoteKind.Added).Count);
        Assert.Equal(4, Items(sections, ReleaseNoteKind.Improved).Count);
        Assert.Equal(4, Items(sections, ReleaseNoteKind.Fixed).Count);

        var first = Items(sections, ReleaseNoteKind.Fixed)[0];
        Assert.Equal("Fixed album cards not opening their album in Masonry view.", first.Title);
        Assert.Null(first.Detail);
        Assert.Equal(new[] { 262 }, first.Issues);

        var noIssue = Items(sections, ReleaseNoteKind.Added)[3];
        Assert.Equal("Added Keep Shuffle On.", noIssue.Title);
        Assert.Empty(noIssue.Issues);

        Assert.DoesNotContain(sections.SelectMany(s => s.Items),
            i => i.Title.Contains("xattr") || i.Title.Contains("Changelog") || i.Title.Contains('|'));
    }

    [Fact]
    public void CrLfBody_ReadsTheSame()
    {
        var lf = ReleaseNotesParser.Parse(Fixture("v1.7.2"));
        var crlf = ReleaseNotesParser.Parse(Fixture("v1.7.2").Replace("\n", "\r\n"));
        Assert.Equal(lf.SelectMany(s => s.Items.Select(i => i.Title)), crlf.SelectMany(s => s.Items.Select(i => i.Title)));
    }

    [Fact]
    public void PluginRelease_UnknownThanksSection_GoesToOther()
    {
        var sections = ReleaseNotesParser.Parse(Fixture("plugin-lyricmotion-v1.1.0"));

        Assert.Equal(new[] { ReleaseNoteKind.Improved, ReleaseNoteKind.Fixed, ReleaseNoteKind.Other },
            sections.Select(s => s.Kind));
        var thanks = Assert.Single(Items(sections, ReleaseNoteKind.Other));
        Assert.Equal("@Alfamin for this version.", thanks.Title);
        Assert.Equal(new[] { 168 }, thanks.Issues);
        // The description line above "What's Changed" is a paragraph, not a change.
        Assert.DoesNotContain(sections.SelectMany(s => s.Items), i => i.Title.StartsWith("Lyrics that grow"));
    }

    [Fact]
    public void SingleSectionRelease_HasOnlyThatGroup()
    {
        var sections = ReleaseNotesParser.Parse(Fixture("plugin-albumroulette-v1.0.2"));
        var added = Assert.Single(sections);
        Assert.Equal(ReleaseNoteKind.Added, added.Kind);
        Assert.Equal(new[] { 202 }, Assert.Single(added.Items).Issues);
    }

    [Theory]
    [InlineData("## Added", ReleaseNoteKind.Added)]
    [InlineData("### New", ReleaseNoteKind.Added)]
    [InlineData("**New features**", ReleaseNoteKind.Added)]
    [InlineData("### Improved", ReleaseNoteKind.Improved)]
    [InlineData("## Changed", ReleaseNoteKind.Improved)]
    [InlineData("### Changes:", ReleaseNoteKind.Improved)]
    [InlineData("### FIXED", ReleaseNoteKind.Fixed)]
    [InlineData("## Bug fixes", ReleaseNoteKind.Fixed)]
    [InlineData("**Fixes:**", ReleaseNoteKind.Fixed)]
    [InlineData("### Thanks", ReleaseNoteKind.Other)]
    [InlineData("## What's Changed", ReleaseNoteKind.Other)]
    public void HeadingVariants_StartTheirSection(string heading, ReleaseNoteKind kind)
    {
        var sections = ReleaseNotesParser.Parse(heading + "\n\n- Something.\n");
        Assert.Equal(kind, Assert.Single(sections).Kind);
    }

    [Fact]
    public void TitleDetail_FromColonOrBold_ElseOneLine()
    {
        var sections = ReleaseNotesParser.Parse("""
            ### Added
            - Artist videos: an artist's video plays behind their page. (#270)
            - **Lossless check** Shows whether a file came from a lossy source.
            - **Spectrogram:** the cutoff is marked.
            - Fixed a crash. See the log: it names the file.
            - Plain line with no title.
            """);
        var items = Items(sections, ReleaseNoteKind.Added);

        Assert.Equal(("Artist videos", "an artist's video plays behind their page."), (items[0].Title, items[0].Detail));
        Assert.Equal(new[] { 270 }, items[0].Issues);
        Assert.Equal(("Lossless check", "Shows whether a file came from a lossy source."), (items[1].Title, items[1].Detail));
        Assert.Equal(("Spectrogram", "the cutoff is marked."), (items[2].Title, items[2].Detail));
        // A sentence before the colon is not a title.
        Assert.Equal("Fixed a crash. See the log: it names the file.", items[3].Title);
        Assert.Null(items[3].Detail);
        Assert.Equal("Plain line with no title.", items[4].Title);
        Assert.Null(items[4].Detail);
    }

    [Fact]
    public void Markdown_BecomesPlainText_LinksKeepTheirText()
    {
        var sections = ReleaseNotesParser.Parse("""
            ### Improved
            - Improved *the* `seek bar` and __Home__, see [the docs](https://example.com/docs) ![x](https://example.com/x.png). (#12, #13)
            - Kept snake_case_names and 2*3*4 as they are.
            """);
        var items = Items(sections, ReleaseNoteKind.Improved);
        Assert.Equal("Improved the seek bar and Home, see the docs.", items[0].Title);
        Assert.Equal(new[] { 12, 13 }, items[0].Issues);
        Assert.Equal("Kept snake_case_names and 2*3*4 as they are.", items[1].Title);
    }

    [Fact]
    public void GeneratedNotes_ContributorTailsGo_FirstContributionsDropped()
    {
        var sections = ReleaseNotesParser.Parse("""
            ## What's Changed
            * Add a lyrics plugin by @someone in https://github.com/heartached/Noctis/pull/168
            * Fix the tray icon by @other in #171

            ## New Contributors
            * @someone made their first contribution in https://github.com/heartached/Noctis/pull/168

            **Full Changelog**: https://github.com/heartached/Noctis/compare/v1.6.8...v1.6.9
            """);
        var other = Items(sections, ReleaseNoteKind.Other);
        Assert.Equal(2, other.Count);
        Assert.Equal("Add a lyrics plugin", other[0].Title);
        Assert.Equal(new[] { 168 }, other[0].Issues);
        Assert.Equal("Fix the tray icon", other[1].Title);
        Assert.Equal(new[] { 171 }, other[1].Issues);
    }

    [Fact]
    public void BulletsBeforeAnyHeading_AndContinuationLines_AreKept()
    {
        var sections = ReleaseNotesParser.Parse("- Loose note\n  that wraps onto a second line.\n\n### Fixed\n- Fixed it.\n");
        Assert.Equal("Loose note that wraps onto a second line.", Assert.Single(Items(sections, ReleaseNoteKind.Other)).Title);
        Assert.Equal("Fixed it.", Assert.Single(Items(sections, ReleaseNoteKind.Fixed)).Title);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n\n")]
    [InlineData("Just a paragraph, no bullets.\n\n**Full Changelog**: https://example.com")]
    public void EmptyOrBulletlessBody_HasNoSections(string? body)
    {
        Assert.Empty(ReleaseNotesParser.Parse(body));
    }
}
