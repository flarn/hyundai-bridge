namespace HyundaiBridge.Hyundai;

// Messages are fixed, sanitized descriptions. Never include upstream bodies or URLs.
internal sealed class HyundaiException(string message, int? statusCode = null, TimeSpan? retryAfter = null)
    : Exception(message)
{
    internal int? StatusCode { get; } = statusCode;
    internal TimeSpan? RetryAfter { get; } = retryAfter;
}
