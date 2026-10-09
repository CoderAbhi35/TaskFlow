using IPNetwork = System.Net.IPNetwork;
using Microsoft.AspNetCore.HttpOverrides;

namespace Reeve.Api.Middleware;

/// <summary>Configuration section <c>ReverseProxy</c>.</summary>
public sealed class ReverseProxyOptions
{
    public const string SectionName = "ReverseProxy";

    /// <summary>
    /// CIDR ranges of proxies allowed to set <c>X-Forwarded-For</c> / <c>X-Forwarded-Proto</c>
    /// (loopback is always trusted). Empty means the API is reached directly.
    /// </summary>
    public string[] TrustedNetworks { get; set; } = [];
}

public static class ReverseProxy
{
    /// <summary>
    /// Behind a proxy every request comes from the proxy's address. Taking the client address from
    /// <c>X-Forwarded-For</c> keeps per-IP rate limits (the token endpoint) per client instead of one
    /// budget for everyone. Only the nearest hop is used, and only when it is a trusted proxy, so a
    /// client can't pick its own address by sending the header.
    /// </summary>
    public static IServiceCollection AddReeveReverseProxy(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection(ReverseProxyOptions.SectionName).Get<ReverseProxyOptions>() ?? new();

        // Parsed here so a malformed range stops startup instead of silently trusting nothing.
        var networks = options.TrustedNetworks
            .Select(n => IPNetwork.TryParse(n, out var network)
                ? network
                : throw new InvalidOperationException($"ReverseProxy:TrustedNetworks contains an invalid CIDR range: '{n}'."))
            .ToList();

        return services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.ForwardLimit = 1;
            foreach (var network in networks)
                o.KnownIPNetworks.Add(network);
        });
    }
}
