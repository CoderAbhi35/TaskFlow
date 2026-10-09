using Microsoft.AspNetCore.Http.HttpResults;
using Reeve.Api.Auth;
using Reeve.Api.RateLimiting;
using Reeve.Contracts.Audit;
using Reeve.Contracts.Common;
using Reeve.Infrastructure.Persistence;

namespace Reeve.Api.Endpoints;

public static class AuditEndpoints
{
    public static IEndpointRouteBuilder MapAuditEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/audit", Search)
            .WithTags("Audit").WithName("SearchAudit")
            .WithSummary("Who did what: filter by actor, action or entity; newest first")
            .RequireAuthorization(Policies.Administer)
            .RequireRateLimit(RateLimitingOptions.DefaultPolicy);
        return app;
    }

    private static async Task<Ok<PagedResponse<AuditEventResponse>>> Search(
        [AsParameters] SearchAuditRequest request, IAuditQueries audit, CancellationToken cancellationToken) =>
        TypedResults.Ok(await audit.SearchAsync(request, cancellationToken));
}
