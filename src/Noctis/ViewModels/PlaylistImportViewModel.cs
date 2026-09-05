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

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusMessage = "Choose an export file or paste a Deezer or TIDAL link.";
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

    public ObservableCollection<string> MissingTracks { get; } = new();

    public event EventHandler? Closed;

    public PlaylistImportViewModel(IPlaylistImportService service, ITidalAuthService tidal)
    {
        _service = service;
        _tidal = tidal;
    }

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
        StatusMessage = "Finish signing in to TIDAL in your browser…";
        bool ok;
        try { ok = await _tidal.LoginAsync(); }
        finally { IsBusy = false; }

        if (!ok)
        {
            StatusMessage = "TIDAL sign-in didn't complete. Try again.";
            return;
        }
        ShowLinkHelp(null, null, null);
        if (CanImportLink) await ImportLink();
        else StatusMessage = "Signed in to TIDAL. Paste a TIDAL playlist link to import it.";
    }

    private void OfferTidalSignIn()
    {
        StatusMessage = "Sign in to TIDAL to import this link.";
        ShowLinkHelp("TIDAL playlists are read with your own TIDAL account. Sign in once in your browser; Noctis only asks to read playlists.",
            "Sign in to TIDAL", null, tidalSignIn: true);
    }
    partial void OnIsBusyChanged(bool value) => OnPropertyChanged(nameof(CanImportLink));

    /// <summary>Called by the dialog after the user picks a file.</summary>
    public Task LoadFileAsync(string path)
        => AnalyzeAsync("Reading and matching…", "No tracks found in that file.", "Could not read file",
            ct => _service.AnalyzeAsync(path, ct));

    [RelayCommand]
    private Task ImportLink()
    {
        if (!CanImportLink) return Task.CompletedTask;
        var service = TidalPlaylistLink.TryParse(LinkText, out _, out _) ? "TIDAL" : "Deezer";
        return AnalyzeAsync($"Fetching from {service} and matching…", $"That {service} link has no tracks (private, or removed).",
            "Could not fetch link", ct => _service.AnalyzeLinkAsync(LinkText, ct));
    }

    private async Task AnalyzeAsync(string busyMessage, string emptyMessage, string errorPrefix,
        Func<CancellationToken, Task<PlaylistImportPreview>> analyze)
    {
        if (IsBusy) return;
        IsBusy = true;
        StatusMessage = busyMessage;
        MissingTracks.Clear();
        HasPreview = false;
        CanCreate = false;

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
            HasPreview = preview.TotalEntries > 0;
            CanCreate = MatchedCount > 0;
            StatusMessage = HasPreview
                ? $"{MatchedCount} matched · {MissingCount} missing of {preview.TotalEntries}"
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
            StatusMessage = $"{errorPrefix}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task Create()
    {
        if (IsBusy || _preview is null || _preview.MatchedTrackIds.Count == 0) return;
        IsBusy = true;
        StatusMessage = "Creating playlist…";

        await _service.CreateAsync(PlaylistName, _preview.MatchedTrackIds);

        // Reflect the new playlist in the sidebar immediately.
        var main = App.Services?.GetService<MainWindowViewModel>();
        if (main is not null) await main.Sidebar.LoadPlaylistsAsync();

        CanCreate = false;
        IsBusy = false;
        StatusMessage = $"Created \"{PlaylistName}\" with {_preview.MatchedTrackIds.Count} track"
            + (_preview.MatchedTrackIds.Count == 1 ? "." : "s.");
    }

    [RelayCommand]
    private void Close()
    {
        _analyzeCts?.Cancel();
        Closed?.Invoke(this, EventArgs.Empty);
    }
}
