using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Reeve.Api.Auth;
using Reeve.Api.Endpoints;
using Reeve.Api.ErrorHandling;
using Reeve.Api.Middleware;
using Reeve.Api.RateLimiting;
using Reeve.Application;
using Reeve.Application.Abstractions;
using Reeve.Application.Workers;
using Reeve.Contracts.Serialization;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Caching;
using Reeve.Infrastructure.Health;
using Reeve.Infrastructure.Observability;
using Reeve.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);

builder.AddReeveTelemetry("reeve-api",
    // Health probes arrive every few seconds; tracing them would bury the interesting requests.
    tracing: t => t.AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/api/v1/health")),
    metrics: m => m.AddAspNetCoreInstrumentation());

builder.Services.AddOpenApi();
builder.Services.ConfigureHttpJsonOptions(options => ReeveJson.Configure(options.SerializerOptions));

builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
{
    context.ProblemDetails.Extensions.TryAdd("code", context.ProblemDetails.Status switch
    {
        >= 500 => ErrorCodes.InternalError,
        StatusCodes.Status401Unauthorized => ErrorCodes.Unauthorized,
        StatusCodes.Status403Forbidden => ErrorCodes.Forbidden,
        StatusCodes.Status404NotFound => ErrorCodes.NotFound,
        _ => ErrorCodes.InvalidRequest,
    });
    if (CorrelationIdMiddleware.Get(context.HttpContext) is { } correlationId)
        context.ProblemDetails.Extensions["correlationId"] = correlationId;
});
builder.Services.AddExceptionHandler<ApiExceptionHandler>();

// ---- authentication and authorization ---------------------------------------------------------

var auth = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();
var authProblems = auth.Validate(builder.Environment).ToList();
if (authProblems.Count > 0)
    throw new InvalidOperationException("Invalid authentication configuration: " + string.Join(" ", authProblems));

builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false; // keep "sub", "role" as issued
    options.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidAudience = auth.Audience,
        NameClaimType = "name",
        RoleClaimType = auth.RoleClaim,
        ClockSkew = TimeSpan.FromSeconds(30),
    };

    if (!string.IsNullOrWhiteSpace(auth.Authority))
    {
        // External identity provider: keys and issuer come from its OpenID Connect metadata.
        options.Authority = auth.Authority;
    }
    else
    {
        options.TokenValidationParameters.ValidIssuer = auth.Issuer;
        options.TokenValidationParameters.IssuerSigningKey = TokenService.SigningKeyFrom(auth.SigningKey!);
    }
});
builder.Services.AddAuthorizationBuilder().AddReevePolicies();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddHttpContextAccessor();

// ---- application ------------------------------------------------------------------------------

builder.Services.Configure<WorkerHealthOptions>(builder.Configuration.GetSection(WorkerHealthOptions.SectionName));
builder.Services.AddReeveApplication();
builder.Services.AddScoped<IRequestContext, HttpRequestContext>();
builder.Services.AddReevePersistence(builder.Configuration);
builder.Services.AddReeveRedis(builder.Configuration);
builder.Services.AddHealthChecks()
    .AddDbContextCheck<ReeveDbContext>("postgres", tags: [HealthEndpoints.ReadyTag]);

builder.Services.AddOptions<RateLimitingOptions>()
    .Bind(builder.Configuration.GetSection(RateLimitingOptions.SectionName))
    .Validate(o => new[] { RateLimitingOptions.DefaultPolicy, RateLimitingOptions.SubmissionsPolicy, RateLimitingOptions.AuthPolicy }
                       .All(o.Policies.ContainsKey)
                   && o.Policies.Values.All(p => p.PermitLimit > 0 && p.WindowSeconds > 0),
        "Rate limit policies 'default', 'submissions' and 'auth' must be configured with positive limits and windows.")
    .ValidateOnStart();
builder.Services.AddSingleton<ApiRateLimiter>();
builder.Services.AddReeveReverseProxy(builder.Configuration);

var app = builder.Build();

// First, so everything after it (rate limits, logs, telemetry) sees the real client address.
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();

app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi().AllowAnonymous();
}

// No HTTPS redirection here: TLS terminates at the edge (the nginx proxy, an ingress), and the
// container itself only serves HTTP.

// Degraded (e.g. Redis down) still answers 200: the API is serving, just with local fallbacks.
// Anonymous so load balancers and orchestrators can probe it.
app.MapHealthChecks("/api/v1/health").AllowAnonymous();
// For Kubernetes: live has no dependency checks (the process answers), ready needs PostgreSQL.
app.MapReeveHealth("/api/v1/health");
app.MapAuthEndpoints(auth);
app.MapJobEndpoints();
app.MapScheduleEndpoints();
app.MapOperationsEndpoints();
app.MapAuditEndpoints();

app.Run();

// Exposes the entry point to WebApplicationFactory in Reeve.ApiTests.
public partial class Program;
