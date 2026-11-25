using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Models;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// GitHub #267 "Get Genres from Discogs": one row per album with its genre now, the genres its
/// Discogs master release (or a release, by option) lists, and the match they came from. The
/// lookup only proposes; ticked rows are written by Apply, after the user looked them over. The
/// options that shape the genre (source, how many, add or replace) apply to the finds at once;
/// "use a release" needs a new lookup. A whole-library run keeps going when hidden.
/// </summary>
public partial class BulkDiscogsGenresViewModel : ViewModelBase
{
    private readonly DiscogsGenreService _service;
    private readonly IMetadataService? _metadata;
    private readonly ILibraryService? _library;
    private readonly bool _isLibraryRun;
    private readonly Action<DiscogsGenreOptions>? _savePrefs;
    private readonly Action<string> _openUrl;
    private readonly bool _loading;
    private CancellationTokenSource? _cts;

    private static string L(string key) => Noctis.Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Noctis.Localization.Loc.T(key, args);

    public string TitleText { get; }
    public ObservableCollection<Row> Rows { get; } = new();

    /// <summary>Source picker rows, in <see cref="DiscogsGenreSource"/> order.</summary>
    public IReadOnlyList<string> SourceOptions { get; } = new[]
    {
        L("DiscogsGenres.SourceStyles"), L("DiscogsGenres.SourceGenres"), L("DiscogsGenres.SourceBoth"),
    };

    /// <summary>"How many" picker rows: index 0 = all, else the count.</summary>
    public IReadOnlyList<string> MaxOptions { get; } =
        new[] { L("DiscogsGenres.MaxAll") }.Concat(Enumerable.Range(1, 5).Select(n => n.ToString())).ToArray();

    [ObservableProperty] private int _sourceIndex;
    [ObservableProperty] private int _maxIndex;
    [ObservableProperty] private bool _useRelease;
    [ObservableProperty] private bool _addToExisting;

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusMessage = L("BulkLyrics.Ready");

    public event EventHandler? Closed;

    public BulkDiscogsGenresViewModel(
        IReadOnlyList<IReadOnlyList<Track>> albums,
        DiscogsGenreService service,
        IMetadataService? metadata,
        ILibraryService? library,
        bool isLibraryRun = false,
        DiscogsGenreOptions? prefs = null,
        Action<DiscogsGenreOptions>? savePrefs = null,
        Action<string>? openUrl = null)
    {
        _service = service;
        _metadata = metadata;
        _library = library;
        _isLibraryRun = isLibraryRun;
        _savePrefs = savePrefs;
        _openUrl = openUrl ?? (url => Helpers.PlatformHelper.OpenUrl(url));

        _loading = true;
        var p = prefs ?? new DiscogsGenreOptions();
        SourceIndex = Math.Clamp((int)p.Source, 0, SourceOptions.Count - 1);
        MaxIndex = Math.Clamp(p.MaxGenres, 0, MaxOptions.Count - 1);
        UseRelease = p.UseRelease;
        AddToExisting = p.AddToExisting;
        _loading = false;

        var n = albums.Count;
        TitleText = n == 1 ? L("DiscogsGenres.TitleOne") : Noctis.Localization.Loc.Plural("DiscogsGenres.TitleMany", n);
        foreach (var album in albums.Where(a => a.Count > 0))
        {
            var row = new Row(album);
            row.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(Row.IsIncluded)) RaiseCans();
            };
            Rows.Add(row);
        }
        if (Rows.Count == 0) StatusMessage = L("BulkArtwork.NoAlbums");
    }

    /// <summary>The options as set now.</summary>
    public DiscogsGenreOptions Options => new(
        (DiscogsGenreSource)Math.Clamp(SourceIndex, 0, 2), Math.Max(0, MaxIndex), UseRelease, AddToExisting);

    public bool CanStart => !IsRunning && !IsApplying && Rows.Any(r => r.NeedsLookup);
    public bool CanApply => !IsRunning && !IsApplying && Rows.Any(r => r.IsIncluded && r.HasChange);
    public bool CanChangeOptions => !IsRunning && !IsApplying;
    public string CloseText => IsRunning && _isLibraryRun ? L("BulkLyrics.Hide") : L("BulkLyrics.Close");
    /// <summary>After a stopped run the primary button reads "Continue" rather than "Find".</summary>
    public string StartText => Rows.Any(r => r.State != RowState.Pending) ? L("DiscogsGenres.Continue") : L("BulkArtwork.Find");

    private void RaiseCans()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanApply));
        OnPropertyChanged(nameof(CanChangeOptions));
        OnPropertyChanged(nameof(CloseText));
        OnPropertyChanged(nameof(StartText));
    }

    partial void OnIsRunningChanged(bool value) => RaiseCans();
    partial void OnIsApplyingChanged(bool value) => RaiseCans();

    partial void OnSourceIndexChanged(int value) => OptionsChanged();
    partial void OnMaxIndexChanged(int value) => OptionsChanged();
    partial void OnAddToExistingChanged(bool value) => OptionsChanged();

    partial void OnUseReleaseChanged(bool value)
    {
        if (_loading) return;
        // Another kind of Discogs record: what was found needs looking up again.
        foreach (var row in Rows.Where(r => r.State is RowState.Found or RowState.NotFound or RowState.Failed))
            row.Reset();
        RaiseCans();
        _savePrefs?.Invoke(Options);
    }

    private void OptionsChanged()
    {
        if (_loading) return;
        var options = Options;
        foreach (var row in Rows) row.Recompute(options);
        RaiseCans();
        _savePrefs?.Invoke(options);
    }

    [RelayCommand]
    private async Task Start()
    {
        if (!CanStart) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _service.BeginRun();
        var todo = Rows.Where(r => r.NeedsLookup).ToList();
        var done = 0;
        var busy = false;
        Progress = 0;
        StatusMessage = L("BulkLyrics.LookingUpProgress", 0, todo.Count);
        try
        {
            foreach (var row in todo)
            {
                ct.ThrowIfCancellationRequested();
                row.SetState(RowState.Searching);
                try
                {
                    var match = await _service.FindAsync(row.Query, UseRelease, ct);
                    row.SetMatch(match, Options);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    row.Reset();
                    throw;
                }
                catch (DiscogsBusyException)
                {
                    row.SetState(RowState.Failed);
                    busy = true;
                    break;
                }
                catch (Exception ex)
                {
                    DebugLog.Write(DiscogsGenreService.LogSource, $"lookup failed for \"{row.Title}\": {ex.Message}");
                    row.SetState(RowState.Failed);
                }
                done++;
                Progress = todo.Count == 0 ? 1 : done / (double)todo.Count;
                StatusMessage = L("BulkLyrics.LookingUpProgress", done, todo.Count);
            }
            StatusMessage = busy ? L("DiscogsGenres.Busy") : Summary();
        }
        catch (OperationCanceledException)
        {
            StatusMessage = L("BulkLyrics.StoppedSummary", Summary());
        }
        finally
        {
            IsRunning = false;
            Progress = 1;
        }
    }

    internal string Summary()
    {
        int found = Rows.Count(r => r.State == RowState.Found);
        int missing = Rows.Count(r => r.State == RowState.NotFound);
        int failed = Rows.Count(r => r.State == RowState.Failed);
        var parts = new List<string>();
        if (found > 0) parts.Add(L("DiscogsGenres.SumFound", found));
        if (missing > 0) parts.Add(L("DiscogsGenres.SumNotFound", missing));
        if (failed > 0) parts.Add(L("BulkLyrics.SumFailed", failed));
        return parts.Count == 0 ? L("BulkLyrics.SumNothing") : string.Join(" · ", parts);
    }

    [RelayCommand]
    private void Stop()
    {
        if (IsRunning) _cts?.Cancel();
    }

    /// <summary>Writes the ticked rows' genres to their songs (library, then files).</summary>
    [RelayCommand]
    private async Task Apply()
    {
        if (!CanApply || _metadata is null) return;
        IsApplying = true;
        StatusMessage = L("DiscogsGenres.Saving");
        var options = Options;
        var rows = Rows.Where(r => r.IsIncluded && r.HasChange && r.Match is not null).ToList();
        var changes = rows.SelectMany(r => r.Tracks.Select(t => (t, DiscogsGenreService.ComposeGenre(t.Genre, r.Match!, options)))).ToList();
        try
        {
            var result = await DiscogsGenreService.ApplyAsync(changes, _metadata, _library);
            var failed = result.Failed.ToHashSet();
            foreach (var row in rows)
                row.MarkSaved(row.Tracks.Any(failed.Contains));
            StatusMessage = failed.Count == 0
                ? L("DiscogsGenres.Saved", rows.Count)
                : L("DiscogsGenres.SavedSomeFailed", rows.Count(r => r.State == RowState.Saved), failed.Count);
        }
        catch (Exception ex)
        {
            StatusMessage = L("BulkLyrics.Failed", ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void OpenMatch(Row? row)
    {
        if (row?.Match?.Url is { Length: > 0 } url) _openUrl(url);
    }

    [RelayCommand]
    private void OpenDiscogs() => _openUrl("https://www.discogs.com");

    /// <summary>Close; while a selection run looks up it stops, a whole-library run keeps going hidden.</summary>
    [RelayCommand]
    private void Cancel()
    {
        if (!_isLibraryRun) Stop();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>The dialog closed some other way (Alt+F4, the owner closing): a selection lookup stops.</summary>
    public void OnDialogClosed()
    {
        if (!_isLibraryRun) Stop();
    }

    public enum RowState { Pending, Searching, Found, NotFound, Failed, Saved, WriteFailed }

    public sealed partial class Row : ObservableObject
    {
        public Row(IReadOnlyList<Track> tracks)
        {
            Tracks = tracks;
            var rep = tracks[0];
            Title = rep.Album;
            Subtitle = AnimatedArtworkBulkService.AlbumArtist(rep);
            var year = tracks.Select(t => t.Year).FirstOrDefault(y => y > 0);
            Query = new DiscogsAlbumQuery(Subtitle, Title, year);
            IsExplicit = tracks.Any(t => t.IsExplicit);
            RefreshCurrent();
        }

        public IReadOnlyList<Track> Tracks { get; }
        public DiscogsAlbumQuery Query { get; }
        public string Title { get; }
        public string Subtitle { get; }
        public bool IsExplicit { get; }

        [ObservableProperty] private RowState _state;
        [ObservableProperty] private string _status = string.Empty;
        [ObservableProperty] private string _currentText = string.Empty;
        [ObservableProperty] private string _proposedText = string.Empty;
        [ObservableProperty] private string _matchText = string.Empty;
        [ObservableProperty] private bool _hasChange;
        [ObservableProperty] private bool _isIncluded;
        [ObservableProperty] private bool _failed;
        [ObservableProperty] private bool _succeeded;
        public DiscogsAlbumMatch? Match { get; private set; }

        public bool HasMatch => Match is not null && State is RowState.Found or RowState.Saved or RowState.WriteFailed;
        public bool CanInclude => State == RowState.Found && HasChange;
        internal bool NeedsLookup => State is RowState.Pending or RowState.Failed;

        partial void OnStateChanged(RowState value)
        {
            Status = value switch
            {
                RowState.Searching => L("DiscogsGenres.Searching"),
                RowState.NotFound => L("DiscogsGenres.NotFound"),
                RowState.Failed => L("DiscogsGenres.LookupFailed"),
                RowState.Saved => L("DiscogsGenres.RowSaved"),
                RowState.WriteFailed => L("DiscogsGenres.RowWriteFailed"),
                RowState.Found => HasChange ? string.Empty : L("DiscogsGenres.NoChange"),
                _ => string.Empty,
            };
            Failed = value is RowState.Failed or RowState.WriteFailed;
            Succeeded = value == RowState.Saved;
            OnPropertyChanged(nameof(HasMatch));
            OnPropertyChanged(nameof(CanInclude));
        }

        partial void OnHasChangeChanged(bool value)
        {
            OnPropertyChanged(nameof(CanInclude));
            if (State == RowState.Found) OnStateChanged(State);
        }

        private void RefreshCurrent()
        {
            var genres = Tracks.Select(t => t.Genre).Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            CurrentText = genres.Count switch
            {
                0 => L("DiscogsGenres.NoGenre"),
                1 => Track.DotGenres(genres[0]),
                _ => L("DiscogsGenres.Mixed"),
            };
        }

        internal void SetState(RowState state) => State = state;

        internal void Reset()
        {
            Match = null;
            MatchText = string.Empty;
            ProposedText = string.Empty;
            HasChange = false;
            IsIncluded = false;
            State = RowState.Pending;
        }

        internal void SetMatch(DiscogsAlbumMatch? match, DiscogsGenreOptions options)
        {
            Match = match;
            if (match is null)
            {
                MatchText = string.Empty;
                ProposedText = string.Empty;
                HasChange = false;
                IsIncluded = false;
                State = RowState.NotFound;
                return;
            }
            var kind = string.Equals(match.Kind, "master", StringComparison.OrdinalIgnoreCase)
                ? L("DiscogsGenres.Master") : L("DiscogsGenres.Release");
            var name = match.Artist.Length > 0 ? $"{match.Artist} – {match.Title}" : match.Title;
            MatchText = match.Year > 0 ? $"{name} ({match.Year}) · {kind}" : $"{name} · {kind}";
            State = RowState.Found;
            Recompute(options, initial: true);
        }

        /// <summary>The proposed genre under <paramref name="options"/>. A row the user unticked stays unticked.</summary>
        internal void Recompute(DiscogsGenreOptions options, bool initial = false)
        {
            if (Match is null || State != RowState.Found) return;
            var proposals = Tracks.Select(t => (t, DiscogsGenreService.ComposeGenre(t.Genre, Match, options))).ToList();
            var hadChange = HasChange;
            var change = proposals.Any(p => !string.Equals(p.t.Genre, p.Item2, StringComparison.Ordinal));
            ProposedText = Track.DotGenres(DiscogsGenreService.ComposeGenre(Tracks[0].Genre, Match, options));
            HasChange = change;
            IsIncluded = change && (initial || IsIncluded || !hadChange);
        }

        internal void MarkSaved(bool failed)
        {
            RefreshCurrent();
            IsIncluded = false;
            State = failed ? RowState.WriteFailed : RowState.Saved;
        }
    }
}
