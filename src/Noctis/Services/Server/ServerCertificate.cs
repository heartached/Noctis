using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Noctis.Services.Server;

/// <summary>
/// The server's TLS certificate: self-signed, generated once per install and kept as a PFX
/// beside a random password file (owner-only on Unix). Clients pair by pinning the SHA-256
/// fingerprint the app shows next to the QR code, which is what makes a self-signed cert
/// safe here: the phone trusts THIS certificate, not a CA.
/// </summary>
public static class ServerCertificate
{
    public const string FileName = "server.pfx";
    public const string KeyFileName = "server.pfx.key";
    private const int ValidYears = 10;

    /// <summary>Loads the stored certificate or creates a new one. Regenerates if the stored one is unreadable or within 30 days of expiry.</summary>
    public static X509Certificate2 LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var pfx = Path.Combine(directory, FileName);
        var keyFile = Path.Combine(directory, KeyFileName);

        if (File.Exists(pfx) && File.Exists(keyFile))
        {
            try
            {
                var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, File.ReadAllText(keyFile).Trim(),
                    X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
                if (cert.NotAfter > DateTime.UtcNow.AddDays(30) && cert.HasPrivateKey) return cert;
                cert.Dispose();
            }
            catch (CryptographicException)
            {
                // Corrupt or password mismatch: fall through and mint a fresh one.
            }
        }

        var created = Create();
        var password = Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        File.WriteAllBytes(pfx, created.Export(X509ContentType.Pkcs12, password));
        File.WriteAllText(keyFile, password);
        RestrictToOwner(pfx);
        RestrictToOwner(keyFile);
        return created;
    }

    /// <summary>A fresh self-signed RSA-2048 certificate for "Noctis", valid <see cref="ValidYears"/> years, with server-auth EKU and localhost/machine-name SANs.</summary>
    public static X509Certificate2 Create()
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=Noctis", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new("1.3.6.1.5.5.7.3.1") }, false)); // serverAuth
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddDnsName("noctis");
        try { san.AddDnsName(Dns.GetHostName()); } catch { /* optional */ }
        san.AddIpAddress(IPAddress.Loopback);
        request.CertificateExtensions.Add(san.Build());

        var now = DateTimeOffset.UtcNow;
        var cert = request.CreateSelfSigned(now.AddDays(-1), now.AddYears(ValidYears));
        // Round-trip through PKCS#12 so the private key is attached in a form Kestrel accepts on every OS.
        var pfx = cert.Export(X509ContentType.Pkcs12);
        cert.Dispose();
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet);
    }

    /// <summary>"AB:CD:…" SHA-256 fingerprint the phone pins.</summary>
    public static string Fingerprint(X509Certificate2 cert)
        => string.Join(':', Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)).Chunk(2).Select(c => new string(c)));

    private static void RestrictToOwner(string path)
    {
        if (OperatingSystem.IsWindows()) return; // per-user data folder already; ACLs are inherited from %LOCALAPPDATA%
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best effort */ }
    }
}
