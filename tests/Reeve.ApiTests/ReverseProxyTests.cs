using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Reeve.Api.Auth;
using Reeve.ApiTests.Infrastructure;

namespace Reeve.ApiTests;

/// <summary>
/// Behind nginx every request arrives from the proxy. The per-IP limit on the token endpoint must
/// follow <c>X-Forwarded-For</c> from a trusted proxy, and ignore it from anyone else.
/// </summary>
[Collection(ApiCollection.Name)]
public class ReverseProxyTests(ReeveApiFactory factory)
{
    private const string ProxyAddress = "10.89.0.5";

    private WebApplicationFactory<Program> Api(params string[] trustedNetworks) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Redis:KeyPrefix", $"test-{Guid.NewGuid():N}:");
            builder.UseSetting("RateLimiting:Policies:auth:PermitLimit", "2");
            for (var i = 0; i < trustedNetworks.Length; i++)
                builder.UseSetting($"ReverseProxy:TrustedNetworks:{i}", trustedNetworks[i]);
            // The in-memory test server has no socket, so give every request the proxy's address.
            builder.ConfigureServices(s => s.AddSingleton<IStartupFilter>(new FromAddress(IPAddress.Parse(ProxyAddress))));
        });

    private static async Task<HttpStatusCode> SignInAsync(HttpClient client, string forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/token")
        {
            Content = JsonContent.Create(new TokenRequest("admin", "wrong")),
        };
        request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await client.SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Clients_behind_a_trusted_proxy_get_their_own_budget()
    {
        await using var api = Api("10.89.0.0/24");
        var client = api.CreateClient();

        (await SignInAsync(client, "198.51.100.1")).Should().Be(HttpStatusCode.Unauthorized);
        (await SignInAsync(client, "198.51.100.1")).Should().Be(HttpStatusCode.Unauthorized);
        (await SignInAsync(client, "198.51.100.1")).Should().Be(HttpStatusCode.TooManyRequests);
        (await SignInAsync(client, "198.51.100.2")).Should().Be(HttpStatusCode.Unauthorized,
            "a different client behind the same proxy has its own budget");
    }

    [Fact]
    public async Task Forwarded_addresses_from_an_untrusted_caller_are_ignored()
    {
        await using var api = Api();
        var client = api.CreateClient();

        (await SignInAsync(client, "198.51.100.1")).Should().Be(HttpStatusCode.Unauthorized);
        (await SignInAsync(client, "198.51.100.2")).Should().Be(HttpStatusCode.Unauthorized);
        (await SignInAsync(client, "198.51.100.3")).Should().Be(HttpStatusCode.TooManyRequests,
            "a caller can't escape the limit by claiming a new address each time");
    }

    [Fact]
    public void An_invalid_trusted_network_stops_the_api_from_starting()
    {
        using var api = Api("10.89.0.0/99");

        var start = () => api.CreateClient();

        start.Should().Throw<InvalidOperationException>().WithMessage("*TrustedNetworks*10.89.0.0/99*");
    }

    private sealed class FromAddress(IPAddress address) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((HttpContext http, RequestDelegate nextMiddleware) =>
            {
                http.Connection.RemoteIpAddress = address;
                return nextMiddleware(http);
            });
            next(app);
        };
    }
}
