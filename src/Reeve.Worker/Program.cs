using Reeve.Application;
using Reeve.Infrastructure;
using Reeve.Infrastructure.Health;
using Reeve.Infrastructure.Observability;
using Reeve.Worker;
using Reeve.Worker.Services;

// A web host only for the health probes (/health/live, /health/ready); the work is done by the
// hosted services. In containers it listens on 8080; in Development on a random local port, so
// several workers can run side by side.
var builder = WebApplication.CreateBuilder(args);

builder.AddReeveTelemetry("reeve-worker");

// Enough database connections for every running job plus claiming, heartbeats and recovery, unless
// configured explicitly (see Reeve.Infrastructure.DependencyInjection.MaxPoolSizeKey).
var worker = builder.Configuration.GetSection(WorkerOptions.SectionName).Get<WorkerOptions>() ?? new WorkerOptions();
if (builder.Configuration[Reeve.Infrastructure.DependencyInjection.MaxPoolSizeKey] is null)
    builder.Configuration.AddInMemoryCollection([new(Reeve.Infrastructure.DependencyInjection.MaxPoolSizeKey, (worker.Concurrency + 8).ToString())]);

builder.Services.AddReeveApplication();
builder.Services.AddReevePersistence(builder.Configuration);
builder.Services.AddReeveWorker(builder.Configuration);
builder.Services.AddSampleJobHandlers();

var health = builder.Services.AddHealthChecks()
    .AddLoopsAlive()
    // Ready while it is registered and heartbeating, which also means PostgreSQL is reachable...
    .AddLoopSucceeded(WorkerLifecycleService.LoopName, worker.HeartbeatInterval * 3);
if (worker.Transport == WorkerTransport.Kafka)
    // ...and its Kafka consumer is polling without errors.
    health.AddLoopSucceeded(KafkaJobConsumerService.LoopName, TimeSpan.FromSeconds(30));

var app = builder.Build();
app.MapReeveHealth("/health");
app.Run();
