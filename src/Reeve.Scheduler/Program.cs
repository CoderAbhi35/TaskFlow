using Reeve.Application;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Health;
using Reeve.Infrastructure.Messaging;
using Reeve.Infrastructure.Observability;
using Reeve.Scheduler;

// A web host only for the health probes (/health/live, /health/ready); the work is done by the
// hosted services. In containers it listens on 8080; in Development on a random local port.
var builder = WebApplication.CreateBuilder(args);

builder.AddReeveTelemetry("reeve-scheduler");

builder.Services.AddReeveApplication();
builder.Services.AddReevePersistence(builder.Configuration);
builder.Services.AddReeveDispatcher(builder.Configuration);
builder.Services.Configure<DispatcherOptions>(builder.Configuration.GetSection(DispatcherOptions.SectionName));
builder.Services.AddHostedService<ScheduleService>();
builder.Services.AddHostedService<DispatcherService>();
builder.Services.AddHostedService<QueuedJobSweeperService>();
builder.Services.AddHostedService<BacklogSampler>();

builder.Services.AddHealthChecks()
    .AddLoopsAlive()
    // Ready while dispatch passes succeed, which needs both PostgreSQL and Kafka.
    .AddLoopSucceeded(DispatcherService.LoopName, TimeSpan.FromSeconds(30));

var app = builder.Build();
app.MapReeveHealth("/health");
app.Run();
