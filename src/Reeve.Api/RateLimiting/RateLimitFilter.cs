using System.Globalization;
using Reeve.Api.ErrorHandling;
using Reeve.Application.Telemetry;

namespace Reeve.Api.RateLimiting;

/// <summary>
/// Endpoint filter enforcing one rate limit policy. Adds the IETF draft <c>RateLimit-*</c> headers and,
/// when rejecting, <c>Retry-After</c> with a 429 problem response.
/// </summary>
internal sealed class RateLimitFilter(string policy) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var limiter = http.RequestServices.GetRequiredService<ApiRateLimiter>();

        // Signed-in callers get their own budget, so clients sharing a NAT or proxy do not starve each
        // other; anonymous requests (the token endpoint) fall back to the IP address, which behind a
        // reverse proxy requires forwarded headers to be configured.
        var subject = http.User.FindFirst("sub")?.Value;
        var client = subject is not null ? $"user:{subject}" : $"ip:{http.Connection.RemoteIpAddress?.ToString() ?? "unknown"}";
        var decision = await limiter.CheckAsync(policy, client, http.RequestAborted);

        http.Response.Headers["RateLimit-Policy"] = policy;
        http.Response.Headers["RateLimit-Limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        if (decision.Remaining >= 0)
            http.Response.Headers["RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (decision.Allowed)
            return await next(context);

        ReeveTelemetry.RateLimited.Add(1, new KeyValuePair<string, object?>(ReeveTelemetry.PolicyTag, policy));
        var retryAfterSeconds = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds));
        http.Response.Headers.RetryAfter = retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(
            statusCode: StatusCodes.Status429TooManyRequests,
            title: "Too many requests",
            detail: $"Rate limit '{policy}' exceeded. Retry after {retryAfterSeconds} s.",
            extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.RateLimited });
    }
}

public static class RateLimitEndpointExtensions
{
    public static TBuilder RequireRateLimit<TBuilder>(this TBuilder builder, string policy)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new RateLimitFilter(policy));
}
