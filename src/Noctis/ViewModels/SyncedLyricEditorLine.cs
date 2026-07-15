using System;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Noctis.ViewModels;

/// <summary>
/// One editable line in the Metadata → Synced Lyrics manual editor: a lyric with
/// an optional [mm:ss.xx] timestamp the user types or nudges by hand.
///
/// Unlike the lyrics-page LRC editor there is no live "stamp from playback" — the
/// Metadata dialog has no player and may be editing a track that isn't the one
/// playing — so timestamps are set manually (typed) and fine-tuned with nudges.
/// </summary>
public partial class SyncedLyricEditorLine : ObservableObject
{
    /// <summary>Raised on any user edit so the owner can regenerate the LRC text.</summary>
    public event Action? Changed;

    public SyncedLyricEditorLine(TimeSpan? timestamp, string text)
    {
        _timestamp = timestamp;
        Text = text;
        DisplayText = StripWordTags(text);
        HasWordTiming = DisplayText != text;
    }

    public string Text { get; }

    /// <summary>
    /// Lyric text with inline enhanced-LRC word tags (&lt;mm:ss.xx&gt;) stripped, so
    /// display rows show the words instead of the markup. Save round-trips the raw
    /// Text, so word timing is never lost by viewing.
    /// </summary>
    public string DisplayText { get; }

    /// <summary>True when the raw text carries inline word-timing tags.</summary>
    public bool HasWordTiming { get; }

    private static readonly Regex WordTagRegex = new(@"<\d{1,2}:\d{2}(?:\.\d{1,3})?>", RegexOptions.Compiled);

    private static string StripWordTags(string text)
    {
        if (!text.Contains('<')) return text;
        var stripped = WordTagRegex.Replace(text, string.Empty);
        if (ReferenceEquals(stripped, text) || stripped == text) return text;
        return Regex.Replace(stripped, @"\s{2,}", " ").Trim();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTimestamp))]
    [NotifyPropertyChangedFor(nameof(TimestampText))]
    private TimeSpan? _timestamp;

    public bool HasTimestamp => Timestamp.HasValue;

    /// <summary>
    /// Two-way bound to the row's timestamp box. Accepts m:ss, m:ss.x, m:ss.xx;
    /// blank or unparseable input clears the timestamp.
    /// </summary>
    public string TimestampText
    {
        get => Timestamp is { } t ? LrcEditorViewModel.FormatTimestamp(t) : string.Empty;
        set
        {
            var parsed = ParseTimestamp(value);
            if (parsed == Timestamp)
            {
                // Snap the box back to the stored value when the entry was a no-op
                // (e.g. invalid text, or re-typing the same time).
                OnPropertyChanged();
                return;
            }
            Timestamp = parsed; // raises HasTimestamp + TimestampText + Changed
        }
    }

    partial void OnTimestampChanged(TimeSpan? value) => Changed?.Invoke();

    private static readonly TimeSpan NudgeStep = TimeSpan.FromMilliseconds(100);

    /// <summary>Shifts an existing timestamp by ±0.1s; no-op on un-timestamped lines.</summary>
    public void Nudge(bool forward)
    {
        if (Timestamp is not { } t) return;
        var next = forward ? t + NudgeStep : t - NudgeStep;
        Timestamp = next < TimeSpan.Zero ? TimeSpan.Zero : next;
    }

    public void ClearTimestamp() => Timestamp = null;

    private static TimeSpan? ParseTimestamp(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        var s = raw.Trim().Trim('[', ']');
        var colon = s.IndexOf(':');
        if (colon <= 0) return null;
        if (!int.TryParse(s[..colon], out var minutes) || minutes < 0) return null;

        var rest = s[(colon + 1)..];
        var dot = rest.IndexOfAny(new[] { '.', ':' });
        int seconds, ms = 0;
        if (dot < 0)
        {
            if (!int.TryParse(rest, out seconds)) return null;
        }
        else
        {
            if (!int.TryParse(rest[..dot], out seconds)) return null;
            var frac = rest[(dot + 1)..];
            if (frac.Length > 0)
            {
                if (!int.TryParse(frac, out var f) || f < 0) return null;
                ms = frac.Length switch { 1 => f * 100, 2 => f * 10, _ => f };
            }
        }

        if (seconds is < 0 or > 59) return null;
        return new TimeSpan(0, 0, minutes, seconds, ms);
    }
}
