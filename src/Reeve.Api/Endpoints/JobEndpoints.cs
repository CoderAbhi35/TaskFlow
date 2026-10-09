using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc;
using Reeve.Api.Auth;
using Reeve.Api.RateLimiting;
using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Application.Jobs;
using Reeve.Contracts.Common;
using Reeve.Contracts.Jobs;

namespace Reeve.Api.Endpoints;

public static class JobEndpoints
{
    public const string IdempotencyKeyHeader = "Idempotency-Key";
    public const string IdempotentReplayedHeader = "Idempotent-Replayed";

    /// <summary>Generous for a 64 KB payload plus envelope; rejects oversized bodies before they are buffered.</summary>
    private const long MaxCreateRequestBytes = 128 * 1024;

    public static IEndpointRouteBuilder MapJobEndpoints(this IEndpointRouteBuilder app)
    {
        var jobs = app.MapGroup("/api/v1/jobs").WithTags("Jobs")
            .RequireAuthorization(Policies.Read)
            .RequireRateLimit(RateLimitingOptions.DefaultPolicy);

        jobs.MapPost("/", CreateJob)
            .WithName("CreateJob")
            .WithSummary("Create a job")
            .WithDescription($"Send an `{IdempotencyKeyHeader}` header to make retries safe: repeating the same " +
                $"request returns the original job with `{IdempotentReplayedHeader}: true`.")
            .WithMetadata(new RequestSizeLimitAttribute(MaxCreateRequestBytes))
            .RequireAuthorization(Policies.Operate)
            .RequireRateLimit(RateLimitingOptions.SubmissionsPolicy);

        jobs.MapGet("/", SearchJobs).WithName("SearchJobs").WithSummary("Search and filter jobs");
        jobs.MapGet("/{id:guid}", GetJob).WithName("GetJob").WithSummary("Get job details");
        jobs.MapPost("/{id:guid}/cancel", CancelJob).WithName("CancelJob").WithSummary("Cancel a job that has not completed")
            .RequireAuthorization(Policies.Operate);
        jobs.MapPost("/{id:guid}/retry", RetryJob).WithName("RetryJob").WithSummary("Retry a failed or dead-lettered job")
            .RequireAuthorization(Policies.Operate);
        jobs.MapGet("/{id:guid}/attempts", GetAttempts).WithName("GetJobAttempts").WithSummary("Get execution history");

        return app;
    }

    private static async Task<Results<Created<JobResponse>, Ok<JobResponse>>> CreateJob(
        CreateJobRequest request,
        CreateJobHandler handler,
        HttpContext http,
        CancellationToken cancellationToken)
    {
        // Read the raw header: [FromHeader] binds an empty value as null, which would silently turn
        // a client bug (empty key) into a request without idempotency protection.
        var idempotencyKey = http.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values)
            ? values.ToString()
            : null;

        var result = await handler.HandleAsync(request, idempotencyKey, cancellationToken);
        if (!result.Replayed)
            return TypedResults.Created($"/api/v1/jobs/{result.Job.Id}", result.Job);

        http.Response.Headers[IdempotentReplayedHeader] = "true";
        return TypedResults.Ok(result.Job);
    }

    private static async Task<Ok<PagedResponse<JobSummaryResponse>>> SearchJobs(
        [AsParameters] SearchJobsRequest request, IJobQueries queries, CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.SearchJobsAsync(JobSearchCriteria.From(request), cancellationToken));

    private static async Task<Ok<JobResponse>> GetJob(Guid id, IJobQueries queries, CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.GetJobAsync(id, cancellationToken) ?? throw new NotFoundException("Job", id));

    private static async Task<Ok<JobResponse>> CancelJob(
        Guid id, JobLifecycleHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.CancelAsync(id, cancellationToken));

    private static async Task<Ok<JobResponse>> RetryJob(
        Guid id, JobLifecycleHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.RetryAsync(id, cancellationToken));

    private static async Task<Ok<IReadOnlyList<JobAttemptResponse>>> GetAttempts(
        Guid id, IJobQueries queries, CancellationToken cancellationToken) =>
        TypedResults.Ok(await queries.GetAttemptsAsync(id, cancellationToken) ?? throw new NotFoundException("Job", id));
}
