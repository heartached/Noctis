using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Helpers;
using Noctis.Services;

namespace Noctis.ViewModels;

/// <summary>
/// Drives the playlist-import dialog: read an Exportify CSV / TuneMyMusic JSON / m3u export or
/// a pasted Deezer/TIDAL link, fuzzy-match its entries against the library, then create a playlist
/// from the matches and show a report of the tracks that couldn't be found.
/// </summary>
public partial class PlaylistImportViewModel : ViewModelBase
{
    private readonly IPlaylistImportService _service;
    private readonly ITidalAuthService _tidal;
    private PlaylistImportPreview? _preview;
    private CancellationTokenSource? _analyzeCts;

    private static string L(string key) => Localization.Loc.T(key);
    private static string L(string key, params object[] args) => Localization.Loc.T(key, args);

    /// <summary>One entry of the preview list: a title, its artist under it, and whether the
    /// library has it (the row's Found / Missing chip).</summary>
    public sealed record ImportRow(string Title, string Artist, bool IsMatched)
    {
        public bool HasArtist => Artist.Length > 0;

        /// <summary>Splits the service's "Artist – Title" label for the two-line row.</summary>
        public static ImportRow From(string label, bool matched)
        {
            var at = label.IndexOf(" – ", StringComparison.Ordinal);
            return at > 0
                ? new ImportRow(label[(at + 3)..], label[..at], matched)
                : new ImportRow(label, string.Empty, matched);
        }
    }

    [ObservableProperty] private bool _isBusy;
    /// <summary>Busy reading/fetching and matching (not creating or signing in): the list area
    /// shows its loading skeleton.</summary>
    [ObservableProperty] private bool _isAnalyzing;
    /// <summary>Footer status. Empty until something happens: the start panel says what to do.</summary>
    [ObservableProperty] private string _statusMessage = string.Empty;
    /// <summary>Pasted share link (Deezer or TIDAL playlist/album).</summary>
    [ObservableProperty] private string _linkText = string.Empty;
    public bool CanImportLink => !IsBusy && IsImportableLink(LinkText);

    /// <summary>Deezer always; TIDAL only in a build that carries a TIDAL client id.</summary>
    private static bool IsImportableLink(string? text)
        => DeezerPlaylistLink.TryParse(text, out _, out _) ||
           (TidalOAuth.IsConfigured && TidalPlaylistLink.TryParse(text, out _, out _));

    /// <summary>Guidance shown when the pasted link is a service Noctis can't fetch (Spotify,
    /// Apple Music, …): what to do instead, with a button to the exporter site.</summary>
    [ObservableProperty] private string _linkHelp = string.Empty;
    [ObservableProperty] private string _linkHelpLabel = string.Empty;
    private string _linkHelpUrl = string.Empty;
    /// <summary>The help button runs the TIDAL browser sign-in instead of opening a URL.</summary>
    private bool _linkHelpIsTidalSignIn;
    public bool HasLinkHelp => LinkHelp.Length > 0;
    [ObservableProperty] private string _playlistName = string.Empty;
    [ObservableProperty] private bool _hasPreview;
    [ObservableProperty] private bool _canCreate;
    [ObservableProperty] private int _matchedCount;
    [ObservableProperty] private int _missingCount;
    [ObservableProperty] private bool _hasMissing;
    /// <summary>The playlist was created: the footer's Cancel reads Done.</summary>
    [ObservableProperty] private bool _isCreated;

    public ObservableCollection<string> MissingTracks { get; } = new();

    /// <summary>Every entry of the preview, missing ones first (they are what needs a look).
    /// Replaced whole per analysis so a long playlist is one list change, not one per row.</summary>
    [ObservableProperty] private IReadOnlyList<ImportRow> _rows = Array.Empty<ImportRow>();

    /// <summary>Nothing loaded and nothing loading: the drop / formats panel shows.</summary>
    public bool ShowStart => !HasPreview && !IsAnalyzing;

    public string CancelLabel => IsCreated ? L("Import.Done") : L("Metadata.Cancel");

    public event EventHandler? Closed;

    public PlaylistImportViewModel(IPlaylistImportService service, ITidalAuthService tidal)
    {
        _service = service;
        _tidal = tidal;
    }

    partial void OnHasPreviewChanged(bool value) => OnPropertyChanged(nameof(ShowStart));
    partial void OnIsAnalyzingChanged(bool value) => OnPropertyChanged(nameof(ShowStart));
    partial void OnIsCreatedChanged(bool value) => OnPropertyChanged(nameof(CancelLabel));

    partial void OnLinkTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanImportLink));

        // A TIDAL link this build can fetch itself gets no "convert it elsewhere" hint.
        var hint = IsImportableLink(value) ? null : StreamingLinkHints.For(value);
        ShowLinkHelp(hint?.Message, hint?.HelpLabel, hint?.HelpUrl);

        // A complete Deezer/TIDAL link is unambiguous: import as soon as it lands (paste, prefill,
        // typing the last digit) instead of asking for a second click.
        if (CanImportLink) _ = ImportLink();
    }

    private void ShowLinkHelp(string? message, string? label, string? url, bool tidalSignIn = false)
    {
        LinkHelp = message ?? string.Empty;
        LinkHelpLabel = label ?? string.Empty;
        _linkHelpUrl = url ?? string.Empty;
        _linkHelpIsTidalSignIn = tidalSignIn;
        OnPropertyChanged(nameof(HasLinkHelp));
    }

    /// <summary>Pre-fills a link the dialog found on the clipboard when it opened.</summary>
    public void OfferClipboardText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || LinkText.Length > 0) return;
        var t = text.Trim();
        if (IsImportableLink(t) || StreamingLinkHints.For(t) is not null)
            LinkText = t;
    }

    [RelayCommand]
    private void OpenLinkHelp()
    {
        if (_linkHelpIsTidalSignIn) _ = ConnectTidalAsync();
        else if (_linkHelpUrl.Length > 0) PlatformHelper.OpenUrl(_linkHelpUrl);
    }

    /// <summary>
    /// Browser sign-in to TIDAL, then the pending link imports on its own. Busy for the whole
    /// wait so a second click can't start a second listener on the callback port.
    /// </summary>
    private async Task ConnectTidalAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = L("Import.StatusTidalWaiting");
        bool ok;
        try { ok = await _tidal.LoginAsync(); }
        finally { IsBusy = false; }

        if (!ok)
        {
            StatusMessage = L("Import.StatusTidalFailed");
            return;
        }
        ShowLinkHelp(null, null, null);
        if (CanImportLink) await ImportLink();
        else StatusMessage = L("Import.StatusTidalSignedIn");
    }

    private void OfferTidalSignIn()
    {
        StatusMessage = L("Import.StatusTidalNeeded");
        ShowLinkHelp(L("Import.TidalHelp"), L("Import.TidalSignIn"), null, tidalSignIn: true);
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanImportLink));

    /// <summary>Called by the dialog after the user picks a file.</summary>
    public Task LoadFileAsync(string path)
        => AnalyzeAsync(L("Import.StatusMatching"), L("Import.StatusEmptyFile"), "Import.StatusReadFailed",
            ct => _service.AnalyzeAsync(path, ct));

    [RelayCommand]
    private Task ImportLink()
    {
        if (!CanImportLink) return Task.CompletedTask;
        var service = TidalPlaylistLink.TryParse(LinkText, out _, out _) ? "TIDAL" : "Deezer";
        return AnalyzeAsync(L("Import.StatusFetching", service), L("Import.StatusEmptyLink", service),
            "Import.StatusFetchFailed", ct => _service.AnalyzeLinkAsync(LinkText, ct));
    }

    /// <param name="errorKey">Format key taking the exception message as {0}.</param>
    private async Task AnalyzeAsync(string busyMessage, string emptyMessage, string errorKey,
        Func<CancellationToken, Task<PlaylistImportPreview>> analyze)
    {
        if (IsBusy) return;
        IsBusy = true;
        IsAnalyzing = true;
        StatusMessage = busyMessage;
        MissingTracks.Clear();
        Rows = Array.Empty<ImportRow>();
        HasPreview = false;
        CanCreate = false;
        IsCreated = false;

        _analyzeCts?.Cancel();
        _analyzeCts?.Dispose();
        _analyzeCts = new CancellationTokenSource();

        try
        {
            var preview = await analyze(_analyzeCts.Token);
            _preview = preview;
            PlaylistName = preview.SuggestedName;
            MatchedCount = preview.MatchedTrackIds.Count;
            MissingCount = preview.MissingLabels.Count;
            HasMissing = MissingCount > 0;
            foreach (var m in preview.MissingLabels) MissingTracks.Add(m);
            Rows = preview.MissingLabels.Select(m => ImportRow.From(m, matched: false))
                .Concat(preview.MatchedLabels.Select(m => ImportRow.From(m, matched: true)))
                .ToList();
            HasPreview = preview.TotalEntries > 0;
            CanCreate = MatchedCount > 0;
            StatusMessage = HasPreview
                ? L("Import.StatusResult", MatchedCount, preview.TotalEntries)
                : emptyMessage;
        }
        catch (OperationCanceledException)
        {
            // Dialog closed mid-analysis; the background match loop stops here.
        }
        catch (TidalNotConnectedException)
        {
            OfferTidalSignIn();
        }
        catch (Exception ex)
        {
            StatusMessage = L(errorKey, ex.Message);
        }
        finally
        {
            IsAnalyzing = false;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Create()
    {
        if (IsBusy || _preview is null || _preview.MatchedTrackIds.Count == 0) return;
        IsBusy = true;
        StatusMessage = L("Import.StatusCreating");

        await _service.CreateAsync(PlaylistName, _preview.MatchedTrackIds);

        // Reflect the new playlist in the sidebar immediately.
        var main = App.Services?.GetService<MainWindowViewModel>();
        if (main is not null) await main.Sidebar.LoadPlaylistsAsync();

        CanCreate = false;
        IsBusy = false;
        IsCreated = true;
        var count = _preview.MatchedTrackIds.Count;
        StatusMessage = count == 1
            ? L("Import.StatusCreatedOne", PlaylistName)
            : L("Import.StatusCreated", PlaylistName, count);
    }

    [RelayCommand]
    private void Close()
    {
        _analyzeCts?.Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
