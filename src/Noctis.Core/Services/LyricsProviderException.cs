namespace Noctis.Services;

/// <summary>
/// A lyrics provider failed to answer — network failure, timeout, server error,
/// or a malformed response. Distinct from a definitive miss, which providers
/// report as a null result / empty list. Callers use this to show "check your
/// internet connection" instead of the misleading "No Lyrics found".
/// </summary>
public class LyricsProviderException : Exception
{
    public string Provider { get; }

    public LyricsProviderException(string provider, Exception inner)
        : base($"{provider} lookup failed: {inner.Message}", inner)
    {
        Provider = provider;
    }

    /// <summary>The provider answered "busy" (503 or 429): it is up and reachable, just not
    /// answering now — not a connection problem.</summary>
    public bool IsBusy => InnerException is HttpRequestException
    {
        StatusCode: System.Net.HttpStatusCode.ServiceUnavailable or System.Net.HttpStatusCode.TooManyRequests,
    };
}
