using System.Security.Cryptography;
using System.Text;

namespace Noctis.Services.Server;

/// <summary>
/// The one-time secret behind the desktop's "Connect a phone" QR code (noctisPairSignIn).
/// <para>
/// Threat model. The code is on screen, so assume it can be seen: someone looking over the
/// owner's shoulder, a screen share or call, a screenshot, a photo. What it grants is one phone
/// sign-in to the bound account, the same device key a password sign-in issues. So the secret:
/// is 256 bits from the OS CSPRNG (not guessable; the login brake on the endpoint is defence in
/// depth only); lives about two minutes (<see cref="Lifetime"/>); works once; is one of one —
/// issuing a new code, closing the panel or stopping the server kills the old one; is kept only
/// as its SHA-256 and compared in constant time; and is never logged. A code seen and used by
/// someone else within those two minutes still shows up as a new phone under "Your phones" with
/// its name, where Remove signs it out. The server only answers home-network addresses
/// (PrivateClientsOnly), so a photo of the code is useless away from the owner's network.
/// The QR also carries the certificate pin, and the phone checks it before sending the secret,
/// so a look-alike server on the same network never receives a live code.
/// A wrong or expired code fails the same way ("not valid"), so an attacker learns nothing
/// about which; and a wrong guess does not burn the live code (a stranger on the network
/// could otherwise keep the owner's code from ever working).
/// </para>
/// </summary>
public sealed class PairingCodes
{
    /// <summary>How long a code works after it is issued.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private const int SecretBytes = 32;

    private readonly object _gate = new();
    private readonly Func<DateTime> _now;
    private byte[]? _hash;
    private string? _user;
    private DateTime _expiresUtc;

    public PairingCodes() : this(null) { }

    /// <summary>Clock injection for tests; null uses UTC now.</summary>
    public PairingCodes(Func<DateTime>? now) => _now = now ?? (static () => DateTime.UtcNow);

    /// <summary>
    /// A new code for <paramref name="user"/>, replacing any live one (only one works at a time).
    /// The returned secret is the only copy: it goes into the QR code, never into a log.
    /// </summary>
    public string Issue(string user, out DateTime expiresUtc)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("A pairing code needs an account.", nameof(user));
        var secret = Base64Url(RandomNumberGenerator.GetBytes(SecretBytes));
        lock (_gate)
        {
            _hash = HashOf(secret);
            _user = user.Trim();
            _expiresUtc = expiresUtc = _now() + Lifetime;
        }
        return secret;
    }

    /// <summary>Kills the live code, if any (the panel closed, the server stopped).</summary>
    public void Revoke()
    {
        lock (_gate) Clear();
    }

    /// <summary>A code is live and not yet expired.</summary>
    public bool IsLive
    {
        get { lock (_gate) return _hash is not null && _now() < _expiresUtc; }
    }

    /// <summary>
    /// True (with the account it was issued for) when <paramref name="secret"/> is the live code;
    /// it is then used up. An expired code is dropped. A wrong one leaves the live code alone.
    /// </summary>
    public bool TryRedeem(string? secret, out string user)
    {
        user = string.Empty;
        if (secret is null || secret.Length != PairingPayload.SecretLength) return false;
        var candidate = HashOf(secret);
        lock (_gate)
        {
            if (_hash is null) return false;
            if (_now() >= _expiresUtc)
            {
                Clear();
                return false;
            }
            if (!CryptographicOperations.FixedTimeEquals(candidate, _hash)) return false;
            user = _user!;
            Clear();
            return true;
        }
    }

    private void Clear()
    {
        if (_hash is not null) CryptographicOperations.ZeroMemory(_hash);
        _hash = null;
        _user = null;
        _expiresUtc = DateTime.MinValue;
    }

    private static byte[] HashOf(string secret) => SHA256.HashData(Encoding.ASCII.GetBytes(secret));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
