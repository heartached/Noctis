using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One editable lyric line in the LRC editor.</summary>
public partial class LrcEditorLine : ObservableObject
{
    /// <param name="wordAnchor">The time the word tags in <paramref name="text"/> were written
    /// for, when not <paramref name="timestamp"/> (a later stamp of a stack).</param>
    public LrcEditorLine(TimeSpan? timestamp, string text, TimeSpan? wordAnchor = null)
    {
        _timestamp = timestamp;
        _wordAnchor = wordAnchor ?? timestamp;
        Text = text;
        var (body, _) = EnhancedLrcParser.StripVoiceMarker(text);
        DisplayText = Regex.Replace(EnhancedLrcParser.StripWordTags(body), @"\s{2,}", " ").Trim();
    }

    // The stamp the row's word tags were timed against: a re-stamp moves them by the difference.
    private readonly TimeSpan? _wordAnchor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampLabel))]
    [NotifyPropertyChangedFor(nameof(HasTimestamp))]
    private TimeSpan? _timestamp;

    public string Text { get; }

    /// <summary>The "[bg: …]" background-vocal lines written under this line. They have no row
    /// of their own; save writes them back after this line.</summary>
    public IReadOnlyList<string>? BackgroundLines { get; init; }

    /// <summary>The rows written at this line's stamp as its romanization / translation
    /// (GitHub #116, <see cref="LrcEditorViewModel.CompanionOwners"/>); null when none.</summary>
    internal List<LrcEditorLine>? Companions { get; set; }

    // Re-stamping the line takes its companions along while they still share its stamp: left
    // behind, they came back from the save as separate sung lines. One moved on its own stays put.
    partial void OnTimestampChanged(TimeSpan? oldValue, TimeSpan? newValue)
    {
        if (Companions is null) return;
        foreach (var companion in Companions)
            if (companion.Timestamp == oldValue) companion.Timestamp = newValue;
    }

    /// <summary><see cref="Text"/> as saved: its word tags moved as far as its stamp was.</summary>
    public string SavedText => Timestamp is null ? LrcEditorViewModel.UntimedText(Text) : LrcEditorViewModel.WordsAtStamp(Text, _wordAnchor, Timestamp);

    /// <summary><see cref="BackgroundLines"/> as saved: their word tags moved with the line.</summary>
    public IReadOnlyList<string>? SavedBackgroundLines =>
        BackgroundLines?.Select(b => Timestamp is null ? LrcEditorViewModel.UntimedBackground(b) : LrcEditorViewModel.WordsAtStamp(b, _wordAnchor, Timestamp)).ToList();

    /// <summary>
    /// Row display: duet voice markers ("v1:"/"v2:") and inline enhanced-LRC word
    /// tags stripped. Save round-trips the raw Text, so neither is lost by viewing.
    /// </summary>
    public string DisplayText { get; }

    public bool HasTimestamp => Timestamp.HasValue;

    public string TimestampLabel => Timestamp is { } t
        ? LrcEditorViewModel.FormatTimestamp(t)
        : "--:--.--";

    /// <summary>What a screen reader reads for the row: its time, then its words (the
    /// ListBoxItem read "Noctis.ViewModels.LrcEditorLine").</summary>
    public override string ToString() => HasTimestamp ? $"{TimestampLabel}, {DisplayText}" : DisplayText;
}

/// <summary>
/// LRC sync editor: stamp timestamps onto lines while the song plays
/// (tap-to-sync) and fine-tune individual lines with nudge buttons.
/// Saves to the .lrc sidecar first; embedded metadata is best effort.
/// </summary>
public partial class LrcEditorViewModel : ViewModelBase
{
    private static readonly TimeSpan NudgeStep = TimeSpan.FromMilliseconds(100);
    private static readonly Regex TimestampPattern = new(
        @"^\s*\[(\d{1,2}):(\d{2,3})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    private readonly Track _track;
    private readonly PlayerViewModel _player;
    private readonly IMetadataService _metadata;

    /// <summary>Settings ▸ Lyrics ▸ Save lyrics to (GitHub #135); the player's live value, replaceable in tests.</summary>
    internal Func<LyricsSaveTarget> SaveTarget { get; set; }

    /// <summary>Writes the tags once the song stops playing (the editor runs while it plays); null = right away.</summary>
    internal IDeferredTagWriter? TagWriter { get; set; }

    public ObservableCollection<LrcEditorLine> Lines { get; } = new();

    [ObservableProperty] private int _selectedIndex;
    [ObservableProperty] private string _statusText = string.Empty;

    public PlayerViewModel Player => _player;
    public string TrackTitle => _track.Title;
    public string TrackArtist => _track.ArtistDisplay;

    /// <summary>Raised after a successful save so the lyrics page can reload.</summary>
    public event EventHandler? Saved;

    public LrcEditorViewModel(Track track, PlayerViewModel player, IMetadataService metadata,
        string? syncedLyrics, string? plainLyrics)
    {
        _track = track;
        _player = player;
        _metadata = metadata;
        SaveTarget = () => LyricsSaveOptions.ParseTarget(player.LyricsSaveTarget);

        // Prefer existing synced lyrics (retiming); otherwise start from plain
        // text lines with no timestamps (the tap-to-sync starting point).
        var source = !string.IsNullOrWhiteSpace(syncedLyrics) ? syncedLyrics : plainLyrics;
        var (rows, background) = SplitBackgroundLines(source ?? string.Empty);
        for (var i = 0; i < rows.Count; i++)
            Lines.Add(new LrcEditorLine(rows[i].Time, rows[i].Text, rows[i].WordAnchor) { BackgroundLines = background.GetValueOrDefault(i) });
        var owners = CompanionOwners(rows.Select(r => (r.Time, r.Text)).ToList());
        for (var i = 0; i < owners.Length; i++)
            if (owners[i] >= 0) (Lines[owners[i]].Companions ??= new List<LrcEditorLine>()).Add(Lines[i]);

        SelectedIndex = Lines.Count > 0 ? 0 : -1;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure helpers (unit-tested)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses LRC or plain text into (timestamp?, text) lines. Lines without a
    /// leading [mm:ss.xx] tag come back with a null timestamp; blank lines are
    /// dropped. A line with a stack of tags becomes one entry per tag.
    /// </summary>
    public static List<(TimeSpan? Time, string Text)> ParseLrc(string text) =>
        ParseRows(text).Select(r => (r.Time, r.Text)).ToList();

    /// <summary>
    /// <see cref="ParseLrc"/> with each row's word anchor: the time its inline word tags were
    /// written for. That is the row's own stamp, except in a stack ("[00:05.00][00:35.00]…"),
    /// whose word tags are timed for its first stamp — the 0:35 row written out on its own
    /// line kept 0:05 word times, and swept before it came up.
    /// </summary>
    private static List<(TimeSpan? Time, string Text, TimeSpan? WordAnchor)> ParseRows(string text)
    {
        var result = new List<(TimeSpan?, string, TimeSpan?)>();

        // Drop the empty elements Split produces for a trailing newline before iterating,
        // so a file ending in "\n" doesn't gain phantom blank rows.
        var rawLines = text.Split('\n');
        var lastContent = rawLines.Length - 1;
        while (lastContent >= 0 && string.IsNullOrWhiteSpace(rawLines[lastContent].TrimEnd('\r')))
            lastContent--;

        for (var idx = 0; idx <= lastContent; idx++)
        {
            var line = rawLines[idx].TrimEnd('\r');

            // INTERIOR blank lines are preserved as untimestamped empty entries rather
            // than dropped. MetadataViewModel.RebuildSyncedLinesFromText feeds plain
            // lyrics through here and RebuildSyncedTextFromLines writes the list straight
            // back, so skipping them meant opening the Timestamp Lyrics tab and touching
            // any row collapsed every verse/chorus break in the saved text.
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Add((null, string.Empty, null));
                continue;
            }

            var match = TimestampPattern.Match(line);
            if (!match.Success)
            {
                // Skip LRC metadata tags like [ar:], [ti:], [offset:]
                if (Regex.IsMatch(line, @"^\s*\[[a-zA-Z]+:.*\]\s*$")) continue;
                result.Add((null, line.Trim(), null));
                continue;
            }

            // A stack of leading tags ("[00:05.00][00:35.00]Chorus") is the same words sung at
            // each time: one row per tag. Keeping only the first lost the repeats the moment
            // Edit Info rewrote the text from these rows (touching any row on the Timestamp
            // Lyrics tab turned the chorus at 0:35 into nothing).
            var times = new List<TimeSpan>();
            var content = line;
            while (TimestampPattern.Match(content) is { Success: true } m)
            {
                times.Add(ToTime(m));
                content = content[m.Length..];
            }
            foreach (var time in times)
                result.Add((time, content.Trim(), times[0]));
        }
        return result;
    }

    /// <summary>
    /// The editor rows of <paramref name="text"/>, and its "[bg: …]" background-vocal lines keyed
    /// by the row they follow. <see cref="ParseLrc"/> takes a bg line for a header tag ([ar:],
    /// [ti:]) and leaves it out, so the text rebuilt from the rows after any edit lost every
    /// background vocal (a TTML import's x-bg spans among them). Both editors — this one and
    /// Edit Info's Timestamp Lyrics — keep them on the row and write them back under it.
    /// </summary>
    public static (List<(TimeSpan? Time, string Text, TimeSpan? WordAnchor)> Rows, Dictionary<int, List<string>> Background) SplitBackgroundLines(string text)
    {
        var background = new Dictionary<int, List<string>>();
        var kept = new List<string>();
        // Rows ParseLrc makes of the kept lines so far: blank lines count once text follows them
        // (it drops trailing ones), a header tag none, a stack of stamps one per stamp.
        int rows = 0, blanks = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (rows > 0 && line.TrimStart().StartsWith("[bg:", StringComparison.Ordinal))
            {
                if (!background.TryGetValue(rows - 1, out var list)) background[rows - 1] = list = new List<string>();
                list.Add(line.Trim());
                continue;
            }
            kept.Add(line);
            if (string.IsNullOrWhiteSpace(line)) blanks++;
            else { rows += blanks + ParseLrc(line).Count; blanks = 0; }
        }
        return (ParseRows(string.Join('\n', kept)), background);
    }

    /// <summary>
    /// For each row, the index of the row it is a companion of, or -1: a line-level row at the
    /// same stamp as a sung row just above it, in that row's voice — its romanization or
    /// translation, which <see cref="LrcParser"/> folds into that line (GitHub #116). Same rules
    /// as the parser: a word-timed row or one in another duet voice at that instant is sung and
    /// starts its own entry, a word-timed "(…)" adlib takes no companions, and a row without
    /// text (an end marker) is neither.
    /// </summary>
    public static int[] CompanionOwners(IReadOnlyList<(TimeSpan? Time, string Text)> rows)
    {
        var owners = new int[rows.Count];
        Array.Fill(owners, -1);
        var i = 0;
        while (i < rows.Count)
        {
            var end = i + 1;
            if (rows[i].Time is { } stamp)
                while (end < rows.Count && rows[end].Time == stamp) end++;
            var pending = Enumerable.Range(i, end - i).Where(k => !string.IsNullOrWhiteSpace(rows[k].Text)).ToList();
            i = end;
            while (pending.Count > 1)
            {
                var main = pending[0];
                pending.RemoveAt(0);
                var (mainWords, mainVoice, mainText) = Shape(rows[main].Text);
                if (mainWords && Services.LyricsStudio.TimedLyricsBuilder.IsFullyParenthesized(mainText)) continue;
                pending.RemoveAll(k =>
                {
                    var (words, voice, _) = Shape(rows[k].Text);
                    if (words || voice != mainVoice) return false;
                    owners[k] = main;
                    return true;
                });
            }
        }
        return owners;

        static (bool Words, LyricVoice Voice, string Text) Shape(string text)
        {
            var (body, voice) = EnhancedLrcParser.StripVoiceMarker(text);
            var plain = EnhancedLrcParser.StripWordTags(body);
            return (plain != body, voice, plain.Trim());
        }
    }

    /// <summary>
    /// A word-timed line's text once its stamp moved from <paramref name="anchor"/> to
    /// <paramref name="stamp"/>: every "&lt;mm:ss.xx&gt;" word tag moved by the same amount
    /// (clamped at zero). Re-stamping moved only the "[mm:ss.xx]", so after save the words
    /// swept before the line came up, or after a gap. Unchanged when either time is missing.
    /// </summary>
    public static string WordsAtStamp(string text, TimeSpan? anchor, TimeSpan? stamp) =>
        anchor is { } from && stamp is { } to ? LyricsTextHelper.ShiftWordTimestamps(text, to - from) : text;

    /// <summary>
    /// A row written without a stamp: word tags (and the voice marker that rides them) mean
    /// nothing there — the reader showed the raw "&lt;00:01.00&gt;" as lyric text. Their times
    /// stay on the row, so stamping it again in the same session still moves them.
    /// Rows without word tags (plain lyrics waiting to be timed) are written as they are.
    /// </summary>
    public static string UntimedText(string text)
    {
        var (body, _) = EnhancedLrcParser.StripVoiceMarker(text);
        var plain = EnhancedLrcParser.StripWordTags(body);
        return plain == body ? text : Regex.Replace(plain, @"\s{2,}", " ").Trim();
    }

    /// <summary>
    /// The "[bg: …]" line of a row written without a stamp, as a plain line of its own: the
    /// reader hangs a "[bg: …]" line on the last stamped line above it, so it became the
    /// background vocal of another line.
    /// </summary>
    public static string UntimedBackground(string line)
    {
        var body = line.Trim();
        if (body.StartsWith("[bg:", StringComparison.Ordinal)) body = body[4..];
        if (body.EndsWith(']')) body = body[..^1];
        return Regex.Replace(EnhancedLrcParser.StripWordTags(body), @"\s{2,}", " ").Trim();
    }

    private static TimeSpan ToTime(Match match)
    {
        var minutes = int.Parse(match.Groups[1].Value);
        var seconds = int.Parse(match.Groups[2].Value);
        var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";
        var ms = fraction.Length switch
        {
            1 => int.Parse(fraction) * 100,
            2 => int.Parse(fraction) * 10,
            _ => int.Parse(fraction),
        };
        return new TimeSpan(0, 0, minutes, seconds, ms);
    }

    /// <summary>Builds LRC text from lines that carry timestamps, in time order.</summary>
    public static string BuildLrc(IEnumerable<(TimeSpan Time, string Text)> lines)
    {
        var sb = new StringBuilder();
        foreach (var (time, text) in lines.OrderBy(l => l.Time))
            sb.Append('[').Append(FormatTimestamp(time)).Append(']').AppendLine(text);
        return sb.ToString();
    }

    /// <summary>
    /// Serializes the editor's lines, keeping the ones the user hasn't stamped yet.
    /// Save previously emitted only stamped lines, so leaving mid-session replaced a
    /// complete sidecar with however many lines had been timed — the rest were gone from
    /// the file entirely. Stamped lines come first in time order, then the untimed
    /// remainder in their original order so a later pass can finish the job.
    /// </summary>
    public static string BuildLrcPreservingUntimed(IEnumerable<(TimeSpan? Time, string Text)> lines)
    {
        var all = lines.ToList();
        var sb = new StringBuilder();

        foreach (var (time, text) in all.Where(l => l.Time.HasValue).OrderBy(l => l.Time!.Value))
            sb.Append('[').Append(FormatTimestamp(time!.Value)).Append(']').AppendLine(text);

        foreach (var (_, text) in all.Where(l => !l.Time.HasValue))
            sb.AppendLine(text);

        return sb.ToString();
    }

    public static string FormatTimestamp(TimeSpan t) =>
        $"{(int)t.TotalMinutes:00}:{t.Seconds:00}.{t.Milliseconds / 10:00}";

    // ─────────────────────────────────────────────────────────────────────
    // Commands
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tap-to-sync: stamps the current playback position onto the selected line
    /// and advances to the next one.
    /// </summary>
    [RelayCommand]
    private void StampCurrent()
    {
        if (SelectedIndex < 0 || SelectedIndex >= Lines.Count) return;
        if (StampingBlockedByMusicVideoAudio()) return;
        var line = Lines[SelectedIndex];
        line.Timestamp = _player.Position;
        var next = NextRowToStamp(line, SelectedIndex);
        if (next < Lines.Count)
            SelectedIndex = next;
    }

    [RelayCommand]
    private void StampLine(LrcEditorLine? line)
    {
        if (line == null) return;
        if (StampingBlockedByMusicVideoAudio()) return;
        line.Timestamp = _player.Position;
        var index = Lines.IndexOf(line);
        if (index < 0) return;
        var next = NextRowToStamp(line, index);
        if (next < Lines.Count)
            SelectedIndex = next;
    }

    /// <summary>The row after <paramref name="line"/>, past the companion rows that took its stamp
    /// with it — the next tap stamped the romaji row apart from its line.</summary>
    private int NextRowToStamp(LrcEditorLine line, int index)
    {
        var next = index + 1;
        while (next < Lines.Count && line.Companions?.Contains(Lines[next]) == true && Lines[next].Timestamp == line.Timestamp)
            next++;
        return next;
    }

    /// <summary>Lines are timed against the song file; a music video's audio runs on the
    /// clip's own clock, so stamping waits until the song file plays again.</summary>
    private bool StampingBlockedByMusicVideoAudio()
    {
        if (!_player.IsPlayingMusicVideoAudio) return false;
        StatusText = Noctis.Localization.Loc.T("LrcEditor.StampingOffVideoAudio");
        return true;
    }

    [RelayCommand]
    private void NudgeBack(LrcEditorLine? line)
    {
        if (line?.Timestamp is not { } t) return;
        var nudged = t - NudgeStep;
        line.Timestamp = nudged < TimeSpan.Zero ? TimeSpan.Zero : nudged;
    }

    [RelayCommand]
    private void NudgeForward(LrcEditorLine? line)
    {
        if (line?.Timestamp is not { } t) return;
        line.Timestamp = t + NudgeStep;
    }

    [RelayCommand]
    private void ClearTimestamp(LrcEditorLine? line)
    {
        if (line == null) return;
        line.Timestamp = null;
    }

    /// <summary>Seeks playback slightly before the line so the nudge can be verified by ear.</summary>
    [RelayCommand]
    private void PlayFromLine(LrcEditorLine? line)
    {
        if (line?.Timestamp is not { } t || _player.Duration.TotalSeconds <= 0) return;
        var target = t - TimeSpan.FromSeconds(1);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        _player.SeekToPositionCommand.Execute(target.TotalSeconds / _player.Duration.TotalSeconds);
        if (_player.State != PlaybackState.Playing)
            _player.PlayPauseCommand.Execute(null);
    }

    [RelayCommand]
    private async Task Save()
    {
        // A line's "[bg: …]" vocals go out right under it, wherever the time sort puts it.
        var content = Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (l.Timestamp, l.SavedBackgroundLines is { Count: > 0 } bg
                ? l.SavedText + Environment.NewLine + string.Join(Environment.NewLine, bg)
                : l.SavedText))
            .ToList();

        if (!content.Any(l => l.Timestamp.HasValue))
        {
            StatusText = Noctis.Localization.Loc.T("LrcEditor.NothingToSave");
            return;
        }

        var lrc = BuildLrcPreservingUntimed(content);
        _track.SyncedLyrics = lrc;

        try
        {
            // WriteTrackMetadata copies and rewrites the whole audio file, so the
            // file work runs on a worker thread — inline it froze the UI for the
            // duration of the copy on large files.
            // Settings ▸ Lyrics ▸ Save lyrics to (GitHub #135).
            LyricsSaveTarget target;
            try { target = SaveTarget(); } catch { target = LyricsSaveTarget.Sidecar; }
            var tagsFailed = false;
            await Task.Run(() =>
            {
                // Sidecar first (root-cause fix: tag writes can fail on in-use files).
                // Inside the file only: no new .lrc, but one already there is kept current —
                // the lyrics page reads it before the tags.
                if (!string.IsNullOrWhiteSpace(_track.FilePath)
                    && (target.WritesSidecars() || File.Exists(Path.ChangeExtension(_track.FilePath, ".lrc"))))
                {
                    var lrcPath = Path.ChangeExtension(_track.FilePath, ".lrc");

                    // Keep one generation of whatever was there. This overwrites the user's
                    // own hand-timed (possibly word-level) sidecar with a line-level file
                    // built from what the editor happened to be seeded with, and there was
                    // no backup and no confirmation.
                    try
                    {
                        if (File.Exists(lrcPath))
                            File.Copy(lrcPath, lrcPath + ".bak", overwrite: true);
                    }
                    catch { /* a missing backup must not block the save */ }

                    // Write via a temp file so a failure part-way through can't leave a
                    // truncated sidecar where a complete one used to be.
                    var tempPath = lrcPath + ".tmp";
                    File.WriteAllText(tempPath, lrc, new UTF8Encoding(false));
                    File.Move(tempPath, lrcPath, overwrite: true);
                }

                // Best-effort metadata write.
                try { _metadata.WriteTrackMetadata(_track); } catch { }

                // The timed lyrics inside the audio file (synced text in its lyrics tag, SYLT for ID3v2).
                if (target.WritesTags() && !string.IsNullOrWhiteSpace(_track.FilePath))
                {
                    var (path, plain) = (_track.FilePath, _track.Lyrics);
                    if (TagWriter is { } deferred)
                        deferred.Enqueue(path, "lyrics", () => _metadata.WriteLyrics(path, plain, lrc));
                    else
                    {
                        try { tagsFailed = !_metadata.WriteLyrics(path, plain, lrc); }
                        catch { tagsFailed = true; }
                    }
                }
            });

            if (tagsFailed)
            {
                StatusText = Noctis.Localization.Loc.T("LrcEditor.SavedEmbedFailed");
                Saved?.Invoke(this, EventArgs.Empty);
                return;
            }
            Noctis.Helpers.TransientStatus.Show(nameof(StatusText), v => StatusText = v, "Saved");
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = Noctis.Localization.Loc.T("LrcEditor.SaveFailed", ex.Message);
        }
    }
}
