using Android.Gms.Tasks;
using Noctis.Mobile.Services;
using Noctis.Services;
using Xamarin.Google.MLKit.Vision.Barcode.Common;
using Xamarin.Google.MLKit.Vision.CodeScanner;

namespace Noctis.Android.Services;

/// <summary>
/// <see cref="IQrScanner"/> over Google's code scanner (play-services-code-scanner). Google Play
/// services opens its own camera screen and hands back only the decoded text, so Noctis asks
/// for no CAMERA permission and never receives an image. QR codes only; no manual entry. The
/// scanner module is installed by Play services (AndroidManifest asks for it at install time);
/// until it is there, or on a phone without Play services, the scan reports Unavailable and the
/// sign-in form is the way in. Never logs the scanned text: it carries a one-time secret.
/// </summary>
public sealed class AndroidQrScanner : IQrScanner
{
    public Task<QrScanResult> ScanAsync(CancellationToken ct = default)
    {
        var activity = MainActivity.Current;
        if (activity is null) return System.Threading.Tasks.Task.FromResult(new QrScanResult(QrScanOutcome.Unavailable));
        var done = new TaskCompletionSource<QrScanResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var options = new GmsBarcodeScannerOptions.Builder()
                .SetBarcodeFormats(Barcode.FormatQrCode)
                .EnableAutoZoom()
                .Build();
            var scanner = GmsBarcodeScanning.GetClient(activity, options);
            var listener = new Listener(done);
            scanner.StartScan()
                .AddOnSuccessListener(listener)
                .AddOnCanceledListener(listener)
                .AddOnFailureListener(listener);
        }
        catch (Exception ex)
        {
            DebugLog.Write("Account", $"QR scanner unavailable: {ex.GetType().Name}");
            done.TrySetResult(new QrScanResult(QrScanOutcome.Unavailable));
        }
        // The scanner screen cannot be closed from here; a cancelled wait just stops listening.
        ct.Register(() => done.TrySetResult(new QrScanResult(QrScanOutcome.Cancelled)));
        return done.Task;
    }

    private sealed class Listener(TaskCompletionSource<QrScanResult> done)
        : Java.Lang.Object, IOnSuccessListener, IOnCanceledListener, IOnFailureListener
    {
        public void OnSuccess(Java.Lang.Object? result) =>
            done.TrySetResult(result is Barcode { RawValue: { } text }
                ? new QrScanResult(QrScanOutcome.Scanned, text)
                : new QrScanResult(QrScanOutcome.Cancelled));

        public void OnCanceled() => done.TrySetResult(new QrScanResult(QrScanOutcome.Cancelled));

        public void OnFailure(Java.Lang.Exception e)
        {
            // The kind only (module not yet downloaded, no Play services): never a value.
            DebugLog.Write("Account", $"QR scan failed: {e.GetType().Name}");
            done.TrySetResult(new QrScanResult(QrScanOutcome.Unavailable));
        }
    }
}
