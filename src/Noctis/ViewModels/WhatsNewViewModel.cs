using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>A "#123" after a change: opens that GitHub issue.</summary>
public sealed record WhatsNewIssueLink(int Number, ICommand Open)
{
    public string Text => "#" + Number.ToString(CultureInfo.InvariantCulture);
    public string Tip => Loc.T("WhatsNew.IssueTip", Text);
}

/// <summary>One change row: the line (SemiBold only when a detail follows) and its issue links.</summary>
public sealed class WhatsNewItemViewModel
{
    public WhatsNewItemViewModel(ReleaseNoteItem item, Func<int, ICommand> openIssue)
    {
        Title = item.Title;
        Detail = item.Detail;
        Issues = item.Issues.Select(n => new WhatsNewIssueLink(n, openIssue(n))).ToList();
    }

    public string Title { get; }
    public string? Detail { get; }
    public bool HasDetail => !string.IsNullOrEmpty(Detail);
    public IReadOnlyList<WhatsNewIssueLink> Issues { get; }
    public bool HasIssues => Issues.Count > 0;
}

/// <summary>One group card: Added, Improved, Fixed or Other.</summary>
public sealed class WhatsNewSectionViewModel
{
    public WhatsNewSectionViewModel(ReleaseNoteKind kind, IReadOnlyList<WhatsNewItemViewModel> items)
    {
        Kind = kind;
        Items = items;
    }

    public ReleaseNoteKind Kind { get; }
    public IReadOnlyList<WhatsNewItemViewModel> Items { get; }
    public string Label => Loc.T("WhatsNew." + Kind).ToUpper(CultureInfo.CurrentUICulture);
    public bool IsAdded => Kind == ReleaseNoteKind.Added;
    public bool IsImproved => Kind == ReleaseNoteKind.Improved;
    public bool IsFixed => Kind == ReleaseNoteKind.Fixed;
    public bool IsOther => Kind == ReleaseNoteKind.Other;
}

/// <summary>
/// Settings › About › What's new: the running version's GitHub release notes, grouped. The notes
/// are fetched only when the dialog opens (never at startup) and kept in whats_new_cache.json:
/// a cached copy shows at once and is refreshed in the background when it is a day old.
/// Offline or with no release for this version (a dev build) it shows a short line instead.
/// </summary>
public partial class WhatsNewViewModel : ViewModelBase
{
    private readonly IReleaseNotesSource? _source;
    private readonly WhatsNewCache _cache;
    private readonly Func<DateTimeOffset> _now;

    public WhatsNewViewModel(string version, IReleaseNotesSource? source, WhatsNewCache cache,
        Func<DateTimeOffset>? now = null)
    {
        Version = version;
        _source = source;
        _cache = cache;
        _now = now ?? (() => DateTimeOffset.UtcNow);
        var title = Loc.T("WhatsNew.Title");
        var at = title.IndexOf("{0}", StringComparison.Ordinal);
        TitleBefore = at < 0 ? title + " " : title[..at];
        TitleAfter = at < 0 ? "" : title[(at + 3)..];
    }

    /// <summary>"1.7.3": the version whose notes these are.</summary>
    public string Version { get; }
    public string Tag => "v" + Version;

    /// <summary>The title around the accent "Noctis 1.7.3" ("What's New in " + name + "").</summary>
    public string TitleBefore { get; }
    public string TitleVersion => "Noctis " + Version;
    public string TitleAfter { get; }

    public string ReleaseUrl => $"https://github.com/heartached/Noctis/releases/tag/{Tag}";

    /// <summary>Opens a web page; tests swap it so no browser starts.</summary>
    internal Action<string> OpenUrl { get; set; } = Helpers.PlatformHelper.OpenUrl;

    public ObservableCollection<WhatsNewSectionViewModel> Sections { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(IsUnavailable))]
    private bool _isLoading = true; // until LoadAsync knows, so "unavailable" never flashes first

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(IsUnavailable))]
    private bool _hasNotes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSubtitle))]
    private string? _subtitle;

    public bool HasSubtitle => !string.IsNullOrEmpty(Subtitle);
    public bool IsLoaded => HasNotes;
    public bool IsUnavailable => !IsLoading && !HasNotes;

    /// <summary>The background refresh behind a cached copy (tests wait on it).</summary>
    internal Task Refresh { get; private set; } = Task.CompletedTask;

    private bool _primed;
    private WhatsNewCacheEntry? _primedEntry;

    /// <summary>
    /// Before the dialog opens: reads and parses the cached notes off the UI thread and fills
    /// the rows, so the dialog's first frame already has them. Reading the file and building
    /// every row inside OnOpened ran in the middle of the open animation and made it stutter
    /// (owner 11-26).
    /// </summary>
    public async Task PrimeAsync()
    {
        if (_primed) return;
        var (entry, sections) = await Task.Run(() =>
        {
            var e = _cache.Load(Tag);
            return (e, e is null ? null : ReleaseNotesParser.Parse(e.Body));
        });
        _primed = true;
        _primedEntry = entry;
        if (entry is null || sections is null) return;
        Show(entry.PublishedAt, sections);
        IsLoading = false;
    }

    /// <summary>Shows the cached notes at once, else fetches; a day-old cache refreshes behind.</summary>
    public async Task LoadAsync()
    {
        var cached = _primed ? _primedEntry : _cache.Load(Tag);
        if (cached is not null)
        {
            if (!_primed) Show(cached.PublishedAt, cached.Body);
            IsLoading = false;
            if (_now() - cached.FetchedUtc < WhatsNewCache.RefreshAfter) return;
            Refresh = FetchAsync(keepOnFailure: true);
            await Refresh;
            return;
        }

        try { await FetchAsync(keepOnFailure: false); }
        finally { IsLoading = false; }
    }

    private async Task FetchAsync(bool keepOnFailure)
    {
        if (_source is null) return;
        try
        {
            var doc = await _source.FetchAsync(Tag);
            if (doc is null)
            {
                DebugLog.Write("WhatsNew", $"No GitHub release for {Tag}.");
                if (!keepOnFailure) Clear();
                return;
            }
            var entry = new WhatsNewCacheEntry
            {
                Tag = Tag, PublishedAt = doc.PublishedAt, Body = doc.Body, FetchedUtc = _now(),
            };
            await Task.Run(() => _cache.Save(entry)); // the file write stays off the UI thread
            Show(doc.PublishedAt, doc.Body);
        }
        catch (Exception ex)
        {
            DebugLog.Write("WhatsNew", $"Release notes for {Tag} could not be fetched: {ex.Message}");
            if (!keepOnFailure) Clear();
        }
    }

    private void Show(DateTimeOffset? published, string body) => Show(published, ReleaseNotesParser.Parse(body));

    private void Show(DateTimeOffset? published, IReadOnlyList<ReleaseNoteSection> sections)
    {
        Sections.Clear();
        foreach (var s in sections)
            Sections.Add(new WhatsNewSectionViewModel(s.Kind,
                s.Items.Select(i => new WhatsNewItemViewModel(i, IssueCommand)).ToList()));
        Subtitle = published is { } date ? Loc.T("WhatsNew.Released", date.LocalDateTime) : null;
        HasNotes = Sections.Count > 0;
    }

    private void Clear()
    {
        Sections.Clear();
        Subtitle = null;
        HasNotes = false;
    }

    private ICommand IssueCommand(int number) =>
        new RelayCommand(() => OpenUrl($"https://github.com/heartached/Noctis/issues/{number}"));

    [RelayCommand]
    private void OpenOnGitHub() => OpenUrl(ReleaseUrl);
}
