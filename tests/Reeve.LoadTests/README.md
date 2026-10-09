# Reeve load and failure tests

A console tool, not a unit test project. Jobs go in through the public entry point (the
dashboard's nginx, `http://localhost:8080/api/v1/jobs`). Everything after submission is measured
from PostgreSQL, the system of record, and from Kafka's offsets. Each job carries a run ID in its
payload, so a run's jobs can be picked out afterwards.

## Running

Start the stack with the benchmark settings. They raise the per-user rate limits, because one
load generator is one user, and make worker concurrency configurable (`WORKER_CONCURRENCY`,
default 10):

```bash
docker compose -f docker-compose.yml -f tests/Reeve.LoadTests/compose.benchmark.yml up -d --build --wait
```
```bash
dotnet build tests/Reeve.LoadTests -c Release
```

Then run scenarios with [`scenarios.sh`](scenarios.sh). Each writes
`tests/Reeve.LoadTests/results/<name>.json` (not committed):

```bash
tests/Reeve.LoadTests/scenarios.sh warmup
```
```bash
tests/Reeve.LoadTests/scenarios.sh drain 4
```

| Scenario | What it does |
|---|---|
| `submit <c> [n]` | `n` submissions over `c` connections, with workers running |
| `drain <w> [n]` | Stops the workers, submits `n` jobs, starts `w` workers and measures until the backlog is gone |
| `steady <rate> <s>` | A fixed submission rate (open loop) with workers running: end-to-end latency |
| `redis-off <c>` | Submission with Redis stopped (local rate limiting, no cache) |
| `crash-worker` | `kill -9` of one worker mid-run; jobs use the effects ledger |
| `restart kafka\|postgres` | Restarts the broker or the database mid-run |
| `outage <s>` | Stops every worker for `s` seconds under steady load, then starts them again |

Set `TELEMETRY=false` when starting the stack (and `SUFFIX=-no-telemetry` for the result names)
to measure what tracing and metrics cost.

## The tool

```bash
dotnet run --project tests/Reeve.LoadTests -c Release -- submit --jobs 10000 --concurrency 64 --docker-stats --out result.json
```

| Option | Default | |
|---|---|---|
| `--jobs`, `--concurrency` | 1000, 32 | Closed loop: each connection sends its next request when the last one answers |
| `--rate` | | Open loop: request *i* is sent at *i* / rate seconds, however slow earlier ones were |
| `--type`, `--duration-ms` | `GENERATE_REPORT`, 0 | Job type, and the simulated work per job |
| `--no-wait` / `measure --run-id` | | Submit now and measure later (the drain scenario) |
| `--docker-stats` | off | Record each container's CPU and memory |
| `--base-url`, `--db`, `--kafka` | local compose | Where to submit, and what to sample |

Reported per run:
- **Submission:** requests/s, latency p50/p95/p99/max, status codes.
- **Execution** (from the database):
  - throughput from the first attempt, and from the first submission;
  - end-to-end latency, queue wait and execution time;
  - failures, retries, jobs that ran more than once, and effects recorded.
- **Sampled every second:** backlog, running jobs, Kafka consumer lag, database transactions/s,
  connections and lock waits.
