using System.Text.Json;
using FluentValidation;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Npgsql;
using Reeve.Application.Abstractions;
using Reeve.Application.Common;
using Reeve.Domain;
using Reeve.Domain.Jobs;

namespace Reeve.Api.ErrorHandling;

/// <summary>
/// Turns known exceptions into RFC 9457 problem responses with a stable machine-readable
/// <c>code</c>. Anything unrecognised falls through to the generic 500 handler, so internal details
/// never reach the client.
/// </summary>
internal sealed class ApiExceptionHandler(IProblemDetailsService problemDetails, ILogger<ApiExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext http, Exception exception, CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ValidationException ex => Validation(ex),
            BadHttpRequestException ex => Problem(ex.StatusCode, "Invalid request", ErrorCodes.InvalidRequest,
                "The request could not be read. Check that the body is valid JSON and matches the expected shape."),
            NotFoundException ex => Problem(StatusCodes.Status404NotFound, "Not found", ErrorCodes.NotFound, ex.Message),
            InvalidJobStateTransitionException ex => Problem(StatusCodes.Status409Conflict, "Invalid state transition",
                ErrorCodes.InvalidStateTransition, ex.Message),
            ConcurrencyConflictException => Problem(StatusCodes.Status409Conflict, "Concurrent modification",
                ErrorCodes.ConcurrencyConflict, "The job was changed by another request. Reload it and try again."),
            IdempotencyKeyMismatchException ex => Problem(StatusCodes.Status422UnprocessableEntity,
                "Idempotency key reused", ErrorCodes.IdempotencyKeyReused, ex.Message),
            DuplicateScheduleNameException ex => Problem(StatusCodes.Status409Conflict,
                "Schedule name taken", ErrorCodes.ScheduleNameTaken, ex.Message),
            DomainException ex => Problem(StatusCodes.Status400BadRequest, "Request violates a domain rule",
                ErrorCodes.DomainRuleViolated, ex.Message),
            _ when IsDatabaseUnavailable(exception) => Problem(StatusCodes.Status503ServiceUnavailable,
                "Service unavailable", ErrorCodes.Unavailable, "The database is temporarily unavailable. Retry shortly."),
            _ => null,
        };

        if (problem?.Status == StatusCodes.Status503ServiceUnavailable)
            http.Response.Headers.RetryAfter = RetryAfterSeconds;

        if (problem is null)
            return false;

        if (problem.Status >= 500)
            logger.LogError(exception, "Request failed with {StatusCode}", problem.Status);
        else
            logger.LogInformation("Request rejected with {StatusCode} {Code}: {Reason}",
                problem.Status, problem.Extensions["code"], exception.Message);

        http.Response.StatusCode = problem.Status!.Value;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = http,
            ProblemDetails = problem,
            Exception = exception,
        });
    }

    private const string RetryAfterSeconds = "5";

    /// <summary>SQLSTATEs for a server that is shutting down, starting up or out of connections.</summary>
    private static readonly HashSet<string> UnavailableStates = ["57P01", "57P02", "57P03", "53300"];

    /// <summary>
    /// The database can't be reached right now (restarting, failing over, out of connections). That
    /// is retryable, so it gets 503 and <c>Retry-After</c> rather than a 500 (measured: a PostgreSQL
    /// restart under load answered about a second of requests with 500). EF Core wraps the cause, so
    /// the whole chain is checked.
    /// </summary>
    internal static bool IsDatabaseUnavailable(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
        {
            if (e is PostgresException pg)
                return UnavailableStates.Contains(pg.SqlState) || pg.SqlState.StartsWith("08", StringComparison.Ordinal);
            if (e is NpgsqlException { IsTransient: true })
                return true;
        }
        return false;
    }

    private static ProblemDetails Validation(ValidationException ex)
    {
        var errors = ex.Errors
            .GroupBy(e => ToWireName(e.PropertyName))
            .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).Distinct().ToArray());

        return new HttpValidationProblemDetails(errors)
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Validation failed",
            Extensions = { ["code"] = ErrorCodes.ValidationFailed },
        };
    }

    private static ProblemDetails Problem(int status, string title, string code, string detail) => new()
    {
        Status = status,
        Title = title,
        Detail = detail,
        Extensions = { ["code"] = code },
    };

    /// <summary>"RetryPolicy.MaxRetries" becomes "retryPolicy.maxRetries"; header names are left alone.</summary>
    private static string ToWireName(string propertyName) =>
        string.Join('.', propertyName.Split('.').Select(segment =>
            segment.Contains('-') ? segment : JsonNamingPolicy.CamelCase.ConvertName(segment)));
}

public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string InvalidRequest = "invalid_request";
    public const string NotFound = "not_found";
    public const string InvalidStateTransition = "invalid_state_transition";
    public const string ConcurrencyConflict = "concurrency_conflict";
    public const string IdempotencyKeyReused = "idempotency_key_reused";
    public const string DomainRuleViolated = "domain_rule_violated";
    public const string ScheduleNameTaken = "schedule_name_taken";
    public const string RateLimited = "rate_limited";
    public const string InvalidCredentials = "invalid_credentials";
    public const string Unauthorized = "unauthorized";
    public const string Forbidden = "forbidden";
    public const string InternalError = "internal_error";
    public const string Unavailable = "unavailable";
}
