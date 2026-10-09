using System.Text;

namespace Reeve.Api.Auth;

/// <summary>Configuration section <c>Auth</c>.</summary>
public sealed class AuthOptions
{
    public const string SectionName = "Auth";
    public const int MinSigningKeyBytes = 32;

    /// <summary>
    /// An external OpenID Connect authority (Entra ID, Auth0, Keycloak …). When set, tokens are
    /// validated against its published keys and <see cref="SigningKey"/> is not used.
    /// </summary>
    public string? Authority { get; set; }

    public string Issuer { get; set; } = "reeve";
    public string Audience { get; set; } = "reeve-api";

    /// <summary>HMAC-SHA256 key for self-issued tokens. Supply it through configuration or a secret store, never source control.</summary>
    public string? SigningKey { get; set; }

    /// <summary>Claim that carries roles. External providers differ ("roles", "groups", …).</summary>
    public string RoleClaim { get; set; } = "role";

    public int TokenLifetimeMinutes { get; set; } = 60;

    public DevIssuerOptions DevIssuer { get; set; } = new();

    public TimeSpan TokenLifetime => TimeSpan.FromMinutes(TokenLifetimeMinutes);

    /// <summary>Problems that must stop the API from starting.</summary>
    public IEnumerable<string> Validate(IHostEnvironment environment)
    {
        if (string.IsNullOrWhiteSpace(Authority))
        {
            if (string.IsNullOrWhiteSpace(SigningKey) || Encoding.UTF8.GetByteCount(SigningKey) < MinSigningKeyBytes)
                yield return $"Auth:SigningKey must be at least {MinSigningKeyBytes} bytes when no Auth:Authority is configured.";
        }

        if (DevIssuer.Enabled)
        {
            if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
                yield return "Auth:DevIssuer is for local development only and cannot be enabled in " +
                             $"the '{environment.EnvironmentName}' environment.";
            if (!string.IsNullOrWhiteSpace(Authority))
                yield return "Auth:DevIssuer cannot be combined with an external Auth:Authority.";
            if (DevIssuer.Users.Count == 0)
                yield return "Auth:DevIssuer is enabled but has no users.";
        }

        if (TokenLifetimeMinutes is < 1 or > 24 * 60)
            yield return "Auth:TokenLifetimeMinutes must be between 1 and 1440.";
    }
}

/// <summary>
/// A tiny token endpoint for local development and demos, so the project runs without an identity
/// provider. Refuses to start outside Development and Testing.
/// </summary>
public sealed class DevIssuerOptions
{
    public bool Enabled { get; set; }
    public List<DevUser> Users { get; set; } = [];
}

public sealed class DevUser
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public List<string> Roles { get; set; } = [];
}
