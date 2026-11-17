using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Noctis.Mobile.Services.Account;

/// <summary>Stock <see cref="NoctisHandlerFactory"/> implementations.</summary>
public static class NoctisHandlers
{
    /// <summary>
    /// A SocketsHttpHandler whose TLS check is exactly the account service's pin check: the
    /// platform's own chain/name verdict is ignored (a self-signed desktop certificate always
    /// "fails" it). Used on Windows and in tests.
    /// </summary>
    public static HttpMessageHandler Sockets(Func<X509Certificate2, bool> acceptCertificate) => new SocketsHttpHandler
    {
        SslOptions = new SslClientAuthenticationOptions
        {
            RemoteCertificateValidationCallback = (_, cert, _, _) => cert switch
            {
                null => false,
                X509Certificate2 c2 => acceptCertificate(c2),
                _ => acceptCertificate(X509CertificateLoader.LoadCertificate(cert.GetRawCertData())),
            },
        },
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        // The desktop never redirects; following one would send the device key header on.
        AllowAutoRedirect = false,
    };
}
