using System.Security.Cryptography.X509Certificates;
using Noctis.Mobile.Services.Account;

namespace Noctis.Android.Services;

/// <summary>
/// The account service's HTTP handler on Android (<see cref="NoctisHandlerFactory"/>).
/// HttpClientHandler is AndroidMessageHandler here (the SDK's default native handler), whose
/// validation callback receives the server's leaf certificate. The service's pin check alone
/// decides: the policy errors are ignored on purpose, both ways — the desktop's certificate is
/// self-signed (so errors are expected), and "no errors" from a public CA must never be enough.
/// </summary>
public static class AndroidNoctisHttp
{
    public static HttpMessageHandler CreateHandler(Func<X509Certificate2, bool> acceptCertificate) =>
        new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert != null && acceptCertificate(cert),
            // The desktop never redirects; following one would send the device key header on.
            AllowAutoRedirect = false,
        };
}
