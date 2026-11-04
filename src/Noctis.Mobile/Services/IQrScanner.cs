namespace Noctis.Mobile.Services;

/// <summary>How a QR scan ended.</summary>
public enum QrScanOutcome
{
    /// <summary>A code was read; <see cref="QrScanResult.Text"/> holds it (untrusted).</summary>
    Scanned,
    /// <summary>The user backed out of the scanner.</summary>
    Cancelled,
    /// <summary>No scanner on this phone right now (no Google Play services, or its scanner
    /// module is still downloading).</summary>
    Unavailable,
}

public sealed record QrScanResult(QrScanOutcome Outcome, string? Text = null);

/// <summary>
/// Scans one QR code for Account → Scan QR Code. Android uses Google's code scanner, which runs
/// the camera inside Google Play services and returns only the decoded text: the app holds no
/// camera permission and never sees a frame. The text is untrusted input; the caller parses it
/// strictly (PairingPayload) and never logs it. Null on platforms without one (tests inject).
/// </summary>
public interface IQrScanner
{
    Task<QrScanResult> ScanAsync(CancellationToken ct = default);
}
