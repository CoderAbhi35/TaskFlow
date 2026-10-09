using System.Text.RegularExpressions;

namespace Reeve.Api.Middleware;

/// <summary>
/// Accepts a caller-supplied <c>X-Correlation-ID</c> (or generates one), echoes it on the response,
/// and adds it to the logging scope so every log line for the request can be found by it. Later
/// phases carry it onto job messages so a job can be followed from submission to execution.
/// </summary>
public sealed partial class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public const string HeaderName = "X-Correlation-ID";
    private const string ItemKey = "Reeve.CorrelationId";

    public static string? Get(HttpContext http) => http.Items[ItemKey] as string;

    public async Task InvokeAsync(HttpContext http)
    {
        var supplied = http.Request.Headers[HeaderName].ToString();
        // Only accept short, safe values: the ID ends up in logs and response headers.
        var correlationId = SafeId().IsMatch(supplied) ? supplied : Guid.NewGuid().ToString("N");

        http.Items[ItemKey] = correlationId;
        http.Response.OnStarting(() =>
        {
            http.Response.Headers[HeaderName] = correlationId;
            return Task.CompletedTask;
        });

        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = correlationId }))
        {
            await next(http);
        }
    }

    [GeneratedRegex("^[A-Za-z0-9._-]{1,64}$")]
    private static partial Regex SafeId();
}
