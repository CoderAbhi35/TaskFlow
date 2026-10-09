using Npgsql;

namespace Reeve.LoadTests;

/// <summary>Measures a run from the database: every job carries the run ID in its payload.</summary>
internal sealed class RunAnalysis(string connectionString)
{
    /// <summary>Waits until every job of the run is finished; false on timeout.</summary>
    public async Task<bool> WaitForRunAsync(string runId, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (DateTimeOffset.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (await ScalarAsync<long>("""
                        select count(*) from jobs
                        where payload->>'run' = @run and status not in ('Succeeded', 'Failed', 'DeadLettered', 'Cancelled')
                        """, runId, cancellationToken) == 0)
                    return true;
            }
            catch (NpgsqlException)
            {
                // Database restarting during a failure test: keep waiting.
            }
            await Task.Delay(1000, cancellationToken);
        }
        return false;
    }

    public async Task<ExecutionResult> AnalyseAsync(string runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            with r as (select * from jobs where payload->>'run' = @run),
            a as (
              select job_id, min(started_at) as first_start, count(*) as attempts
              from job_attempts where job_id in (select id from r) group by job_id
            ),
            x as (
              select extract(epoch from ended_at - started_at) as seconds
              from job_attempts where job_id in (select id from r) and status = 'Succeeded'
            )
            select
              count(*),
              count(*) filter (where r.status = 'Succeeded'),
              count(*) filter (where r.status = 'Failed'),
              count(*) filter (where r.status = 'DeadLettered'),
              count(*) filter (where r.retry_count > 0),
              count(*) filter (where a.attempts > 1),
              coalesce(sum(a.attempts), 0),
              min(r.created_at), max(r.created_at), min(a.first_start), max(r.completed_at),
              percentile_cont(array[0.5, 0.95, 0.99]) within group (order by extract(epoch from r.completed_at - r.created_at)),
              max(extract(epoch from r.completed_at - r.created_at)),
              percentile_cont(array[0.5, 0.95, 0.99]) within group (order by extract(epoch from a.first_start - r.created_at)),
              (select percentile_cont(array[0.5, 0.95, 0.99]) within group (order by seconds) from x),
              (select count(*) from job_effects e where e.job_id in (select id from r)),
              (select count(*) from r join a on a.job_id = r.id join job_effects e on e.job_id = r.id where a.attempts > 1)
            from r left join a on a.job_id = r.id
            """, connection);
        command.Parameters.AddWithValue("run", runId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);

        DateTimeOffset? At(int i) => reader.IsDBNull(i) ? null : reader.GetFieldValue<DateTimeOffset>(i);
        Latency? Seconds(int i) => reader.IsDBNull(i) ? null : Latency.FromSeconds(reader.GetFieldValue<double[]>(i));

        var jobs = reader.GetInt64(0);
        var firstCreated = At(7);
        var firstStart = At(9);
        var lastCompleted = At(10);
        return new ExecutionResult
        {
            Jobs = jobs,
            Succeeded = reader.GetInt64(1),
            Failed = reader.GetInt64(2),
            DeadLettered = reader.GetInt64(3),
            Retried = reader.GetInt64(4),
            RanMoreThanOnce = reader.GetInt64(5),
            Attempts = reader.GetInt64(6),
            FirstCreated = firstCreated,
            LastCreated = At(8),
            FirstStarted = firstStart,
            LastCompleted = lastCompleted,
            // Throughput from the first attempt start: excludes time the backlog waited for workers
            // (for example while they were stopped in the queue-recovery scenario).
            ExecutionJobsPerSecond = firstStart is { } s && lastCompleted is { } e && e > s
                ? Math.Round(jobs / (e - s).TotalSeconds, 1) : 0,
            EndToEndJobsPerSecond = firstCreated is { } c && lastCompleted is { } l && l > c
                ? Math.Round(jobs / (l - c).TotalSeconds, 1) : 0,
            EndToEndSeconds = Seconds(11),
            EndToEndMaxSeconds = reader.IsDBNull(12) ? null : Math.Round(reader.GetDouble(12), 3),
            QueueWaitSeconds = Seconds(13),
            ExecutionSeconds = Seconds(14),
            Effects = reader.GetInt64(15),
            EffectsOnJobsThatRanMoreThanOnce = reader.GetInt64(16),
        };
    }

    private async Task<T> ScalarAsync<T>(string sql, string runId, CancellationToken cancellationToken)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("run", runId);
        return (T)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}
