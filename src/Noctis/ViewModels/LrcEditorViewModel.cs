using System.Collections.ObjectModel;
using System.Text;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>One editable lyric line in the LRC editor.</summary>
public partial class LrcEditorLine : ObservableObject
{
    public LrcEditorLine(TimeSpan? timestamp, string text)
    {
        _timestamp = timestamp;
        Text = text;
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TimestampLabel))]
    [NotifyPropertyChangedFor(nameof(HasTimestamp))]
    private TimeSpan? _timestamp;

    public string Text { get; }

    public bool HasTimestamp => Timestamp.HasValue;

    public string TimestampLabel => Timestamp is { } t
        ? LrcEditorViewModel.FormatTimestamp(t)
        : "--:--.--";
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
        @"^\s*\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]", RegexOptions.Compiled);

    private readonly Track _track;
    private readonly PlayerViewModel _player;
    private readonly IMetadataService _metadata;

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

        // Prefer existing synced lyrics (retiming); otherwise start from plain
        // text lines with no timestamps (the tap-to-sync starting point).
        var source = !string.IsNullOrWhiteSpace(syncedLyrics) ? syncedLyrics : plainLyrics;
        foreach (var (time, text) in ParseLrc(source ?? string.Empty))
            Lines.Add(new LrcEditorLine(time, text));

        SelectedIndex = Lines.Count > 0 ? 0 : -1;
    }

    // ─────────────────────────────────────────────────────────────────────
    // Pure helpers (unit-tested)
    // ─────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Parses LRC or plain text into (timestamp?, text) lines. Lines without a
    /// leading [mm:ss.xx] tag come back with a null timestamp; blank lines are
    /// dropped. Only the first tag per line is honored.
    /// </summary>
    public static List<(TimeSpan? Time, string Text)> ParseLrc(string text)
    {
        var result = new List<(TimeSpan?, string)>();

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
                result.Add((null, string.Empty));
                continue;
            }

            var match = TimestampPattern.Match(line);
            if (!match.Success)
            {
                // Skip LRC metadata tags like [ar:], [ti:], [offset:]
                if (Regex.IsMatch(line, @"^\s*\[[a-zA-Z]+:.*\]\s*$")) continue;
                result.Add((null, line.Trim()));
                continue;
            }

            var minutes = int.Parse(match.Groups[1].Value);
            var seconds = int.Parse(match.Groups[2].Value);
            var fraction = match.Groups[3].Success ? match.Groups[3].Value : "0";
            var ms = fraction.Length switch
            {
                1 => int.Parse(fraction) * 100,
                2 => int.Parse(fraction) * 10,
                _ => int.Parse(fraction),
            };

            var time = new TimeSpan(0, 0, minutes, seconds, ms);
            // Strip every leading timestamp tag (some files stack repeats).
            var content = line;
            while (TimestampPattern.Match(content) is { Success: true } m)
                content = content[m.Length..];
            result.Add((time, content.Trim()));
        }
        return result;
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
        Lines[SelectedIndex].Timestamp = _player.Position;
        if (SelectedIndex < Lines.Count - 1)
            SelectedIndex++;
    }

    [RelayCommand]
    private void StampLine(LrcEditorLine? line)
    {
        if (line == null) return;
        line.Timestamp = _player.Position;
        var index = Lines.IndexOf(line);
        if (index >= 0 && index < Lines.Count - 1)
            SelectedIndex = index + 1;
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
    private void Save()
    {
        var content = Lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Text))
            .Select(l => (l.Timestamp, l.Text))
            .ToList();

        if (!content.Any(l => l.Timestamp.HasValue))
        {
            StatusText = "Nothing to save — stamp at least one line";
            return;
        }

        var lrc = BuildLrcPreservingUntimed(content);
        _track.SyncedLyrics = lrc;

        try
        {
            // Sidecar first (root-cause fix: tag writes can fail on in-use files).
            if (!string.IsNullOrWhiteSpace(_track.FilePath))
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

            StatusText = "Saved";
            Saved?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            StatusText = $"Save failed: {ex.Message}";
        }
    }
}
