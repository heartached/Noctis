using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Services;
using Loc = Noctis.Localization.Loc;

namespace Noctis.ViewModels;

/// <summary>
/// GitHub #141 "Search Artist Info": the automatic lookup can land on a same-name stranger, so
/// the artist page's Options menu lets the user search Deezer and pick the right account (photo,
/// name, fans, albums). The pick is kept per artist; the picture, fan count and About card then
/// follow it until "Use Automatic Artist Info".
/// </summary>
public partial class ArtistInfoSearchViewModel : ViewModelBase
{
    private readonly Func<string, CancellationToken, Task<IReadOnlyList<DeezerArtistPhotos.SearchHit>>> _search;
    private readonly Func<string, CancellationToken, Task<Bitmap?>>? _thumbnail;
    private readonly Func<DeezerArtistPhotos.SearchHit, Task> _apply;
    private CancellationTokenSource? _searchCts;

    /// <param name="artistName">Prefills the search.</param>
    /// <param name="currentId">The account the artist is pinned to (marked "Current"), or null.</param>
    /// <param name="search">Deezer's list for a query.</param>
    /// <param name="thumbnail">Loads a row's small photo; null shows none (tests).</param>
    /// <param name="apply">Keeps the pick and refreshes the page.</param>
    public ArtistInfoSearchViewModel(
        string artistName,
        long? currentId,
        Func<string, CancellationToken, Task<IReadOnlyList<DeezerArtistPhotos.SearchHit>>> search,
        Func<string, CancellationToken, Task<Bitmap?>>? thumbnail,
        Func<DeezerArtistPhotos.SearchHit, Task> apply)
    {
        _search = search;
        _thumbnail = thumbnail;
        _apply = apply;
        _query = artistName ?? string.Empty;
        CurrentId = currentId;
    }

    public long? CurrentId { get; }

    public ObservableCollection<ArtistInfoSearchRow> Results { get; } = new();

    [ObservableProperty] private string _query;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isApplying;
    [ObservableProperty] private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private ArtistInfoSearchRow? _selectedResult;

    public event EventHandler? Closed;

    [RelayCommand]
    public async Task Search()
    {
        _searchCts?.Cancel();
        var cts = _searchCts = new CancellationTokenSource();
        var query = (Query ?? string.Empty).Trim();
        Results.Clear();
        SelectedResult = null;
        if (query.Length == 0)
        {
            StatusText = string.Empty;
            return;
        }

        IsSearching = true;
        StatusText = Loc.T("ArtistInfoSearch.Searching");
        try
        {
            var hits = await _search(query, cts.Token);
            if (cts.IsCancellationRequested) return;
            foreach (var hit in OrderForPicker(hits, query))
                Results.Add(new ArtistInfoSearchRow(hit, hit.Id == CurrentId));
            SelectedResult = Results.FirstOrDefault(r => r.IsCurrent);
            StatusText = Results.Count == 0 ? Loc.T("ArtistInfoSearch.NothingFound") : string.Empty;
            if (_thumbnail != null)
                foreach (var row in Results.ToList())
                    _ = LoadThumbnailAsync(row, cts.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!cts.IsCancellationRequested)
                StatusText = Loc.T("ArtistInfoSearch.Failed", ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_searchCts, cts)) IsSearching = false;
        }
    }

    /// <summary>
    /// Deezer's list as the picker shows it: accounts with exactly the asked name first, most
    /// fans first, then everyone else in Deezer's own order. Deezer ranks by its own relevance,
    /// not by fans: for "Michael Jackson" (measured 10-24) a 226-fan, 1-album namesake came
    /// first, the real artist (13,220,407 fans, 38 albums) second and "TK" third. The
    /// automatic lookup ranks the same way (<see cref="DeezerArtistPhotos.FindAsync"/>).
    /// </summary>
    public static IReadOnlyList<DeezerArtistPhotos.SearchHit> OrderForPicker(
        IReadOnlyList<DeezerArtistPhotos.SearchHit> hits, string query)
    {
        // RankMatch 0 = same name, 1 = same name without spaces; OrderBy is stable, so the
        // rest keep Deezer's order.
        int Tier(DeezerArtistPhotos.SearchHit h) => Math.Min(DeezerArtistPhotos.RankMatch(h.Name, query), 2);
        return hits.OrderBy(Tier).ThenByDescending(h => Tier(h) < 2 ? h.Fans : 0).ToList();
    }

    private async Task LoadThumbnailAsync(ArtistInfoSearchRow row, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(row.Hit.ThumbnailUrl)) return;
        try
        {
            var bitmap = await _thumbnail!(row.Hit.ThumbnailUrl, ct);
            if (bitmap == null) return;
            if (ct.IsCancellationRequested) { bitmap.Dispose(); return; }
            await Dispatcher.UIThread.InvokeAsync(() => row.Thumbnail = bitmap);
        }
        catch { /* a missing photo is cosmetic */ }
    }

    private bool CanApply() => SelectedResult != null && !IsApplying;

    partial void OnIsApplyingChanged(bool value) => ApplyCommand.NotifyCanExecuteChanged();

    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task Apply()
    {
        if (SelectedResult is not { } row) return;
        IsApplying = true;
        StatusText = Loc.T("ArtistInfoSearch.Applying");
        try
        {
            await _apply(row.Hit);
            Close();
        }
        catch (Exception ex)
        {
            StatusText = Loc.T("ArtistInfoSearch.Failed", ex.Message);
        }
        finally
        {
            IsApplying = false;
        }
    }

    [RelayCommand]
    private void Close()
    {
        StopSearch();
        Closed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stops a running search (the window is going).</summary>
    public void StopSearch()
    {
        _searchCts?.Cancel();
        IsSearching = false;
    }
}

/// <summary>One Deezer account in the picker.</summary>
public sealed partial class ArtistInfoSearchRow : ObservableObject
{
    public ArtistInfoSearchRow(DeezerArtistPhotos.SearchHit hit, bool isCurrent)
    {
        Hit = hit;
        IsCurrent = isCurrent;
    }

    public DeezerArtistPhotos.SearchHit Hit { get; }
    public string Name => Hit.Name;
    public bool IsCurrent { get; }

    /// <summary>"7,994,069 fans · 88 albums".</summary>
    public string Detail
    {
        get
        {
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            var fans = Hit.Fans == 1
                ? Loc.T("ArtistInfoSearch.OneFan")
                : Loc.T("ArtistDetail.FansCount", Hit.Fans.ToString("N0", culture));
            if (Hit.Albums <= 0)
                return fans;
            return fans + " · " + (Hit.Albums == 1
                ? Loc.T("ArtistInfoSearch.OneAlbum")
                : Loc.T("ArtistInfoSearch.AlbumsCount", Hit.Albums.ToString("N0", culture)));
        }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasThumbnail))]
    private Bitmap? _thumbnail;

    public bool HasThumbnail => Thumbnail != null;

    public override string ToString() => $"{Name}, {Detail}";
}
