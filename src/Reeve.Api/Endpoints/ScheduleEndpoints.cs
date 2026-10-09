using Microsoft.AspNetCore.Http.HttpResults;
using Reeve.Api.Auth;
using Reeve.Api.RateLimiting;
using Reeve.Application.Schedules;
using Reeve.Contracts.Schedules;

namespace Reeve.Api.Endpoints;

public static class ScheduleEndpoints
{
    public static IEndpointRouteBuilder MapScheduleEndpoints(this IEndpointRouteBuilder app)
    {
        var schedules = app.MapGroup("/api/v1/schedules").WithTags("Schedules")
            .RequireAuthorization(Policies.Read)
            .RequireRateLimit(RateLimitingOptions.DefaultPolicy);

        schedules.MapPost("/", Create).WithName("CreateSchedule").WithSummary("Create a recurring schedule")
            .RequireAuthorization(Policies.Operate)
            .RequireRateLimit(RateLimitingOptions.SubmissionsPolicy);
        schedules.MapGet("/", List).WithName("ListSchedules").WithSummary("List schedules");
        schedules.MapGet("/{id:guid}", Get).WithName("GetSchedule").WithSummary("Get a schedule");
        schedules.MapPost("/{id:guid}/pause", Pause).WithName("PauseSchedule")
            .WithSummary("Stop creating jobs from a schedule (idempotent)")
            .RequireAuthorization(Policies.Operate);
        schedules.MapPost("/{id:guid}/resume", Resume).WithName("ResumeSchedule")
            .WithSummary("Resume a paused schedule; runs missed while paused are skipped (idempotent)")
            .RequireAuthorization(Policies.Operate);
        schedules.MapDelete("/{id:guid}", Delete).WithName("DeleteSchedule")
            .WithSummary("Delete a schedule; jobs it already created are kept")
            .RequireAuthorization(Policies.Administer);

        return app;
    }

    private static async Task<Created<ScheduleResponse>> Create(
        CreateScheduleRequest request, ScheduleHandler handler, CancellationToken cancellationToken)
    {
        var schedule = await handler.CreateAsync(request, cancellationToken);
        return TypedResults.Created($"/api/v1/schedules/{schedule.Id}", schedule);
    }

    private static async Task<Ok<IReadOnlyList<ScheduleResponse>>> List(ScheduleHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.ListAsync(cancellationToken));

    private static async Task<Ok<ScheduleResponse>> Get(Guid id, ScheduleHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.GetAsync(id, cancellationToken));

    private static async Task<Ok<ScheduleResponse>> Pause(Guid id, ScheduleHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.PauseAsync(id, cancellationToken));

    private static async Task<Ok<ScheduleResponse>> Resume(Guid id, ScheduleHandler handler, CancellationToken cancellationToken) =>
        TypedResults.Ok(await handler.ResumeAsync(id, cancellationToken));

    private static async Task<NoContent> Delete(Guid id, ScheduleHandler handler, CancellationToken cancellationToken)
    {
        await handler.DeleteAsync(id, cancellationToken);
        return TypedResults.NoContent();
    }
}
