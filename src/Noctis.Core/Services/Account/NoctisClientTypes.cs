using System.Net.Http;
using System.Security.Cryptography.X509Certificates;

namespace Noctis.Mobile.Services.Account;

// The client side of a Noctis Server sign-in, shared by the phone app and a desktop that syncs
// with another computer (ComputerLinkService). Kept in the namespace the phone code has always
// used, so neither side needs new usings.

/// <summary>Whether the computer's Last.fm and ListenBrainz are signed in and on.</summary>
public sealed record ComputerScrobbling(bool LastFm, bool ListenBrainz);

/// <summary>
/// A play's scrobble verdict, sent with it to the computer (noctisScrobble=). The phone records a
/// play when it starts, before it knows whether it will count, so the verdict follows later.
/// </summary>
public enum PlayScrobble
{
    /// <summary>Passed the rule in Through Computer mode: the computer sends it on.</summary>
    Computer,
    /// <summary>Passed the rule in From This Phone mode: the phone sent it (or chose not to); never again.</summary>
    Phone,
    /// <summary>Did not pass the rule (skipped, too short): counted only.</summary>
    No,
}

/// <summary>The words noctisScrobble= carries.</summary>
public static class PlayScrobbleWire
{
    /// <summary>Held: the verdict is not in yet (never sent).</summary>
    public const string Pending = "pending";

    public static string Of(PlayScrobble verdict) => verdict switch
    {
        PlayScrobble.Computer => "computer",
        PlayScrobble.Phone => "phone",
        _ => "no",
    };
}

/// <summary>Why a server call failed, for the UI to word.</summary>
public enum NoctisErrorKind
{
    Unreachable,
    /// <summary>The certificate no longer matches the pinned fingerprint.</summary>
    CertificateChanged,
    BadCredentials,
    /// <summary>Too many failed sign-ins; retry later.</summary>
    LockedOut,
    /// <summary>Sync is turned off on the desktop (Subsonic error 50).</summary>
    SyncDisabled,
    /// <summary>The device key was revoked or is unknown.</summary>
    SignedOut,
    InvalidAddress,
    Server,
    /// <summary>The phone is too full to download more (a reserve is always left free).</summary>
    StorageFull,
    /// <summary>The desktop lists the song but its file is gone from the computer (download error 70).</summary>
    FileMissing,
    /// <summary>First contact: something answered at the address, but not a Noctis server over
    /// https (another web page, a plain-http port, a router login).</summary>
    NotNoctis,
    /// <summary>First contact: the address's name does not resolve ("studio-pc.lan" typed wrong).</summary>
    UnknownHost,
    /// <summary>The desktop answers but is too old to sign phones in (no noctisSignIn).</summary>
    Outdated,
    /// <summary>Downloads are set to Wi-Fi only and the phone is on mobile data.</summary>
    WifiRequired,
    /// <summary>QR sign-in: the code is not (or no longer) valid — expired, already used, or
    /// replaced by a newer one. The server does not say which.</summary>
    PairingExpired,
}

public sealed class NoctisServerException(NoctisErrorKind kind, string message, Exception? inner = null)
    : Exception(message, inner)
{
    public NoctisErrorKind Kind { get; } = kind;
}

/// <summary>
/// Builds the HTTP handler the account service talks through. The service passes a
/// certificate check (true = accept); the platform wires it into its TLS stack. Windows and
/// tests use a SocketsHttpHandler; Android an HttpClientHandler (AndroidMessageHandler).
/// </summary>
public delegate HttpMessageHandler NoctisHandlerFactory(Func<X509Certificate2, bool> acceptCertificate);
