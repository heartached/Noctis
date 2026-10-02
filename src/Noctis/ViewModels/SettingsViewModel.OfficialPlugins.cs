using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Localization;
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

    /// <summary>The live list loaded this session: opening the page again does not fetch it again.</summary>
    private bool _officialPluginsFetched;

    private void OnPluginsPageOpened()
    {
        if (OfficialPlugins.Count == 0) ShowOfficialPlugins(PluginCatalogClient?.Embedded() ?? PluginCatalog.LoadEmbedded());
        if (!_officialPluginsFetched) _ = LoadOfficialPluginsAsync();
    }

    [RelayCommand]
    private Task RefreshOfficialPlugins() => LoadOfficialPluginsAsync();

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
    {
        RefreshOfficialPluginStates();
        OnPropertyChanged(nameof(ShowPluginsOffNotice));
    }

    private void RefreshOfficialPluginStates()
    {
        if (Plugins is null) return;
        foreach (var item in OfficialPlugins)
            item.State = PluginCatalog.GetState(item.Entry, Plugins.FindById(item.Entry.Id)?.Version,
                Plugins.AppVersion, PluginManifest.CurrentPlatform);
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

    /// <summary>
    /// Download → verify → install. Nothing reaches the plugins folder unless the zip matches the
    /// list. A code plugin installed while community plugins are off offers to turn them on, with
    /// the switch's own confirmation; declining leaves it installed and off.
    /// </summary>
    internal async Task InstallOfficialPluginAsync(OfficialPluginItem item)
    {
        if (Plugins is null || PluginCatalogClient is not { } client || item.IsBusy
            || item.State is not (OfficialPluginState.Install or OfficialPluginState.Update)) return;

        using var cts = new CancellationTokenSource();
        item.BeginDownload(cts);
        string? zip = null;
        try
        {
            zip = await Task.Run(() => client.DownloadAsync(item.Entry,
                (done, total) => Dispatcher.UIThread.Post(() => { if (item.IsBusy) item.Progress = total > 0 ? (double)done / total : 0; }),
                cts.Token));
            item.EndDownload();
            var installed = await InstallPluginPackageAsync(zip, item.Entry);
            if (installed && Plugins.FindById(item.Entry.Id) is { IsCodePlugin: true } && !Plugins.CommunityPluginsEnabled)
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

/// <summary>One row of Get plugins.</summary>
public sealed partial class OfficialPluginItem : ObservableObject
{
    private CancellationTokenSource? _cts;

    public OfficialPluginItem(PluginCatalogEntry entry) => Entry = entry;

    public PluginCatalogEntry Entry { get; }
    public string Name => Entry.Name;
    public string Version => Entry.Version;
    public string Description => Entry.Description;
    public bool HasDescription => Description.Length > 0;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ButtonText), nameof(CanClick), nameof(IsPrimary))]
    private OfficialPluginState _state;

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ButtonText), nameof(CanClick), nameof(IsPrimary))]
    private bool _isBusy;

    /// <summary>0..1 while downloading.</summary>
    [ObservableProperty] private double _progress;

    /// <summary>Why the last try failed; the button then reads Try again.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasError), nameof(ButtonText))]
    private string _error = string.Empty;

    public bool HasError => Error.Length > 0;

    public string ButtonText => IsBusy ? Loc.T("Plugins.Get.Cancel") : State switch
    {
        OfficialPluginState.Install or OfficialPluginState.Update when HasError => Loc.T("Plugins.Get.Retry"),
        OfficialPluginState.Install => Loc.T("Plugins.Get.Install"),
        OfficialPluginState.Update => Loc.T("Plugins.Get.Update"),
        OfficialPluginState.Installed => Loc.T("Plugins.Get.Installed"),
        OfficialPluginState.NeedsNewerApp => Loc.T("Plugins.Get.RequiresApp", Entry.MinAppVersion ?? ""),
        _ => Loc.T("Plugins.Get.NotForThisOs"),
    };

    public bool CanClick => IsBusy || State is OfficialPluginState.Install or OfficialPluginState.Update;
    /// <summary>Install and Update are the accent button; everything else is a quiet one.</summary>
    public bool IsPrimary => !IsBusy && State is OfficialPluginState.Install or OfficialPluginState.Update;

    internal void BeginDownload(CancellationTokenSource cts)
    {
        _cts = cts;
        Error = string.Empty;
        Progress = 0;
        IsBusy = true;
    }

    internal void EndDownload()
    {
        _cts = null;
        IsBusy = false;
    }

    internal void Cancel() => _cts?.Cancel();
}
