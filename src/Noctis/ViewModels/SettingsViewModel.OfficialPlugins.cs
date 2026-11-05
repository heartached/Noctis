using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Localization;
using Noctis.Services.LyricsStudio;
using Noctis.Services.Plugins;

namespace Noctis.ViewModels;

/// <summary>
/// Settings → Plugins → Get plugins: the official list (plugins/index.json), one button per
/// plugin. The list is fetched when the Plugins page opens and on Refresh, never at startup;
/// the copy built into the app shows until it arrives and whenever it cannot be fetched.
/// Install downloads the zip, checks its size and SHA-256 against the list, then installs it
/// through the same path as Install from file (same approval and restricted-mode rules).
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>Reads the list and downloads zips. Set by MainWindowViewModel; null = no fetching (tests).</summary>
    internal PluginCatalogClient? PluginCatalogClient { get; set; }

    public ObservableCollection<OfficialPluginItem> OfficialPlugins { get; } = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasNoOfficialPlugins))]
    private bool _isOfficialPluginsLoading;

    public bool HasNoOfficialPlugins => !IsOfficialPluginsLoading && OfficialPlugins.Count == 0;

    /// <summary>Installed plugins that are not on the official list (Install from file, or a folder
    /// dropped in by hand): the page's Custom Category. Official ones are managed on their own row.</summary>
    public ObservableCollection<LoadedPlugin> CustomPlugins { get; } = new();

    public bool HasNoCustomPlugins => CustomPlugins.Count == 0;

    /// <summary>The live list loaded this session: opening the page again does not fetch it again.</summary>
    private bool _officialPluginsFetched;

    private void OnPluginsPageOpened()
    {
        // Coming back to Plugins shows the grid, not the page that was open when Settings moved on.
        SelectedPluginCard = null;
        if (OfficialPlugins.Count == 0) ShowOfficialPlugins(PluginCatalogClient?.Embedded() ?? PluginCatalog.LoadEmbedded());
        if (!_officialPluginsFetched) _ = LoadOfficialPluginsAsync();
    }

    /// <summary>GitHub's new-issue page for the plugin submission form
    /// (.github/ISSUE_TEMPLATE/3-plugin-submission.yml), with the title and the minimum Noctis
    /// version (this one) filled in: issue forms take a field's id as a query parameter.</summary>
    internal static string PluginSubmissionUrl(Version appVersion)
        => "https://github.com/heartached/Noctis/issues/new?template=3-plugin-submission.yml"
           + "&title=" + Uri.EscapeDataString("[Plugin] ")
           + "&min-app-version=" + Uri.EscapeDataString($"{appVersion.Major}.{appVersion.Minor}.{Math.Max(0, appVersion.Build)}");

    [RelayCommand]
    private void SubmitPlugin() => PlatformHelper.OpenUrl(PluginSubmissionUrl(Services.UpdateService.CurrentVersion));

    /// <summary>The beginner guide on the website (website/src/pages/plugins/make.astro):
    /// what you need, five steps from the sample to a submission, and what the review checks.</summary>
    internal const string PluginGuideUrl = "https://noctisapp.cc/plugins/make";

    [RelayCommand]
    private void OpenPluginGuide() => PlatformHelper.OpenUrl(PluginGuideUrl);

    [RelayCommand]
    private async Task RefreshOfficialPlugins()
    {
        await LoadOfficialPluginsAsync();
        // A refresh that changes nothing used to look like a dead button.
        TransientStatus.Show(nameof(PluginsStatus), v => PluginsStatus = v, Loc.T("Plugins.Get.Refreshed"));
    }

    /// <summary>Fetches the list (off the UI thread) and shows it; the built-in copy when that fails.</summary>
    internal async Task LoadOfficialPluginsAsync()
    {
        if (PluginCatalogClient is not { } client || IsOfficialPluginsLoading) return;
        IsOfficialPluginsLoading = true;
        try
        {
            // No note when the built-in copy stands in (offline, or the list isn't on GitHub
            // yet): it is a valid list, and a refresh or the next page open tries again.
            var load = await Task.Run(() => client.LoadAsync());
            _officialPluginsFetched = !load.IsFallback;
            ShowOfficialPlugins(load.Catalog);
        }
        finally { IsOfficialPluginsLoading = false; }
    }

    private void ShowOfficialPlugins(PluginCatalog catalog)
    {
        // A download in progress keeps its row; the new list shows on the next refresh.
        if (OfficialPlugins.Any(p => p.IsBusy)) return;
        OfficialPlugins.Clear();
        foreach (var entry in catalog.Plugins) OfficialPlugins.Add(new OfficialPluginItem(entry));
        RefreshOfficialPluginStates();
        OnPropertyChanged(nameof(HasNoOfficialPlugins));
    }

    private void OnInstalledPluginsChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => RefreshOfficialPluginStates();

    private void RefreshOfficialPluginStates()
    {
        if (Plugins is not null)
        {
            foreach (var item in OfficialPlugins)
            {
                var copy = Plugins.FindById(item.Entry.Id);
                // An official listing only takes a copy that was checked against it at install time
                // (PluginHost.IsVerifiedOfficial): a zip that merely reuses Mixxx's id and author
                // stays a community plugin and never borrows the verified mark.
                var verified = copy is not null && (!item.IsOfficial || Plugins.IsVerifiedOfficial(copy, item.Entry));
                item.Installed = verified ? copy : null;
                item.HasUnverifiedCopy = copy is not null && !verified;
                var state = PluginCatalog.GetState(item.Entry, copy?.Version,
                    Plugins.AppVersion, PluginManifest.CurrentPlatform);
                // Replace installs the listed zip: only when this Noctis can run it (an unverified
                // copy as new as the list skipped the OS and minAppVersion checks).
                if (state == OfficialPluginState.Installed && item.HasUnverifiedCopy)
                {
                    var listed = PluginCatalog.GetState(item.Entry, null, Plugins.AppVersion, PluginManifest.CurrentPlatform);
                    if (listed is OfficialPluginState.NeedsNewerApp or OfficialPluginState.NotForThisOs) state = listed;
                }
                item.State = state;
            }
        }
        SyncCustomPlugins();
        SyncPluginCards();
    }

    /// <summary>Brings <see cref="CustomPlugins"/> in line with the host's list, in its order. Moves
    /// and removals in place (no Clear), so an untouched row keeps its container and state.</summary>
    private void SyncCustomPlugins()
    {
        var wanted = Plugins is null
            ? new List<LoadedPlugin>()
            : Plugins.Plugins.Where(p => !OfficialPlugins.Any(o => ReferenceEquals(o.Installed, p))).ToList();

        for (var i = CustomPlugins.Count - 1; i >= 0; i--)
            if (!wanted.Contains(CustomPlugins[i])) CustomPlugins.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = CustomPlugins.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at < 0) CustomPlugins.Insert(i, wanted[i]);
            else CustomPlugins.Move(at, i);
        }
        OnPropertyChanged(nameof(HasNoCustomPlugins));
    }

    /// <summary>The row's button: Install / Update, or Cancel while it downloads (hence concurrent:
    /// the Cancel click runs while the install it cancels is still awaiting).</summary>
    [RelayCommand(AllowConcurrentExecutions = true)]
    private async Task OfficialPluginAction(OfficialPluginItem? item)
    {
        if (item is null) return;
        if (item.IsBusy) { item.Cancel(); return; }
        await InstallOfficialPluginAsync(item);
    }

    /// <summary>A plugin download updates its row at most this often (~4 a second).</summary>
    private static readonly TimeSpan ProgressPostInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Download → verify → install. Nothing reaches the plugins folder unless the zip matches the
    /// list. Official plugins don't need the Community switch; a code plugin that ends up a
    /// community one while that is off offers to turn it on (declining leaves it installed and off).
    /// </summary>
    internal async Task InstallOfficialPluginAsync(OfficialPluginItem item)
    {
        if (Plugins is null || PluginCatalogClient is not { } client || item.IsBusy || !item.CanInstall) return;

        using var cts = new CancellationTokenSource();
        item.BeginDownload(cts);
        // Speed and time left as Lyrics Studio shows them (DownloadMeter: MB/s, "about 30 s left").
        // The download reports every 64 KiB chunk; the row hears of it at most ~4 times a second.
        var meter = new DownloadMeter();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var lastPost = -ProgressPostInterval;
        string? zip = null;
        try
        {
            zip = await Task.Run(() => client.DownloadAsync(item.Entry,
                (done, total) =>
                {
                    var at = clock.Elapsed;
                    if (at - lastPost < ProgressPostInterval && !(total > 0 && done >= total)) return;
                    lastPost = at;
                    Dispatcher.UIThread.Post(() =>
                    {
                        if (!item.IsBusy) return;
                        meter.Sample(new ModelDownloadProgress(ModelDownloadPhase.Downloading, done, total), at);
                        var d = meter.Read(at);
                        item.Progress = d.Fraction;
                        item.ProgressDetail = DownloadMeter.SpeedText(d);
                    });
                },
                cts.Token));
            item.EndDownload();
            var installed = await InstallPluginPackageAsync(zip, item.Entry, replaceUnverified: item.HasUnverifiedCopy);
            // A verified official install runs without the Community switch (owner 10-29); only a
            // copy that failed the check (a community plugin) still needs it.
            if (installed && Plugins.FindById(item.Entry.Id) is { IsCodePlugin: true } p && !Plugins.CommunityPluginsEnabled
                && !Plugins.IsTrustedOfficial(p))
                await ConfirmTurnOnCommunityPluginsAsync();
        }
        catch (OperationCanceledException)
        {
            ShowPluginStatus(Loc.T("Plugins.Get.Cancelled"));
        }
        catch (PluginDownloadException ex)
        {
            item.Error = ex.Failure switch
            {
                PluginDownloadFailure.Mismatch => Loc.T("Plugins.Get.Mismatch"),
                PluginDownloadFailure.NotAllowed => Loc.T("Plugins.Get.NotAllowed"),
                _ => Loc.T("Plugins.Get.DownloadFailed"),
            };
        }
        finally
        {
            item.EndDownload();
            if (zip is not null)
            {
                try { File.Delete(zip); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* temp folder */ }
            }
            RefreshOfficialPluginStates();
        }
    }
}

/// <summary>One plugin on the list (plugins/index.json): what its card's Install / Update button
/// does. Listings credited to Noctis are the official ones; any other author on the list is a
/// community plugin (<see cref="IsOfficial"/>).</summary>
public sealed partial class OfficialPluginItem : ObservableObject
{
    private CancellationTokenSource? _cts;

    public OfficialPluginItem(PluginCatalogEntry entry) => Entry = entry;

    public PluginCatalogEntry Entry { get; }
    /// <summary>Made by the Noctis team (listed under author Noctis): the verified mark, the
    /// Official section. Not something an installed zip can claim for itself.</summary>
    public bool IsOfficial => Entry.IsOfficial;
    public string Name => Entry.Name;
    /// <summary>The installed copy's version once installed (a hand-installed newer build shows
    /// as itself, not as the list's), else the list's.</summary>
    public string Version => Installed?.Version ?? Entry.Version;
    public string Description => Entry.Description;
    public bool HasDescription => Description.Length > 0;
    /// <summary>"by &lt;author&gt;": the list's credit, which for a community plugin is its submitter's.</summary>
    public string AuthorLine => Loc.T("Plugins.Get.By", Entry.Author);
    public bool HasAuthor => Entry.Author.Length > 0;

    /// <summary>The installed copy, or null: its switch, settings and Remove show on this row.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsInstalled), nameof(ShowButton), nameof(Version))]
    private LoadedPlugin? _installed;

    public bool IsInstalled => Installed is not null;

    /// <summary>A plugin with this listing's id is installed but is not the copy checked against
    /// the official list (installed from a file, or before the check existed). The button then
    /// offers to replace it with the listed zip.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ButtonText), nameof(CanClick), nameof(IsPrimary), nameof(ShowButton), nameof(CanInstall))]
    private bool _hasUnverifiedCopy;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ButtonText), nameof(CanClick), nameof(IsPrimary), nameof(ShowButton), nameof(CanInstall))]
    private OfficialPluginState _state;

    /// <summary>The button would download and install: Install, Update, or Replace with the official copy.</summary>
    public bool CanInstall => State is OfficialPluginState.Install or OfficialPluginState.Update
                              || (State == OfficialPluginState.Installed && HasUnverifiedCopy);

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ButtonText), nameof(CanClick), nameof(IsPrimary), nameof(ShowButton))]
    private bool _isBusy;

    /// <summary>0..1 while downloading.</summary>
    [ObservableProperty] private double _progress;

    /// <summary>"4.2 MB/s · about 30 s left" while downloading, else empty.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasProgressDetail))]
    private string _progressDetail = string.Empty;

    public bool HasProgressDetail => ProgressDetail.Length > 0;

    /// <summary>Why the last try failed; the button then reads Try again.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(ButtonText))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    public string ButtonText => IsBusy ? Loc.T("Plugins.Get.Cancel") : State switch
    {
        _ when CanInstall && HasError => Loc.T("Plugins.Get.Retry"),
        OfficialPluginState.Install => Loc.T("Plugins.Get.Install"),
        OfficialPluginState.Update => Loc.T("Plugins.Get.Update"),
        OfficialPluginState.Installed when HasUnverifiedCopy => Loc.T("Plugins.Get.Replace"),
        OfficialPluginState.Installed => Loc.T("Plugins.Get.Installed"),
        OfficialPluginState.NeedsNewerApp => Loc.T("Plugins.Get.RequiresApp", Entry.MinAppVersion ?? ""),
        _ => Loc.T("Plugins.Get.NotForThisOs"),
    };

    public bool CanClick => IsBusy || CanInstall;
    /// <summary>Install, Update and Replace are the accent button; everything else is a quiet one.</summary>
    public bool IsPrimary => !IsBusy && CanInstall;
    /// <summary>A plain "Installed" label is dropped once the card has its own on/off switch.</summary>
    public bool ShowButton => IsBusy || !(IsInstalled && State == OfficialPluginState.Installed);

    internal void BeginDownload(CancellationTokenSource cts)
    {
        _cts = cts;
        Error = string.Empty;
        Progress = 0;
        ProgressDetail = string.Empty;
        IsBusy = true;
    }

    internal void EndDownload()
    {
        _cts = null;
        // The speed line is shown by HasProgressDetail alone: clear it, or the last speed
        // stays under the card after the install finishes or fails.
        ProgressDetail = string.Empty;
        IsBusy = false;
    }

    internal void Cancel() => _cts?.Cancel();
}
