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

    /// <summary>How long the provider asked to be left alone (its Retry-After), when it said (GitHub #228).</summary>
    public TimeSpan? RetryAfter => (InnerException as BusyResponseException)?.RetryAfter;
}

/// <summary>
/// A provider answered "busy" (429 / 503, or its own rate-limit signal mapped to 429), with the
/// wait it asked for in Retry-After when it sent one (GitHub #228).
/// </summary>
public sealed class BusyResponseException : HttpRequestException
{
    public BusyResponseException(string message, System.Net.HttpStatusCode status, TimeSpan? retryAfter = null)
        : base(message, null, status)
    {
        RetryAfter = retryAfter;
    }

    public TimeSpan? RetryAfter { get; }
}
