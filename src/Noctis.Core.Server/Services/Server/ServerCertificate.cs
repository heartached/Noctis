using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Noctis.Services.Server;

/// <summary>
/// The server's TLS certificate: self-signed, generated once per install and kept as a PFX
/// beside its random password. On Windows the password file holds the password DPAPI-protected
/// to the user (as the scrobbler tokens in settings.json are), so a copy of the two files —
/// another account on the PC, a backup, a synced profile — is not this server's identity; a
/// password file an earlier build left in plain text is read as before and protected in place,
/// the certificate unchanged. On Unix the password stays plain text, owner-only. Clients pair
/// by pinning the SHA-256 fingerprint the app shows next to the QR code, which is what makes a
/// self-signed cert safe here: the phone trusts THIS certificate, not a CA.
/// </summary>
public static class ServerCertificate
{
    public const string FileName = "server.pfx";
    public const string KeyFileName = "server.pfx.key";
    private const int ValidYears = 10;

    /// <summary>macOS has no ephemeral key set: keys there land in a temporary keychain instead, so the flag is dropped on that platform.
    /// Windows' Schannel cannot use an ephemeral key as a server credential (every TLS handshake fails), so the key goes to the user key store there; it is removed again when the certificate is disposed.</summary>
    private static X509KeyStorageFlags KeyFlags => OperatingSystem.IsMacOS()
        ? X509KeyStorageFlags.Exportable
        : OperatingSystem.IsWindows()
            ? X509KeyStorageFlags.Exportable | X509KeyStorageFlags.UserKeySet
            : X509KeyStorageFlags.Exportable | X509KeyStorageFlags.EphemeralKeySet;

    /// <summary>Loads the stored certificate or creates a new one. Regenerates if the stored one is unreadable or within 30 days of expiry.</summary>
    public static X509Certificate2 LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var pfx = Path.Combine(directory, FileName);
        var keyFile = Path.Combine(directory, KeyFileName);
        // GitHub #230: a data folder shared by two computers holds the certificate of the one
        // that made it, its password protected for that computer only. Minting a new one over
        // it here made every switch of computers change the certificate, so each phone paired
        // with the other computer had to pair again. This computer keeps a pair of its own.
        if (IsOtherComputersKey(keyFile))
        {
            pfx = Path.Combine(directory, $"server-{SecretProtector.Current.ComputerId}.pfx");
            keyFile = pfx + ".key";
        }

        if (File.Exists(pfx) && File.Exists(keyFile))
        {
            try
            {
                // Plain text (an earlier build, or Unix) passes through; a DPAPI blob from
                // another user or machine unprotects to nothing and fails the load below.
                var stored = File.ReadAllText(keyFile).Trim();
                var current = PersistenceService.UnprotectSecret(stored);
                var cert = X509CertificateLoader.LoadPkcs12FromFile(pfx, current, KeyFlags);
                if (cert.NotAfter > DateTime.UtcNow.AddDays(30) && cert.HasPrivateKey)
                {
                    // Only a plain-text file is rewritten: DPAPI output differs on every call,
                    // so comparing a fresh blob with the stored one rewrote it on every start.
                    if (current == stored && PersistenceService.ProtectSecret(current) is var protectedPassword && protectedPassword != stored)
                        WriteKeyFile(keyFile, protectedPassword);
                    return cert;
                }
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
        WriteKeyFile(keyFile, PersistenceService.ProtectSecret(password));
        RestrictToOwner(pfx);
        return created;
    }

    /// <summary>The password file holds a copy protected by another computer or Windows user.</summary>
    private static bool IsOtherComputersKey(string keyFile)
    {
        try
        {
            if (!File.Exists(keyFile)) return false;
            var stored = File.ReadAllText(keyFile).Trim();
            return stored.StartsWith("enc:dpapi:", StringComparison.Ordinal) && PersistenceService.UnprotectSecret(stored).Length == 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Writes the password file in one step: a half-written one would mean a new certificate, and re-pairing every phone.</summary>
    private static void WriteKeyFile(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        RestrictToOwner(temp);
        File.Move(temp, path, overwrite: true);
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
        return X509CertificateLoader.LoadPkcs12(pfx, null, KeyFlags);
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
