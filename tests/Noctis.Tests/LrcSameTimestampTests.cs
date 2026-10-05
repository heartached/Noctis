using Noctis.Models;
using Noctis.Services;
using Xunit;

namespace Noctis.Tests;

/// <summary>
/// GitHub #116: LRC lines sharing one timestamp (original + romaji + translation) are one
/// lyric entry. They used to parse as separate lines at the same instant, so the timeline
/// cursor landed on the last one and only that line showed as current.
/// </summary>
public class LrcSameTimestampTests
{
    private const string IssueLrc =
        "[00:05.54]泣きじゃくる子供のように\n" +
        "[00:05.54]nakijakuru kodomo no you ni\n" +
        "[00:05.54]Como un niño que llora sin consuelo\n" +
        "[00:09.00]next";

    [Fact]
    public void IssueExample_ThreeLines_BecomeOneEntry_MainRomajiTranslation()
    {
        var lines = LrcParser.Parse(IssueLrc);

        Assert.Equal(2, lines.Count);
        var first = lines[0];
        Assert.Equal(TimeSpan.FromMilliseconds(5540), first.Timestamp);
        Assert.Equal("泣きじゃくる子供のように", first.Text);
        Assert.Equal("nakijakuru kodomo no you ni", first.Transliteration);
        Assert.Equal("Como un niño que llora sin consuelo", first.Translation);
        Assert.True(first.ShowTransliterationText);
        Assert.True(first.ShowTranslation);
        Assert.Equal("next", lines[1].Text);
    }

    [Fact]
    public void IssueExample_TimelineActivatesTheMergedEntry()
    {
        var lines = LrcParser.Parse(IssueLrc);
        var timeline = new LyricsTimeline(lines, TimeSpan.Zero, TimeSpan.Zero);

        var step = timeline.Update(TimeSpan.FromSeconds(6));

        Assert.Same(lines[0], step.ActiveLine);
        Assert.Equal("Como un niño que llora sin consuelo", step.ActiveLine!.Translation);
    }

    [Fact]
    public void DesktopForwarder_MergesToo()
    {
        var lines = Noctis.ViewModels.LyricsViewModel.ParseLrcContent(IssueLrc);
        Assert.Equal("nakijakuru kodomo no you ni", lines[0].Transliteration);
        Assert.Equal("Como un niño que llora sin consuelo", lines[0].Translation);
    }

    [Fact]
    public void TwoLines_SecondIsTheTranslation()
    {
        var lines = LrcParser.Parse("[00:01.00]こんにちは\n[00:01.00]Hello");

        var line = Assert.Single(lines);
        Assert.Equal("こんにちは", line.Text);
        Assert.Equal("Hello", line.Translation);
        Assert.Null(line.Transliteration);
    }

    [Fact]
    public void FourOrMoreLines_ExtraLinesJoinTheTranslation_NothingDropped()
    {
        var lines = LrcParser.Parse(
            "[00:01.00]main\n[00:01.00]roman\n[00:01.00]english\n[00:01.00]español");

        var line = Assert.Single(lines);
        Assert.Equal("main", line.Text);
        Assert.Equal("roman", line.Transliteration);
        Assert.Equal("english\nespañol", line.Translation);
    }

    [Fact]
    public void GroupNotConsecutiveInFile_KeepsFileOrderWithinTheTimestamp()
    {
        var lines = LrcParser.Parse(
            "[00:01.00]one\n[00:02.00]two\n[00:01.00]uno-roman\n[00:02.00]dos\n[00:01.00]uno");

        Assert.Equal(2, lines.Count);
        Assert.Equal("one", lines[0].Text);
        Assert.Equal("uno-roman", lines[0].Transliteration);
        Assert.Equal("uno", lines[0].Translation);
        Assert.Equal("two", lines[1].Text);
        Assert.Equal("dos", lines[1].Translation);
    }

    [Fact]
    public void MultiTimestampLines_StillRepeat_AndPairPerStamp()
    {
        var lines = LrcParser.Parse(
            "[00:01.00][00:20.00]chorus\n[00:01.00][00:20.00]coro\n[00:10.00]verse");

        Assert.Equal(3, lines.Count);
        Assert.Equal(("chorus", "coro"), (lines[0].Text, lines[0].Translation));
        Assert.Equal("verse", lines[1].Text);
        Assert.Equal(("chorus", "coro"), (lines[2].Text, lines[2].Translation));
        Assert.Equal(TimeSpan.FromSeconds(20), lines[2].Timestamp);
    }

    [Fact]
    public void BlankMarkerAtTheSameTimestamp_IsIgnored()
    {
        var lines = LrcParser.Parse("[00:01.00]main\n[00:01.00]\n[00:01.00]trans\n[00:03.00]");

        var line = Assert.Single(lines);
        Assert.Equal("main", line.Text);
        Assert.Equal("trans", line.Translation);
    }

    [Fact]
    public void Offset_AppliesToTheGroup()
    {
        var lines = LrcParser.Parse("[offset:+500]\n[00:01.00]main\n[00:01.00]trans");

        var line = Assert.Single(lines);
        Assert.Equal(TimeSpan.FromSeconds(1.5), line.Timestamp);
        Assert.Equal("trans", line.Translation);
    }

    [Fact]
    public void ExactDuplicateLine_IsNotRepeatedAsATranslation()
    {
        var lines = LrcParser.Parse("[00:01.00]same\n[00:01.00]same\n[00:01.00]other");

        var line = Assert.Single(lines);
        Assert.Equal("same", line.Text);
        Assert.Equal("other", line.Translation);
    }

    [Fact]
    public void ElrcGroup_MainKeepsWordTiming_PlainLinesBecomeItsLayers()
    {
        var lines = LrcParser.Parse(
            "[00:01.00]<00:01.00>泣き <00:01.50>じゃくる<00:02.00>\n" +
            "[00:01.00]naki jakuru\n" +
            "[00:01.00]Llorando");

        var line = Assert.Single(lines);
        Assert.Equal(new[] { "泣き", "じゃくる" }, line.Words!.Select(w => w.Text.Trim()));
        Assert.Equal("naki jakuru", line.Transliteration);
        Assert.Equal("Llorando", line.Translation);
    }

    [Fact]
    public void WordTimedLinesAtTheSameInstant_AreSungLines_NotLayers()
    {
        // Overlapping vocals (or Lyrics Studio lines clamped to 0:00) each carry their own
        // word timing; folding one into the other would lose it.
        var lines = LrcParser.Parse(
            "[00:01.00]<00:01.00>first <00:01.50>voice<00:02.00>\n" +
            "[00:01.00]<00:01.10>second <00:01.70>voice<00:02.20>");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.True(l.HasWords));
        Assert.All(lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void DuetLinesAtTheSameInstant_StaySeparate()
    {
        var lines = LrcParser.Parse("[00:01.00]v1:I sing\n[00:01.00]v2:you sing\n[00:01.00]v2:tú cantas");

        Assert.Equal(2, lines.Count);
        Assert.Equal(("I sing", LyricVoice.Default), (lines[0].Text, lines[0].Voice));
        Assert.Null(lines[0].Translation);
        Assert.Equal(("you sing", LyricVoice.Voice2), (lines[1].Text, lines[1].Voice));
        Assert.Equal("tú cantas", lines[1].Translation);
    }

    [Fact]
    public void WordTimedAdlibAtTheSameInstant_StillFoldsIntoBackground()
    {
        // A fully parenthesized word-timed line at its main line's instant is an adlib
        // (FoldBackgroundLines), not a translation.
        var lines = LrcParser.Parse(
            "[00:01.00]<00:01.00>hello <00:01.50>there<00:02.00>\n" +
            "[00:01.00]<00:01.20>(oh <00:01.60>yeah)<00:02.00>");

        var line = Assert.Single(lines);
        Assert.True(line.HasBackgroundWords);
        Assert.Null(line.Translation);
    }

    [Fact]
    public void BgLineAfterACompanion_MovesToTheMainLine()
    {
        var lines = LrcParser.Parse(
            "[00:01.00]main\n[00:01.00]trans\n[bg: <00:01.20>(ah<00:01.80>)]");

        var line = Assert.Single(lines);
        Assert.Equal("trans", line.Translation);
        Assert.True(line.HasBackgroundWords);
    }

    [Fact]
    public void DistinctTimestamps_AreUntouched()
    {
        var lines = LrcParser.Parse("[00:01.00]a\n[00:01.01]b");

        Assert.Equal(2, lines.Count);
        Assert.All(lines, l => Assert.Null(l.Translation));
    }

    [Fact]
    public void PhoneLoader_LrcSidecar_GetsTheMergedEntry()
    {
        var files = new FakeTrackFiles();
        files.Sidecars[".lrc"] = IssueLrc;
        var track = new Track { Id = Guid.NewGuid(), FilePath = "content://x/tree/t/document/primary%3AMusic%2Fsong.mp3" };

        var result = LyricsLoader.Load(track, files, joinSplitWords: false);

        var entry = result.Lines.First(l => !l.IsIntroPlaceholder);
        Assert.Equal("泣きじゃくる子供のように", entry.Text);
        Assert.Equal("nakijakuru kodomo no you ni", entry.Transliteration);
        Assert.Equal("Como un niño que llora sin consuelo", entry.Translation);
    }
}
