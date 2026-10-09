#!/usr/bin/env bash
# Benchmark and failure scenarios against the Docker Compose stack. Results go to
# tests/Reeve.LoadTests/results/<name>.json.
#
#   tests/Reeve.LoadTests/scenarios.sh <scenario> [args]
#
# Scenarios:
#   warmup                     500 jobs, to JIT-compile and fill connection pools
#   submit <concurrency> [n]   API submission rate and latency (workers running)
#   drain <workers> [n]        backlog drain: submit n jobs with workers stopped, then start them
#   steady <rate> <seconds>    fixed submission rate with workers running (end-to-end latency)
#   crash-worker               kill -9 one worker mid-run (SEND_NOTIFICATION, effects ledger)
#   restart <service>          restart kafka or postgres mid-run
#   outage <seconds>           stop all workers for a while under steady load, then recover
#   redis-off <concurrency>    API submission with Redis stopped (local rate limiting, no cache)
#
# SUFFIX=-x is appended to result names (for example with TELEMETRY=false).
set -euo pipefail
cd "$(dirname "$0")/../.."

DC="docker compose -f docker-compose.yml -f tests/Reeve.LoadTests/compose.benchmark.yml"
OUT=tests/Reeve.LoadTests/results
TOOL="dotnet run --project tests/Reeve.LoadTests -c Release --no-build --"
WORKERS=${WORKERS:-2}
SUFFIX=${SUFFIX:-}

workers() { $DC up -d --scale worker="$1" --no-recreate --wait worker >/dev/null 2>&1; }
stamp() { date -u +%H%M%S; }

scenario=${1:?scenario}; shift || true
case "$scenario" in
  warmup)
    $TOOL submit --name warmup --jobs 500 --concurrency 16 ;;

  submit)
    c=${1:?concurrency}; n=${2:-10000}
    $TOOL submit --name "submit-c$c" --jobs "$n" --concurrency "$c" --docker-stats --out "$OUT/submit-c$c$SUFFIX.json" ;;

  drain)
    w=${1:?workers}; n=${2:-10000}; run="drain-w$w-$(stamp)"
    $DC stop worker >/dev/null 2>&1
    $TOOL submit --run-id "$run" --name "drain-w$w (submission)" --jobs "$n" --concurrency 64 --no-wait
    workers "$w"
    $TOOL measure --run-id "$run" --name "drain-w$w" --docker-stats --out "$OUT/drain-w$w$SUFFIX.json" ;;

  steady)
    rate=${1:?rate}; secs=${2:-60}
    $TOOL submit --name "steady-r$rate" --jobs $((rate * secs)) --rate "$rate" --concurrency 64 --docker-stats --out "$OUT/steady-r$rate$SUFFIX.json" ;;

  crash-worker)
    workers "$WORKERS"
    $TOOL submit --name crash-worker --type SEND_NOTIFICATION --duration-ms 200 --jobs 3000 --rate 150 --concurrency 32 \
      --docker-stats --out "$OUT/failure-crash-worker.json" &
    sleep 8
    victim=$($DC ps -q worker | head -1)
    echo ">>> $(date -u +%T) kill -9 worker ${victim:0:12}"
    docker kill --signal KILL "$victim" >/dev/null
    wait
    $DC up -d --scale worker="$WORKERS" --no-recreate worker >/dev/null 2>&1 ;;

  restart)
    svc=${1:?service}
    workers "$WORKERS"
    # This tool's own Kafka client once crashed while its broker restarted: don't sample lag then.
    lag=""; [ "$svc" = kafka ] && lag="--no-kafka-lag"
    $TOOL submit --name "restart-$svc" --duration-ms 50 --jobs 3000 --rate 100 --concurrency 32 $lag \
      --docker-stats --out "$OUT/failure-restart-$svc.json" &
    sleep 8
    echo ">>> $(date -u +%T) restart $svc"
    $DC restart "$svc" >/dev/null 2>&1
    wait ;;

  outage)
    secs=${1:-30}
    workers "$WORKERS"
    $TOOL submit --name "outage-${secs}s" --duration-ms 20 --jobs 6000 --rate 100 --concurrency 32 \
      --docker-stats --out "$OUT/failure-outage-${secs}s.json" &
    sleep 10
    echo ">>> $(date -u +%T) stop all workers for $secs s"
    $DC stop worker >/dev/null 2>&1
    sleep "$secs"
    echo ">>> $(date -u +%T) start workers"
    workers "$WORKERS"
    wait ;;

  redis-off)
    c=${1:?concurrency}
    $DC stop redis >/dev/null 2>&1
    $TOOL submit --name "submit-c$c-redis-off" --jobs 10000 --concurrency "$c" --docker-stats --out "$OUT/submit-c$c-redis-off.json" || true
    $DC up -d --wait redis >/dev/null 2>&1 ;;

  *) echo "unknown scenario: $scenario" >&2; exit 2 ;;
esac
