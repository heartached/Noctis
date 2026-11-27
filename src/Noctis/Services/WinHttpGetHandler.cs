using System.ComponentModel;
using System.Net;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Noctis.Services;

/// <summary>
/// HTTPS GETs through Windows' own WinHTTP instead of .NET's TLS stack, for hosts whose bot
/// screen turns .NET away. Genius (GitHub #265, live 2026-11-25): every genius.com request from
/// <see cref="HttpClient"/> — HTTP/1.1 or 2, and the WinRT client too — got Cloudflare's
/// "cf-mitigated: challenge" 403, while WinHTTP and curl got 200 for the same URL and headers.
/// GET only, no redirects followed (a 3xx comes back as is), the body capped at
/// <see cref="HttpSafety.MaxTextBytes"/>.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class WinHttpGetHandler : HttpMessageHandler
{
    private const uint AccessTypeAutomaticProxy = 4;
    private const uint FlagSecure = 0x00800000;
    private const uint OptionRedirectPolicy = 88;
    private const uint RedirectPolicyNever = 0;
    private const uint QueryStatusCode = 19;
    private const uint QueryFlagNumber = 0x20000000;
    private const int ErrorTimeout = 12002;
    private const int ErrorOperationCancelled = 12017;

    /// <summary>Resolve, connect, send and receive limits, in milliseconds.</summary>
    internal int TimeoutMs { get; init; } = 15_000;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Method != HttpMethod.Get || request.RequestUri is not { Scheme: "https" } uri)
            throw new NotSupportedException("Only HTTPS GET requests.");
        return Task.Run(() => Send(request, uri, cancellationToken), cancellationToken);
    }

    private HttpResponseMessage Send(HttpRequestMessage request, Uri uri, CancellationToken ct)
    {
        var userAgent = request.Headers.UserAgent.ToString();
        var session = WinHttpOpen(userAgent.Length > 0 ? userAgent : "Noctis", AccessTypeAutomaticProxy, null, null, 0);
        if (session == IntPtr.Zero) throw Failure("WinHttpOpen");
        IntPtr connect = IntPtr.Zero, handle = IntPtr.Zero;
        var closed = 0;
        void CloseRequest()
        {
            if (Interlocked.Exchange(ref closed, 1) == 0 && handle != IntPtr.Zero) WinHttpCloseHandle(handle);
        }
        try
        {
            WinHttpSetTimeouts(session, TimeoutMs, TimeoutMs, TimeoutMs, TimeoutMs);
            connect = WinHttpConnect(session, uri.IdnHost, (ushort)uri.Port, 0);
            if (connect == IntPtr.Zero) throw Failure("WinHttpConnect");
            handle = WinHttpOpenRequest(connect, "GET", uri.PathAndQuery, null, null, IntPtr.Zero, FlagSecure);
            if (handle == IntPtr.Zero) throw Failure("WinHttpOpenRequest");
            var never = RedirectPolicyNever;
            WinHttpSetOption(handle, OptionRedirectPolicy, ref never, sizeof(uint));

            var headers = new StringBuilder();
            foreach (var (name, values) in request.Headers)
                if (!name.Equals("User-Agent", StringComparison.OrdinalIgnoreCase))
                    headers.Append(name).Append(": ").Append(string.Join(", ", values)).Append("\r\n");

            // Closing the request handle makes a blocked WinHTTP call return at once.
            using var cancel = ct.Register(CloseRequest);
            if (!WinHttpSendRequest(handle, headers.Length > 0 ? headers.ToString() : null, headers.Length > 0 ? -1 : 0, IntPtr.Zero, 0, 0, IntPtr.Zero)
                || !WinHttpReceiveResponse(handle, IntPtr.Zero))
                throw Failure("WinHttpSendRequest", ct);

            uint status = 0, size = sizeof(uint), index = 0;
            if (!WinHttpQueryHeaders(handle, QueryStatusCode | QueryFlagNumber, null, ref status, ref size, ref index))
                throw Failure("WinHttpQueryHeaders", ct);

            using var body = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                if (!WinHttpReadData(handle, buffer, buffer.Length, out var read)) throw Failure("WinHttpReadData", ct);
                if (read == 0) break;
                if (body.Length + read > HttpSafety.MaxTextBytes)
                    throw new InvalidDataException($"Response exceeded {HttpSafety.MaxTextBytes} bytes.");
                body.Write(buffer, 0, read);
            }

            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new ByteArrayContent(body.ToArray()),
                RequestMessage = request,
            };
        }
        finally
        {
            CloseRequest();
            if (connect != IntPtr.Zero) WinHttpCloseHandle(connect);
            WinHttpCloseHandle(session);
        }
    }

    private static Exception Failure(string call, CancellationToken ct = default)
    {
        var error = Marshal.GetLastPInvokeError();
        if (ct.IsCancellationRequested) return new OperationCanceledException(ct);
        if (error == ErrorOperationCancelled) return new OperationCanceledException(ct);
        var inner = new Win32Exception(error);
        return error == ErrorTimeout
            ? new TaskCanceledException($"{call} timed out.", inner)
            : new HttpRequestException($"{call} failed: {inner.Message} ({error})", inner);
    }

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpen(string userAgent, uint accessType, string? proxyName, string? proxyBypass, uint flags);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpSetTimeouts(IntPtr handle, int resolveTimeout, int connectTimeout, int sendTimeout, int receiveTimeout);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpConnect(IntPtr session, string serverName, ushort serverPort, uint reserved);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr WinHttpOpenRequest(IntPtr connect, string verb, string objectName, string? version, string? referrer, IntPtr acceptTypes, uint flags);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpSetOption(IntPtr handle, uint option, ref uint buffer, int bufferLength);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WinHttpSendRequest(IntPtr request, string? headers, int headersLength, IntPtr optional, uint optionalLength, uint totalLength, IntPtr context);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpReceiveResponse(IntPtr request, IntPtr reserved);

    [DllImport("winhttp.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WinHttpQueryHeaders(IntPtr request, uint infoLevel, string? name, ref uint buffer, ref uint bufferLength, ref uint index);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpReadData(IntPtr request, byte[] buffer, int bytesToRead, out int bytesRead);

    [DllImport("winhttp.dll", SetLastError = true)]
    private static extern bool WinHttpCloseHandle(IntPtr handle);
}
