using FluentValidation;
using FluentValidation.Results;
using Microsoft.AspNetCore.Http.HttpResults;
using Reeve.Api.Auth;
using Reeve.Api.RateLimiting;
using Reeve.Application.Abstractions;
using Reeve.Contracts.Queues;
using Reeve.Contracts.Stats;
using Reeve.Contracts.Workers;

namespace Reeve.Api.Endpoints;

public static class OperationsEndpoints
{
    public static IEndpointRouteBuilder MapOperationsEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/api/v1/workers", GetWorkers)
            .WithTags("Workers").WithName("ListWorkers").WithSummary("List workers and their heartbeat health")
            .RequireAuthorization(Policies.Read)
            .RequireRateLimit(RateLimitingOptions.DefaultPolicy);

        app.MapGet("/api/v1/queues", GetQueues)
            .WithTags("Queues").WithName("ListQueues").WithSummary("Backlog statistics per job type")
            .RequireAuthorization(Policies.Read)
            .RequireRateLimit(RateLimitingOptions.DefaultPolicy);

        app.MapGet("/api/v1/stats/overview", GetOverview)
            .WithTags("Stats").WithName("GetOverview")
            .WithSummary("Throughput, success rate, latency percentiles, backlog and workers for the dashboard")
            .RequireAuthorization(Policies.Read)
            .RequireRateLimit(RateLimitingOptions.DefaultPolicy);

        return app;
    }

    public const int MinOverviewMinutes = 5;
    public const int MaxOverviewMinutes = 24 * 60;

    private static async Task<Ok<OverviewResponse>> GetOverview(
        IJobQueries queries, CancellationToken cancellationToken, int windowMinutes = 60)
    {
        if (windowMinutes is < MinOverviewMinutes or > MaxOverviewMinutes)
        {
            throw new ValidationException([new ValidationFailure("windowMinutes",
                $"windowMinutes must be between {MinOverviewMinutes} and {MaxOverviewMinutes}.")]);
        }

        return TypedResults.Ok(await queries.GetOverviewAsync(TimeSpan.FromMinutes(windowMinutes), cancellationToken));
    }

    private static async Task<Ok<IReadOnlyList<WorkerResponse>>> GetWorkers(
        IJobQueries queries, CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.GetWorkersAsync(cancellationToken));

    private static async Task<Ok<IReadOnlyList<QueueStatsResponse>>> GetQueues(
        IJobQueries queries, CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.GetQueueStatsAsync(cancellationToken));
}
