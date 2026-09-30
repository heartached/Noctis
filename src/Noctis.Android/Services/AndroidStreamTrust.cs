using System.Security.Cryptography;
using Android.Runtime;
using Java.Security;
using Javax.Net.Ssl;
using Noctis.Services;
using JCertificate = Java.Security.Cert.Certificate;
using JX509Certificate = Java.Security.Cert.X509Certificate;

namespace Noctis.Android.Services;

/// <summary>
/// Lets ExoPlayer stream from the owner's desktop, whose self-signed certificate was pinned at
/// sign-in (the fingerprint the user compared). Media3's DefaultHttpDataSource opens plain
/// HttpsURLConnections and has no per-instance SSL settings, so this installs process-wide
/// defaults instead:
/// <list type="bullet">
/// <item>a trust manager that accepts a chain only when its leaf's SHA-256 is the pin, and hands
/// every other chain to the platform's own trust manager (never an empty check);</item>
/// <item>a hostname verifier bound to the desktop's host: for that host only the pinned leaf
/// passes (the desktop is reached by IP, which its certificate need not name — and a CA-issued
/// certificate for the same name must not, or a DDNS/DNS hijack would get the device key sent
/// with the stream); for any other host the pinned leaf is refused and the default verifier it
/// replaced decides — never true for everything.</item>
/// </list>
/// Nothing else in the app relies on HttpsURLConnection's defaults (the account service's
/// HttpClient carries its own check per handler), so the reach is ExoPlayer's streams.
/// Installed while signed in (at start and on sign-in), removed on sign-out.
/// </summary>
public static class AndroidStreamTrust
{
    private static readonly object Gate = new();
    private static SSLSocketFactory? _originalFactory;
    private static IHostnameVerifier? _originalVerifier;
    private static string? _installed;

    /// <summary>Trust the desktop at <paramref name="host"/> whose leaf certificate has this SHA-256 ("AB:CD:…").</summary>
    public static void Install(string fingerprint, string host)
    {
        var pin = ParsePin(fingerprint);
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("No desktop host", nameof(host));
        var key = Convert.ToHexString(pin) + "@" + host.ToLowerInvariant();
        lock (Gate)
        {
            if (_installed == key) return;
            // The originals once, before the first replacement: a second Install (a new pin)
            // must still restore the platform's, not the previous pin's.
            _originalFactory ??= HttpsURLConnection.DefaultSSLSocketFactory;
            _originalVerifier ??= HttpsURLConnection.DefaultHostnameVerifier;

            var context = SSLContext.GetInstance("TLS")!;
            context.Init(null, new ITrustManager[] { new PinnedTrustManager(pin, PlatformTrustManager()) }, null);
            HttpsURLConnection.DefaultSSLSocketFactory = context.SocketFactory;
            HttpsURLConnection.DefaultHostnameVerifier = new PinnedHostnameVerifier(pin, host, _originalVerifier);
            _installed = key;
            DebugLog.Write("Account", "Stream trust installed for the pinned desktop");
        }
    }

    /// <summary>Signed out: the platform's defaults again.</summary>
    public static void Clear()
    {
        lock (Gate)
        {
            if (_installed == null) return;
            if (_originalFactory != null) HttpsURLConnection.DefaultSSLSocketFactory = _originalFactory;
            if (_originalVerifier != null) HttpsURLConnection.DefaultHostnameVerifier = _originalVerifier;
            _installed = null;
            DebugLog.Write("Account", "Stream trust removed");
        }
    }

    /// <summary>"AB:CD:…" (any case, colons optional) as its 32 bytes; anything else throws.</summary>
    internal static byte[] ParsePin(string fingerprint)
    {
        var bytes = Convert.FromHexString(fingerprint.Replace(":", string.Empty).Trim());
        if (bytes.Length != 32) throw new ArgumentException("Not a SHA-256 fingerprint", nameof(fingerprint));
        return bytes;
    }

    /// <summary>Whether DER <paramref name="encoded"/> hashes to <paramref name="pin"/> (constant time).</summary>
    internal static bool Matches(byte[]? encoded, byte[] pin) =>
        encoded is { Length: > 0 } && CryptographicOperations.FixedTimeEquals(SHA256.HashData(encoded), pin);

    /// <summary>The platform's default X509 trust manager (system CAs, the app's network security config).</summary>
    private static IX509TrustManager PlatformTrustManager()
    {
        var factory = TrustManagerFactory.GetInstance(TrustManagerFactory.DefaultAlgorithm)!;
        factory.Init((KeyStore?)null);
        foreach (var manager in factory.GetTrustManagers() ?? Array.Empty<ITrustManager>())
        {
            try
            {
                return manager.JavaCast<IX509TrustManager>()!;
            }
            catch (InvalidCastException)
            {
                // Not an X509 manager; try the next.
            }
        }
        throw new InvalidOperationException("The platform has no X509 trust manager");
    }

    private sealed class PinnedTrustManager : Java.Lang.Object, IX509TrustManager
    {
        private readonly byte[] _pin;
        private readonly IX509TrustManager _platform;

        public PinnedTrustManager(byte[] pin, IX509TrustManager platform)
        {
            _pin = pin;
            _platform = platform;
        }

        public void CheckClientTrusted(JX509Certificate[]? chain, string? authType) => _platform.CheckClientTrusted(chain, authType);

        public void CheckServerTrusted(JX509Certificate[]? chain, string? authType)
        {
            if (chain is { Length: > 0 } && Matches(chain[0].GetEncoded(), _pin)) return;
            _platform.CheckServerTrusted(chain, authType);
        }

        public JX509Certificate[] GetAcceptedIssuers() => _platform.GetAcceptedIssuers() ?? Array.Empty<JX509Certificate>();
    }

    private sealed class PinnedHostnameVerifier : Java.Lang.Object, IHostnameVerifier
    {
        private readonly byte[] _pin;
        private readonly string _host;
        private readonly IHostnameVerifier? _fallback;

        public PinnedHostnameVerifier(byte[] pin, string host, IHostnameVerifier? fallback)
        {
            _pin = pin;
            _host = host;
            _fallback = fallback;
        }

        public bool Verify(string? hostname, ISSLSession? session)
        {
            var pinned = false;
            try
            {
                JCertificate[]? peer = session?.GetPeerCertificates();
                pinned = peer is { Length: > 0 } && Matches(peer[0].GetEncoded(), _pin);
            }
            catch (Exception)
            {
                // SSLPeerUnverifiedException and the like: not the pinned desktop.
            }
            // The desktop's host: its pinned certificate and nothing else.
            if (string.Equals(hostname, _host, StringComparison.OrdinalIgnoreCase)) return pinned;
            // Any other host never gets by on the desktop's certificate.
            if (pinned) return false;
            return _fallback?.Verify(hostname, session) ?? false;
        }
    }
}
