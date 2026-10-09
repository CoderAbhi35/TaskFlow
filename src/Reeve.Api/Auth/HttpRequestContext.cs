using Microsoft.IdentityModel.JsonWebTokens;
using Reeve.Api.Middleware;
using Reeve.Application.Abstractions;

namespace Reeve.Api.Auth;

/// <summary>The signed-in subject and correlation ID of the current HTTP request, for auditing.</summary>
internal sealed class HttpRequestContext(IHttpContextAccessor accessor) : IRequestContext
{
    public string Actor
    {
        get
        {
            var user = accessor.HttpContext?.User;
            return user?.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                ?? user?.Identity?.Name
                ?? "anonymous";
        }
    }

    public string? CorrelationId => accessor.HttpContext is { } http ? CorrelationIdMiddleware.Get(http) : null;
}
