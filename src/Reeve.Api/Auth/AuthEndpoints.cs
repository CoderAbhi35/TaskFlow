using System.Security.Claims;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Reeve.Api.ErrorHandling;
using Reeve.Api.RateLimiting;
using Reeve.Application.Abstractions;

namespace Reeve.Api.Auth;

public sealed record TokenRequest(string? Username, string? Password);

public sealed record TokenResponse(string AccessToken, string TokenType, int ExpiresIn, DateTimeOffset ExpiresAt, string Username, IReadOnlyList<string> Roles);

public sealed record MeResponse(string Username, IReadOnlyList<string> Roles);

public static class AuthEndpoints
{
    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app, AuthOptions options)
    {
        var auth = app.MapGroup("/api/v1/auth").WithTags("Auth");

        if (options.DevIssuer.Enabled)
        {
            auth.MapPost("/token", IssueToken)
                .AllowAnonymous()
                .WithName("IssueToken")
                .WithSummary("Development only: exchange a configured username and password for a JWT")
                .RequireRateLimit(RateLimitingOptions.AuthPolicy);
        }

        auth.MapGet("/me", Me).RequireAuthorization(Policies.Read).WithName("Me").WithSummary("The signed-in user and roles");
        return app;
    }

    private static async Task<Results<Ok<TokenResponse>, ProblemHttpResult>> IssueToken(
        TokenRequest request, TokenService tokens, IAuditLog audit, IUnitOfWork unitOfWork,
        TimeProvider time, CancellationToken cancellationToken)
    {
        var issued = tokens.TryIssue(request.Username, request.Password);
        var username = request.Username?.Trim() is { Length: > 0 } name ? name : "(blank)";

        // A successful sign-in is attributed to the user it authenticated. A failed one stays anonymous:
        // the typed username is untrusted, so it is only recorded as the entity that was targeted.
        if (issued is null)
            audit.Record(AuditActions.LoginFailed, AuditEntities.User, username);
        else
            audit.Record(AuditActions.TokenIssued, AuditEntities.User, issued.Username, actor: issued.Username);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        if (issued is null)
        {
            return TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Invalid credentials",
                detail: "The username or password is incorrect.",
                extensions: new Dictionary<string, object?> { ["code"] = ErrorCodes.InvalidCredentials });
        }

        var expiresIn = (int)(issued.ExpiresAt - time.GetUtcNow()).TotalSeconds;
        return TypedResults.Ok(new TokenResponse(issued.AccessToken, "Bearer", expiresIn, issued.ExpiresAt, issued.Username, issued.Roles));
    }

    private static Ok<MeResponse> Me(ClaimsPrincipal user, IOptions<AuthOptions> options) =>
        TypedResults.Ok(new MeResponse(
            user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value ?? user.Identity?.Name ?? "unknown",
            user.FindAll(options.Value.RoleClaim).Select(c => c.Value).ToList()));
}
