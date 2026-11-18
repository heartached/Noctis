using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Noctis.Mobile.Services.Account;
using Noctis.Services;
using Noctis.Services.Server;
using Noctis.Services.Sync;

namespace Noctis.ViewModels;

/// <summary>
/// Settings › Account &amp; Devices › Other computers: sync with another computer
/// (<see cref="ComputerLinkService"/>). Signed out it is three boxes and Connect; Connect shows
/// the other computer's security code to compare, then Sign In; signed in it is one line (with
/// whom, when) and Sync Now / Sign Out. Nothing runs until someone signs in.
/// </summary>
public partial class SettingsViewModel
{
    private ComputerLinkService? _computerLink;
    private bool _computerLinkResolved;

    /// <summary>Tests hand in a link; the app resolves it from <see cref="App.Services"/>.</summary>
    internal ComputerLinkService? ComputerLink
    {
        get
        {
            if (!_computerLinkResolved)
            {
                _computerLinkResolved = true;
                _computerLink ??= App.Services?.GetService<ComputerLinkService>();
                if (_computerLink is not null)
                    _computerLink.StateChanged += (_, _) => Dispatcher.UIThread.Post(RefreshComputerLinkState);
            }
            return _computerLink;
        }
        set
        {
            _computerLink = value;
            _computerLinkResolved = false;
        }
    }

    [ObservableProperty] private string _computerLinkAddress = string.Empty;
    [ObservableProperty] private string _computerLinkUser = string.Empty;
    [ObservableProperty] private string _computerLinkPassword = string.Empty;
    /// <summary>The other computer's security code after Connect, grouped as its Settings shows it.</summary>
    [ObservableProperty] private string _computerLinkCode = string.Empty;
    [ObservableProperty] private bool _isComputerLinkConfirming;
    [ObservableProperty] private bool _isComputerLinked;
    [ObservableProperty] private bool _isComputerLinkBusy;
    [ObservableProperty] private string _computerLinkStatus = string.Empty;
    [ObservableProperty] private string _computerLinkError = string.Empty;

    /// <summary>The address and code Connect found, what Sign In uses.</summary>
    private (string Url, string Fingerprint)? _computerLinkProbe;

    public bool IsComputerLinkFormVisible => !IsComputerLinked && !IsComputerLinkConfirming;

    /// <summary>Where to start on the other computer, naming its switches and buttons exactly as
    /// this language shows them (filled from their own strings).</summary>
    public string ComputerLinkHelp => Localization.Loc.T("PcSync.Help",
        Localization.Loc.T("Phone.Title"), Localization.Loc.T("Sync.Title"),
        Localization.Loc.T("AccountSync.PairDevice"), Localization.Loc.T("Pair.ModeManual"));
    partial void OnIsComputerLinkedChanged(bool value) => OnPropertyChanged(nameof(IsComputerLinkFormVisible));
    partial void OnIsComputerLinkConfirmingChanged(bool value) => OnPropertyChanged(nameof(IsComputerLinkFormVisible));

    /// <summary>At settings load: the link's state, and its automatic sync when signed in.</summary>
    private void InitComputerLink()
    {
        if (!AccountFeatures.Enabled || ComputerLink is not { } link) return;
        link.StartAutoSync();
        RefreshComputerLinkState();
    }

    private void RefreshComputerLinkState()
    {
        var link = ComputerLink;
        var account = link?.Account;
        IsComputerLinked = account is not null;
        if (link is null || account is null)
        {
            ComputerLinkStatus = string.Empty;
            return;
        }
        var name = string.IsNullOrWhiteSpace(account.ServerName) ? account.ServerUrl : account.ServerName;
        var when = link.IsSyncing ? Localization.Loc.T("PcSync.Syncing")
            : account.LastSyncUtc is { } at ? Localization.Loc.T("Phone.LastSynced", Ago(at, DateTime.UtcNow))
            : Localization.Loc.T("Phone.NotSyncedYet");
        ComputerLinkStatus = Localization.Loc.T("PcSync.SyncedWith", name) + " · " + when;
        // The last sync's outcome: a failure stays on the card until a sync works again.
        if (!link.IsSyncing) ComputerLinkError = link.LastError is { } error ? ComputerLinkErrorText(error) : string.Empty;
    }

    [RelayCommand]
    private async Task ConnectComputerLink()
    {
        if (ComputerLink is not { } link || IsComputerLinkBusy) return;
        ComputerLinkError = string.Empty;
        IsComputerLinkBusy = true;
        try
        {
            var probe = await link.ProbeAsync(ComputerLinkAddress);
            _computerLinkProbe = probe;
            // Grouped as the other computer shows it (Connect a phone › Enter manually).
            ComputerLinkCode = SecurityCode.Format(probe.Fingerprint);
            IsComputerLinkConfirming = true;
        }
        catch (Exception ex) { ComputerLinkError = ComputerLinkErrorText(ex); }
        finally { IsComputerLinkBusy = false; }
    }

    [RelayCommand]
    private async Task ConfirmComputerLink()
    {
        if (ComputerLink is not { } link || IsComputerLinkBusy || _computerLinkProbe is not { } probe) return;
        ComputerLinkError = string.Empty;
        IsComputerLinkBusy = true;
        try
        {
            await link.SignInAsync(probe.Url, ComputerLinkUser, ComputerLinkPassword, probe.Fingerprint);
            ComputerLinkPassword = string.Empty;
            IsComputerLinkConfirming = false;
            _computerLinkProbe = null;
            RefreshComputerLinkState();
            await link.SyncNowAsync();
        }
        catch (Exception ex) { ComputerLinkError = ComputerLinkErrorText(ex); }
        finally
        {
            IsComputerLinkBusy = false;
            RefreshComputerLinkState();
        }
    }

    [RelayCommand]
    private void CancelComputerLink()
    {
        IsComputerLinkConfirming = false;
        _computerLinkProbe = null;
        ComputerLinkCode = string.Empty;
        ComputerLinkError = string.Empty;
    }

    [RelayCommand]
    private async Task SyncComputerLinkNow()
    {
        if (ComputerLink is not { IsLinked: true } link || IsComputerLinkBusy) return;
        ComputerLinkError = string.Empty;
        IsComputerLinkBusy = true;
        try { await link.SyncNowAsync(); }
        catch (Exception ex) { ComputerLinkError = ComputerLinkErrorText(ex); }
        finally
        {
            IsComputerLinkBusy = false;
            RefreshComputerLinkState();
        }
    }

    [RelayCommand]
    private async Task SignOutComputerLink()
    {
        if (ComputerLink is not { } link || IsComputerLinkBusy) return;
        IsComputerLinkBusy = true;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await link.SignOutAsync(timeout.Token);
            ComputerLinkError = string.Empty;
        }
        catch (Exception ex) { ComputerLinkError = ComputerLinkErrorText(ex); }
        finally
        {
            IsComputerLinkBusy = false;
            RefreshComputerLinkState();
        }
    }

    internal static string ComputerLinkErrorText(Exception ex) => ex switch
    {
        NoctisServerException { Kind: NoctisErrorKind.Unreachable or NoctisErrorKind.UnknownHost or NoctisErrorKind.NotNoctis }
            => Localization.Loc.T("PcSync.ErrorUnreachable", Localization.Loc.T("Phone.Title")),
        NoctisServerException { Kind: NoctisErrorKind.SyncDisabled } => Localization.Loc.T("PcSync.ErrorSyncOff", Localization.Loc.T("Sync.Title")),
        NoctisServerException { Kind: NoctisErrorKind.BadCredentials } => Localization.Loc.T("PcSync.ErrorSignIn"),
        NoctisServerException { Kind: NoctisErrorKind.SignedOut } => Localization.Loc.T("PcSync.SignedOutThere"),
        _ => Localization.Loc.T("PcSync.ErrorOther", ex.Message),
    };
}
