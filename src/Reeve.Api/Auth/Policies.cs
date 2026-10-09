using Microsoft.AspNetCore.Authorization;

namespace Reeve.Api.Auth;

/// <summary>
/// Roles are cumulative: an operator can do everything a viewer can, an admin everything an operator can.
/// </summary>
public static class Roles
{
    /// <summary>Read-only access to jobs, queues, workers, schedules and metrics.</summary>
    public const string Viewer = "viewer";

    /// <summary>Also submits, cancels and retries jobs, and creates, pauses and resumes schedules.</summary>
    public const string Operator = "operator";

    /// <summary>Also deletes schedules and reads the audit log.</summary>
    public const string Admin = "admin";

    public static readonly string[] All = [Viewer, Operator, Admin];
}

public static class Policies
{
    public const string Read = "read";
    public const string Operate = "operate";
    public const string Administer = "administer";
}

public static class AuthServiceCollectionExtensions
{
    public static AuthorizationBuilder AddReevePolicies(this AuthorizationBuilder builder) => builder
        .AddPolicy(Policies.Read, p => p.RequireAuthenticatedUser().RequireRole(Roles.Viewer, Roles.Operator, Roles.Admin))
        .AddPolicy(Policies.Operate, p => p.RequireAuthenticatedUser().RequireRole(Roles.Operator, Roles.Admin))
        .AddPolicy(Policies.Administer, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin))
        // Secure by default: an endpoint that forgets to declare a policy still requires a signed-in user.
        .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());
}
