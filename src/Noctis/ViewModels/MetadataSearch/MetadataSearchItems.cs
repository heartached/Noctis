using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Noctis.Models;
using Noctis.Services.MetadataSearch;

namespace Noctis.ViewModels.MetadataSearch;

// Row/item types of the Search metadata panel (owner 10-08: Search metadata revamp — "I want
// the user to be able to see the changes before and after"). Plain view state: the panel VM
// builds them, the editor (MetadataViewModel.Search.cs) decides what a field maps onto.

/// <summary>Every field a search result can fill. The editor maps each onto one of its edit
/// properties, or reports why it can't store it in this scope.</summary>
public enum MetadataSearchField
{
    Title, Artist, Album, AlbumArtist, Year, ReleaseDate, Genre,
    TrackNumber, TrackCount, DiscNumber, DiscCount, Composer,
    Label, Copyright, Isrc, Barcode, Explicit, Bpm,
}

/// <summary>What the editor holds for one search field. <c>Raw</c> is compared with the
/// candidate's value; <c>Display</c> is shown ("—" when empty, "Mixed" across an album).
/// <c>Applicable</c> false: the field has no row in this editor (album titles live in the
/// track table). A <c>BlockedReason</c>: shown, but this editor can't store it.</summary>
public readonly record struct SearchFieldState(string Raw, string Display, bool IsEmpty, string? BlockedReason, bool Applicable)
{
    public static SearchFieldState NotApplicable => new(string.Empty, string.Empty, true, null, false);
    public static SearchFieldState Blocked(string reason) => new(string.Empty, "—", true, reason, true);
}

/// <summary>A provider filter chip ("Deezer", "MusicBrainz", …).</summary>
public sealed partial class ProviderChip : ObservableObject
{
    public ProviderChip(string name) => Name = name;
    public string Name { get; }
    [ObservableProperty] private bool _isSelected = true;
}

/// <summary>One provider's outcome under the search bar: "Deezer 5", "Apple Music offline".</summary>
public sealed record ProviderStatusItem(string Text, bool IsOk, bool IsProblem);

/// <summary>A result card in the left list.</summary>
public sealed partial class CandidateItem : ObservableObject, IDisposable
{
    public CandidateItem(MetadataCandidate candidate, bool albumScope)
    {
        Candidate = candidate;
        var title = albumScope && !string.IsNullOrWhiteSpace(candidate.Album) ? candidate.Album : candidate.Title;
        Title = string.IsNullOrWhiteSpace(title) ? candidate.Album : title;
        var artist = albumScope && !string.IsNullOrWhiteSpace(candidate.AlbumArtist) ? candidate.AlbumArtist : candidate.Artist;
        // Album scope: the title already is the album, so the second line carries the track count.
        var second = albumScope
            ? (candidate.Tracks.Count > 0 || candidate.TrackCount is > 0
                ? Localization.Loc.T("MetadataSearch.TrackCountShort", candidate.Tracks.Count > 0 ? candidate.Tracks.Count : candidate.TrackCount!.Value)
                : string.Empty)
            : candidate.Album;
        Subtitle = string.Join(" · ", new[] { artist, second }.Where(s => !string.IsNullOrWhiteSpace(s)));
        var year = candidate.Year ?? MetadataSearchPanelViewModel.YearOf(candidate.ReleaseDate);
        YearText = year is > 0 ? year.Value.ToString() : string.Empty;
        Provider = candidate.Provider;
        var pct = (int)Math.Round(Math.Clamp(candidate.Confidence, 0, 1) * 100);
        ConfidenceText = $"{pct}%";
        // Thresholds as the brief reads them: Strong is "you can apply without looking",
        // Weak is "probably a different release".
        ConfidenceLevel = candidate.Confidence >= 0.85 ? 2 : candidate.Confidence >= 0.6 ? 1 : 0;
        ConfidenceLabel = Localization.Loc.T(ConfidenceLevel switch
        {
            2 => "MetadataSearch.Strong",
            1 => "MetadataSearch.Good",
            _ => "MetadataSearch.Weak",
        });
        NotesText = string.Join(" · ", candidate.MatchNotes);
    }

    public MetadataCandidate Candidate { get; }
    public string Title { get; }
    public string Subtitle { get; }
    public string YearText { get; }
    public bool HasYear => YearText.Length > 0;
    public string Provider { get; }
    public string ConfidenceText { get; }
    public string ConfidenceLabel { get; }
    /// <summary>0 = weak, 1 = good, 2 = strong (drives the pill's colour class).</summary>
    public int ConfidenceLevel { get; }
    public bool IsStrong => ConfidenceLevel == 2;
    public bool IsWeak => ConfidenceLevel == 0;
    public string NotesText { get; }
    public bool HasNotes => NotesText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail != null;

    public void Dispose()
    {
        var t = Thumbnail;
        Thumbnail = null;
        t?.Dispose();
    }
}

/// <summary>One Current | New row of the comparison.</summary>
public sealed partial class CompareRow : ObservableObject
{
    public CompareRow(MetadataSearchField field, string label, string current, string newDisplay,
        string newValue, bool isChanged, bool currentIsEmpty, string? blockedReason)
    {
        Field = field;
        Label = label;
        Current = current;
        New = newDisplay;
        NewValue = newValue;
        IsChanged = isChanged;
        CurrentIsEmpty = currentIsEmpty;
        BlockedReason = blockedReason;
    }

    public MetadataSearchField Field { get; }
    public string Label { get; }
    /// <summary>What the editor holds now ("—" when empty, "Mixed" across an album).</summary>
    public string Current { get; }
    public string New { get; }
    /// <summary>The value written into the edit field on Apply.</summary>
    public string NewValue { get; }
    public bool IsChanged { get; }
    public bool CurrentIsEmpty { get; }
    /// <summary>Why this editor can't store the field (album editor, unreadable tags), or null.</summary>
    public string? BlockedReason { get; }
    public bool IsBlocked => BlockedReason != null;
    public bool CanToggle => IsChanged && !IsBlocked;
    /// <summary>"Fills an empty field" — the quietest kind of change, marked as such.</summary>
    public bool IsFill => IsChanged && CurrentIsEmpty;

    [ObservableProperty] private bool _isChecked;
}

/// <summary>Album scope: one local track and the release track it maps onto.</summary>
public sealed partial class TrackMatchRow : ObservableObject
{
    public TrackMatchRow(Track local, CandidateTrack? match)
    {
        Local = local;
        Match = match;
        LocalNumber = NumberText(local.DiscNumber, local.TrackNumber);
        LocalTitle = local.Title ?? string.Empty;
        LocalDuration = DurationText(local.Duration);
        if (match != null)
        {
            NewTitle = string.IsNullOrWhiteSpace(match.Title) ? LocalTitle : match.Title.Trim();
            NewTrackNumber = match.TrackNumber is > 0 ? match.TrackNumber : null;
            NewDiscNumber = match.DiscNumber is > 0 ? match.DiscNumber : null;
            NewNumber = NumberText(NewDiscNumber ?? local.DiscNumber, NewTrackNumber ?? local.TrackNumber);
            NewDuration = match.Duration is { } d ? DurationText(d) : string.Empty;
            if (match.Duration is { } md && local.Duration > TimeSpan.Zero)
            {
                var delta = (int)Math.Round((md - local.Duration).TotalSeconds);
                DeltaText = delta == 0 ? "±0 s" : (delta > 0 ? $"+{delta} s" : $"−{-delta} s");
                // Past a few seconds it is likely another version (radio edit, remaster, live).
                DeltaIsLarge = Math.Abs(delta) > 5;
            }
            TitleChanges = !string.Equals(NewTitle, LocalTitle, StringComparison.Ordinal);
            NumberChanges = (NewTrackNumber is { } tn && tn != local.TrackNumber)
                            || (NewDiscNumber is { } dn && dn != Math.Max(1, local.DiscNumber));
        }
        IsIncluded = IsMatched && IsChanged;
    }

    public Track Local { get; }
    public CandidateTrack? Match { get; }
    public bool IsMatched => Match != null;
    public string LocalNumber { get; }
    public string LocalTitle { get; }
    public string LocalDuration { get; }
    public string NewTitle { get; } = string.Empty;
    public int? NewTrackNumber { get; }
    public int? NewDiscNumber { get; }
    public string NewNumber { get; } = string.Empty;
    public string NewDuration { get; } = string.Empty;
    public string DeltaText { get; } = string.Empty;
    public bool HasDelta => DeltaText.Length > 0;
    public bool DeltaIsLarge { get; }
    public bool TitleChanges { get; }
    public bool NumberChanges { get; }
    public bool IsChanged => TitleChanges || NumberChanges;
    public bool CanToggle => IsMatched && IsChanged;
    public string StatusText => Localization.Loc.T(!IsMatched ? "MetadataSearch.NoMatch"
        : IsChanged ? "MetadataSearch.WillChange" : "MetadataSearch.Same");

    [ObservableProperty] private bool _isIncluded;

    private static string NumberText(int disc, int track)
        => track <= 0 ? "–" : disc > 1 ? $"{disc}-{track:00}" : track.ToString("00");

    private static string DurationText(TimeSpan d)
        => d <= TimeSpan.Zero ? string.Empty : d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss");
}

/// <summary>Per-track values staged by an album-scope apply, written on Save.</summary>
public sealed record StagedTrackChange(string? Title, int? TrackNumber, int? DiscNumber);

/// <summary>What Apply hands the editor: the ticked field values, the chosen cover and the
/// included per-track changes.</summary>
public sealed record MetadataSearchPlan
{
    public string Provider { get; init; } = string.Empty;
    public IReadOnlyList<(MetadataSearchField Field, string Value)> Fields { get; init; } = Array.Empty<(MetadataSearchField, string)>();
    public byte[]? Artwork { get; init; }
    public IReadOnlyList<(Track Track, StagedTrackChange Change)> Tracks { get; init; } = Array.Empty<(Track, StagedTrackChange)>();
    public int Count => Fields.Count + (Artwork != null ? 1 : 0) + Tracks.Count;
}
