using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Reeve.Api.Auth;

public sealed record IssuedToken(string AccessToken, DateTimeOffset ExpiresAt, string Username, IReadOnlyList<string> Roles);

/// <summary>Checks development credentials and signs short-lived JWTs with the configured key.</summary>
public sealed class TokenService(IOptions<AuthOptions> options, TimeProvider time)
{
    private readonly AuthOptions _options = options.Value;

    public static SymmetricSecurityKey SigningKeyFrom(string key) => new(Encoding.UTF8.GetBytes(key));

    /// <returns>Null when the username or password is wrong. Both cases look the same to the caller.</returns>
    public IssuedToken? TryIssue(string? username, string? password)
    {
        var user = _options.DevIssuer.Users.FirstOrDefault(u =>
            string.Equals(u.Username, username, StringComparison.OrdinalIgnoreCase));

        // Compare in constant time, and compare against something even for unknown users, so response
        // timing does not reveal which usernames exist.
        var valid = FixedTimeEquals(password ?? "", user?.Password ?? Guid.NewGuid().ToString()) && user is not null;
        return valid ? Issue(user!.Username, user.Roles) : null;
    }

    public IssuedToken Issue(string username, IReadOnlyList<string> roles)
    {
        var now = time.GetUtcNow();
        var expires = now + _options.TokenLifetime;

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, username),
            new(JwtRegisteredClaimNames.Name, username),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N")),
        };
        claims.AddRange(roles.Select(r => new Claim(_options.RoleClaim, r)));

        var token = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = _options.Issuer,
            Audience = _options.Audience,
            Subject = new ClaimsIdentity(claims),
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime,
            Expires = expires.UtcDateTime,
            SigningCredentials = new SigningCredentials(SigningKeyFrom(_options.SigningKey!), SecurityAlgorithms.HmacSha256),
        });

        return new IssuedToken(token, expires, username, roles);
    }

    private static bool FixedTimeEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));
}
