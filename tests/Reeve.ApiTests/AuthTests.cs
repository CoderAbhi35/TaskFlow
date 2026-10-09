using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;
using Reeve.Contracts.Jobs;
using Reeve.Contracts.Schedules;
using Reeve.Contracts.Serialization;

namespace Reeve.ApiTests;

[Collection(ApiCollection.Name)]
public class AuthTests(ReeveApiFactory factory)
{
    private static async Task<ProblemResponse> ProblemAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<ProblemResponse>(ReeveJson.Options))!;

    private HttpClient WithToken(string token)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static string SignToken(string key, string issuer, DateTime expires, string role = Roles.Admin) =>
        new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = "reeve-api",
            Subject = new ClaimsIdentity([new Claim("sub", "mallory"), new Claim("role", role)]),
            NotBefore = expires.AddHours(-2),
            IssuedAt = expires.AddHours(-2),
            Expires = expires,
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256),
        });

    [Fact]
    public async Task Requests_without_a_token_are_rejected_but_health_stays_open()
    {
        var anonymous = factory.CreateClient();

        var read = await anonymous.GetAsync("/api/v1/jobs");
        var write = await anonymous.PostAsJsonAsync("/api/v1/jobs", new CreateJobRequest("SEND_NOTIFICATION"), ReeveJson.Options);

        read.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        write.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await ProblemAsync(read)).Code.Should().Be("unauthorized");
        read.Headers.WwwAuthenticate.ToString().Should().StartWith("Bearer");
        (await anonymous.GetAsync("/api/v1/health")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("not-a-jwt")]
    [InlineData("wrong-key")]
    [InlineData("wrong-issuer")]
    [InlineData("expired")]
    public async Task Invalid_tokens_are_rejected(string kind)
    {
        var token = kind switch
        {
            "wrong-key" => SignToken("some-other-key-that-is-long-enough-0123456789", ReeveApiFactory.TestIssuer, DateTime.UtcNow.AddHours(1)),
            "wrong-issuer" => SignToken(ReeveApiFactory.TestSigningKey, "evil-issuer", DateTime.UtcNow.AddHours(1)),
            "expired" => SignToken(ReeveApiFactory.TestSigningKey, ReeveApiFactory.TestIssuer, DateTime.UtcNow.AddMinutes(-5)),
            _ => "not-a-jwt",
        };

        var response = await WithToken(token).GetAsync("/api/v1/jobs");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Theory]
    // Viewers read everything but change nothing.
    [InlineData(Roles.Viewer, "GET", "/api/v1/jobs", HttpStatusCode.OK)]
    [InlineData(Roles.Viewer, "GET", "/api/v1/stats/overview", HttpStatusCode.OK)]
    [InlineData(Roles.Viewer, "POST", "/api/v1/jobs", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Viewer, "POST", "/api/v1/schedules", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Viewer, "GET", "/api/v1/audit", HttpStatusCode.Forbidden)]
    // Operators run the system but cannot delete schedules or read the audit log.
    [InlineData(Roles.Operator, "POST", "/api/v1/jobs", HttpStatusCode.Created)]
    [InlineData(Roles.Operator, "POST", "/api/v1/schedules", HttpStatusCode.Created)]
    [InlineData(Roles.Operator, "DELETE", "/api/v1/schedules/{schedule}", HttpStatusCode.Forbidden)]
    [InlineData(Roles.Operator, "GET", "/api/v1/audit", HttpStatusCode.Forbidden)]
    // Admins can do everything.
    [InlineData(Roles.Admin, "DELETE", "/api/v1/schedules/{schedule}", HttpStatusCode.NoContent)]
    [InlineData(Roles.Admin, "GET", "/api/v1/audit", HttpStatusCode.OK)]
    public async Task Roles_grant_exactly_their_permissions(string role, string method, string path, HttpStatusCode expected)
    {
        if (path.Contains("{schedule}"))
        {
            var created = await factory.CreateClientAs(Roles.Admin).PostAsJsonAsync("/api/v1/schedules",
                new CreateScheduleRequest($"Matrix {Guid.NewGuid():N}", "GENERATE_REPORT", "0 2 * * *"), ReeveJson.Options);
            var schedule = (await created.Content.ReadFromJsonAsync<ScheduleResponse>(ReeveJson.Options))!;
            path = path.Replace("{schedule}", schedule.Id.ToString());
        }

        var client = factory.CreateClientAs(role);
        var response = method switch
        {
            "GET" => await client.GetAsync(path),
            "DELETE" => await client.DeleteAsync(path),
            _ when path.EndsWith("/schedules") => await client.PostAsJsonAsync(path,
                new CreateScheduleRequest($"Matrix {Guid.NewGuid():N}", "GENERATE_REPORT", "0 2 * * *"), ReeveJson.Options),
            _ => await client.PostAsJsonAsync(path, new CreateJobRequest("SEND_NOTIFICATION"), ReeveJson.Options),
        };

        response.StatusCode.Should().Be(expected);
        if (expected == HttpStatusCode.Forbidden)
            (await ProblemAsync(response)).Code.Should().Be("forbidden");
    }

    [Fact]
    public async Task Viewers_cannot_cancel_or_retry_jobs()
    {
        var job = await factory.CreateJobAsync(factory.CreateClientAs(Roles.Operator), new CreateJobRequest("SEND_NOTIFICATION"));
        var viewer = factory.CreateClientAs(Roles.Viewer);

        (await viewer.PostAsync($"/api/v1/jobs/{job.Id}/cancel", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await viewer.PostAsync($"/api/v1/jobs/{job.Id}/retry", null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Dev_issuer_exchanges_credentials_for_a_working_token()
    {
        var anonymous = factory.CreateClient();

        var response = await anonymous.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest("operator", "operator-password"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await response.Content.ReadFromJsonAsync<TokenResponse>(ReeveJson.Options))!;
        token.TokenType.Should().Be("Bearer");
        token.Roles.Should().Equal(Roles.Operator);
        token.ExpiresIn.Should().BeGreaterThan(0);

        var me = await WithToken(token.AccessToken).GetFromJsonAsync<MeResponse>("/api/v1/auth/me", ReeveJson.Options);
        me.Should().BeEquivalentTo(new MeResponse("operator", [Roles.Operator]));
    }

    [Theory]
    [InlineData("operator", "wrong-password")]
    [InlineData("nobody", "operator-password")]
    [InlineData("", "")]
    public async Task Wrong_credentials_get_the_same_answer_whether_or_not_the_user_exists(string username, string password)
    {
        var response = await factory.CreateClient().PostAsJsonAsync("/api/v1/auth/token", new TokenRequest(username, password));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var problem = await ProblemAsync(response);
        problem.Code.Should().Be("invalid_credentials");
        problem.Detail.Should().Be("The username or password is incorrect.");
    }

    [Fact]
    public async Task Token_requests_are_rate_limited_per_client()
    {
        await using var api = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Redis:KeyPrefix"] = $"test-{Guid.NewGuid():N}:",
            ["RateLimiting:Policies:auth:PermitLimit"] = "2",
        });
        var client = api.CreateClient();

        for (var i = 0; i < 2; i++)
            (await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest("admin", "guess"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        var third = await client.PostAsJsonAsync("/api/v1/auth/token", new TokenRequest("admin", "admin-password"));

        third.StatusCode.Should().Be(HttpStatusCode.TooManyRequests, "even the right password waits once the limit is hit");
    }

    [Fact]
    public async Task Each_user_has_their_own_rate_limit_budget()
    {
        await using var api = factory.WithSettings(new Dictionary<string, string?>
        {
            ["Redis:KeyPrefix"] = $"test-{Guid.NewGuid():N}:",
            ["RateLimiting:Policies:submissions:PermitLimit"] = "2",
        });
        var alice = api.CreateClientAs(Roles.Operator, "alice");
        var bob = api.CreateClientAs(Roles.Operator, "bob");

        Task<HttpResponseMessage> Submit(HttpClient c) =>
            c.PostAsJsonAsync("/api/v1/jobs", new CreateJobRequest("SEND_NOTIFICATION"), ReeveJson.Options);

        (await Submit(alice)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Submit(alice)).StatusCode.Should().Be(HttpStatusCode.Created);
        (await Submit(alice)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await Submit(bob)).StatusCode.Should().Be(HttpStatusCode.Created, "bob shares alice's IP address but not her budget");
    }

    [Theory]
    [InlineData("Production", "true", "api-tests-signing-key-0123456789abcdef-0123456789", "DevIssuer")]
    [InlineData("Testing", "false", "too-short", "SigningKey")]
    public void Unsafe_configuration_stops_the_api_from_starting(string environment, string devIssuer, string key, string expected)
    {
        using var api = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment(environment);
            builder.UseSetting("Auth:DevIssuer:Enabled", devIssuer);
            builder.UseSetting("Auth:SigningKey", key);
        });

        var start = () => api.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage($"*{expected}*");
    }
}
