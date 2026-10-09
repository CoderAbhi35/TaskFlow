using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Reeve.Application.Abstractions;
using Reeve.Domain.Jobs;
using Reeve.Domain.Workers;
using Reeve.Infrastructure.Persistence;

namespace Reeve.IntegrationTests.Persistence;

[Collection(PostgresCollection.Name)]
public class JobPersistenceTests(PostgresFixture fixture)
{
    // PostgreSQL stores microseconds; .NET ticks are 100ns. Compare timestamps with this tolerance.
    private static readonly TimeSpan DbPrecision = TimeSpan.FromMilliseconds(1);

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;

    private static Job NewJob(string? idempotencyKey = null) =>
        Job.Create("GENERATE_REPORT", """{"customerId": 12345, "reportType": "MONTHLY"}""",
            JobPriority.High, new RetryPolicy(3, 30), Now, idempotencyKey: idempotencyKey);

    private async Task<Guid> SaveNewJobAsync(Job job)
    {
        await using var scope = fixture.CreateScope();
        scope.ServiceProvider.GetRequiredService<IJobRepository>().Add(job);
        await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        return job.Id;
    }

    private async Task<Job> LoadAsync(Guid id)
    {
        await using var scope = fixture.CreateScope();
        var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(id);
        job.Should().NotBeNull();
        return job!;
    }

    [Fact]
    public async Task Job_round_trips_with_all_fields()
    {
        var job = NewJob(idempotencyKey: $"key-{Guid.NewGuid()}");
        await SaveNewJobAsync(job);

        var loaded = await LoadAsync(job.Id);

        loaded.Should().BeEquivalentTo(job, o => o
            .Excluding(j => j.Payload)
            .Using<DateTimeOffset>(ctx => ctx.Subject.Should().BeCloseTo(ctx.Expectation, DbPrecision))
            .WhenTypeIs<DateTimeOffset>());

        // jsonb normalises whitespace, so compare the JSON semantically.
        loaded.Payload.Should().Contain("\"customerId\": 12345").And.Contain("\"reportType\": \"MONTHLY\"");
    }

    [Fact]
    public async Task Attempts_added_in_a_later_unit_of_work_are_inserted()
    {
        // Regression guard: with client-generated keys EF must INSERT new attempts, not try to UPDATE them.
        var id = await SaveNewJobAsync(NewJob());

        await using (var scope = fixture.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(id);
            job!.Start("worker-1", Now);
            job.Fail("Connection reset", isTransient: true, Now, new Random(1));
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        await using (var scope = fixture.CreateScope())
        {
            var job = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(id);
            job!.Start("worker-2", Now);
            job.Succeed(Now);
            await scope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();
        }

        var loaded = await LoadAsync(id);
        loaded.Status.Should().Be(JobStatus.Succeeded);
        loaded.Attempts.Select(a => (a.AttemptNumber, a.WorkerId, a.Status)).Should().Equal(
            (1, "worker-1", JobAttemptStatus.Failed),
            (2, "worker-2", JobAttemptStatus.Succeeded));
        loaded.Attempts[0].Error.Should().Be("Connection reset");
    }

    [Fact]
    public async Task Concurrent_update_of_same_job_is_rejected()
    {
        var id = await SaveNewJobAsync(NewJob());

        // Two processes load the same job: a worker about to finish it and an operator cancelling it.
        await using var workerScope = fixture.CreateScope();
        await using var operatorScope = fixture.CreateScope();
        var workerView = await workerScope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(id);
        var operatorView = await operatorScope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdAsync(id);

        operatorView!.Cancel(Now);
        await operatorScope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        workerView!.Start("worker-1", Now);
        var act = () => workerScope.ServiceProvider.GetRequiredService<IUnitOfWork>().SaveChangesAsync();

        await act.Should().ThrowAsync<ConcurrencyConflictException>();
        (await LoadAsync(id)).Status.Should().Be(JobStatus.Cancelled);
    }

    [Fact]
    public async Task Duplicate_idempotency_key_is_rejected_by_the_database()
    {
        var key = $"order-{Guid.NewGuid()}";
        await SaveNewJobAsync(NewJob(key));

        var act = () => SaveNewJobAsync(NewJob(key));

        var thrown = await act.Should().ThrowAsync<DuplicateIdempotencyKeyException>();
        thrown.Which.IdempotencyKey.Should().Be(key);
        thrown.Which.InnerException.Should().BeOfType<DbUpdateException>()
            .Which.InnerException.Should().BeOfType<PostgresException>()
            .Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
    }

    [Fact]
    public async Task Context_stays_usable_after_a_duplicate_key_is_rejected()
    {
        var key = $"order-{Guid.NewGuid()}";
        var originalId = await SaveNewJobAsync(NewJob(key));

        await using var scope = fixture.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        jobs.Add(NewJob(key));
        await unitOfWork.Invoking(u => u.SaveChangesAsync()).Should().ThrowAsync<DuplicateIdempotencyKeyException>();

        // The rejected insert must not be retried by the next save in the same request.
        (await jobs.GetByIdempotencyKeyAsync("", key))!.Id.Should().Be(originalId);
        var other = NewJob();
        jobs.Add(other);
        await unitOfWork.SaveChangesAsync();
        (await LoadAsync(other.Id)).Should().NotBeNull();
    }

    [Fact]
    public async Task Job_can_be_found_by_idempotency_key()
    {
        var key = $"order-{Guid.NewGuid()}";
        var id = await SaveNewJobAsync(NewJob(key));

        await using var scope = fixture.CreateScope();
        var found = await scope.ServiceProvider.GetRequiredService<IJobRepository>().GetByIdempotencyKeyAsync("", key);

        found!.Id.Should().Be(id);
    }

    [Fact]
    public async Task The_same_idempotency_key_in_different_scopes_is_two_jobs()
    {
        var key = $"order-{Guid.NewGuid()}";
        Job ForCaller(string scope) => Job.Create("GENERATE_REPORT", """{"customerId":1}""",
            JobPriority.Normal, RetryPolicy.Default, Now, idempotencyKey: key, idempotencyScope: scope);
        var alice = await SaveNewJobAsync(ForCaller("alice"));
        var bob = await SaveNewJobAsync(ForCaller("bob"));

        await using var scope = fixture.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IJobRepository>();
        alice.Should().NotBe(bob);
        (await jobs.GetByIdempotencyKeyAsync("alice", key))!.Id.Should().Be(alice);
        (await jobs.GetByIdempotencyKeyAsync("bob", key))!.Id.Should().Be(bob);
        (await jobs.GetByIdempotencyKeyAsync("mallory", key)).Should().BeNull();
    }

    [Fact]
    public async Task Worker_round_trips_supported_job_types_as_array()
    {
        var workerId = $"host-{Guid.NewGuid():N}";
        var worker = WorkerNode.Register(workerId, "host", 8, ["GENERATE_REPORT", "SEND_EMAIL"], Now);

        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
            db.Workers.Add(worker);
            await db.SaveChangesAsync();
        }

        await using (var scope = fixture.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ReeveDbContext>();
            var loaded = await db.Workers.SingleAsync(w => w.Id == workerId);
            loaded.SupportedJobTypes.Should().Equal("GENERATE_REPORT", "SEND_EMAIL");
            loaded.Concurrency.Should().Be(8);

            // Array containment is queryable server-side, which the dispatcher will need.
            var canRunEmail = await db.Workers
                .Where(w => w.Id == workerId && w.SupportedJobTypes.Contains("SEND_EMAIL"))
                .AnyAsync();
            canRunEmail.Should().BeTrue();
        }
    }
}
