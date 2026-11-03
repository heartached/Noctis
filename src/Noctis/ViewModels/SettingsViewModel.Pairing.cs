using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Noctis.Helpers;
using Noctis.Services;
using Noctis.Services.Server;

namespace Noctis.ViewModels;

/// <summary>
/// Connect a phone's QR code: a one-time sign-in for the main account (see PairingCodes for the
/// threat model). Shown only while the pairing panel is open on the Account &amp; Devices tab and
/// the server runs; a new code replaces it every <see cref="PairingCodes.Lifetime"/> and after
/// each phone that signs in with it. Closing the panel, leaving the tab or Settings, or stopping
/// the server kills the live code at once. The code (its secret) is never logged or copied.
/// </summary>
public partial class SettingsViewModel
{
    /// <summary>The pairing QR (addresses, certificate pin, one-time secret); null when none is live.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPairingQr))] private Avalonia.Media.Imaging.Bitmap? _pairingQr;

    public bool HasPairingQr => PairingQr is not null;

    /// <summary>Connect a phone shows "Enter manually" (address, password, security code)
    /// instead of the QR. No code is live meanwhile: it is not on screen.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsQrPairing), nameof(ShowsQrUnavailable))] private bool _isManualPairing;

    public bool IsQrPairing => !IsManualPairing;

    /// <summary>QR mode, but no code can be shown (no account yet, no LAN address): say so.</summary>
    public bool ShowsQrUnavailable => !IsManualPairing && !HasPairingQr;

    /// <summary>The phone card's quieter corner: this PC's name, the API key for other apps.</summary>
    [ObservableProperty] private bool _isPhoneAdvancedOpen;

    [RelayCommand]
    private void ShowQrPairing() => IsManualPairing = false;

    [RelayCommand]
    private void ShowManualPairing() => IsManualPairing = true;

    [RelayCommand]
    private void TogglePhoneAdvanced() => IsPhoneAdvancedOpen = !IsPhoneAdvancedOpen;

    /// <summary>New code: the shown one stops working and a fresh one takes its place.</summary>
    [RelayCommand]
    private void RenewPairingQr() => RefreshPairingQr();

    partial void OnIsManualPairingChanged(bool value)
    {
        if (value) StopPairingQr();
        else RefreshPairingQr();
    }

    partial void OnPairingQrChanged(Avalonia.Media.Imaging.Bitmap? value) => OnPropertyChanged(nameof(ShowsQrUnavailable));

    /// <summary>"New code in 1:42" under the QR.</summary>
    [ObservableProperty] private string _pairingQrCountdown = string.Empty;

    private DateTime _pairingQrExpiresUtc;
    private DispatcherTimer? _pairingQrTimer;

    /// <summary>Clock for the countdown (tests).</summary>
    internal Func<DateTime> PairingClock { get; set; } = static () => DateTime.UtcNow;

    partial void OnIsPairingVisibleChanged(bool value)
    {
        if (value) RefreshPairingQr();
        else
        {
            StopPairingQr();
            // Next time it opens on the quick way again.
            IsManualPairing = false;
        }
    }

    /// <summary>
    /// Shows a fresh code when the panel is open and a phone could use it: the server is running,
    /// phone sign-in is on and there is an account to sign in to. Anything else: no code at all.
    /// </summary>
    internal void RefreshPairingQr()
    {
        if (!IsPairingVisible || IsManualPairing || _noctisServer is not { IsRunning: true } server || !AccountFeatures.Enabled
            || PrimaryAccount is not { } account || NoctisServerFingerprint.Length == 0)
        {
            StopPairingQr();
            return;
        }
        try
        {
            var primaryHost = Uri.TryCreate(NoctisServerUrl, UriKind.Absolute, out var url) ? url.Host : null;
            var hosts = LanAddresses.ForPairing(primaryHost);
            if (hosts.Count == 0)
            {
                // No private address a phone could reach: the typed address is all there is.
                StopPairingQr();
                return;
            }
            var secret = server.IssuePairingCode(account.Name, out var expiresUtc);
            var payload = PairingPayload.Build(hosts, server.Port, NoctisServerFingerprint, secret);
            SetPairingQr(QrCodeBitmap.TryRender(payload));
            _pairingQrExpiresUtc = expiresUtc;
            UpdatePairingCountdown(renew: false);
            _pairingQrTimer ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdatePairingCountdown(renew: true));
            _pairingQrTimer.Start();
        }
        catch (Exception ex)
        {
            // The message only: it never holds the code.
            DebugLogger.Error(DebugLogger.Category.Error, "Server", $"pairing code: {ex.GetType().Name}");
            StopPairingQr();
        }
    }

    /// <summary>Kills the live code and takes the QR off the screen.</summary>
    internal void StopPairingQr()
    {
        _pairingQrTimer?.Stop();
        _noctisServer?.RevokePairingCode();
        SetPairingQr(null);
        PairingQrCountdown = string.Empty;
    }

    /// <param name="renew">From the timer: a code about to expire is replaced. Not from
    /// <see cref="RefreshPairingQr"/> itself, which just issued one.</param>
    private void UpdatePairingCountdown(bool renew)
    {
        var left = _pairingQrExpiresUtc - PairingClock();
        // A second early, so the phone never scans a code that dies in flight.
        if (renew && left <= TimeSpan.FromSeconds(1))
        {
            RefreshPairingQr();
            return;
        }
        var seconds = Math.Max(0, (int)Math.Ceiling(left.TotalSeconds));
        PairingQrCountdown = Localization.Loc.T("AccountSync.PairQrCountdown", $"{seconds / 60}:{seconds % 60:00}");
    }

    /// <summary>A phone signed in with the code (used up): the panel shows the next one.</summary>
    private void OnPairingCodeUsed()
    {
        if (IsPairingVisible) RefreshPairingQr();
    }

    /// <summary>The main window left Settings: the pairing panel folds, which kills its code.</summary>
    public void OnSettingsLeft() => IsPairingVisible = false;

    private void SetPairingQr(Avalonia.Media.Imaging.Bitmap? qr)
    {
        var old = PairingQr;
        PairingQr = qr;
        old?.Dispose();
    }
}
