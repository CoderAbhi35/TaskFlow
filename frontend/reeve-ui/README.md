# Reeve operations dashboard

Angular 22 console for Reeve: standalone components, zoneless change detection and signals.
It isn't a CRUD front end. It answers three questions quickly: *is the system healthy, what is slow
or failing, and which jobs are affected?*

| Screen | Shows |
|---|---|
| **Overview** | Throughput, success rate, failures, p50/p95 execution time, queue depth, workers, a finished-jobs chart, busiest queues, recent failures |
| **Jobs** | Server-side filters (status, type, priority) kept in the URL, cursor paging, and a submit-job form |
| **Job details** | Payload, retry policy, every attempt with worker, duration and error; cancel and retry. Updates live while the job is active |
| **Failures** | Failed and dead-lettered jobs with one-click retry |
| **Queues** | Backlog per job type: ready, scheduled, queued, running, dead-lettered, oldest-ready age |
| **Workers** | Status, heartbeat age with a stale flag, concurrency, job types |
| **Schedules** | Cron schedules in the operator's time zone; create, pause, resume, delete |

## Running it

Start the API (and a Scheduler plus a worker, to see jobs run), then:

```bash
npm start
```

Open <http://localhost:4200>. The dev server proxies `/api` to `http://localhost:5182`
([`proxy.conf.json`](proxy.conf.json)), so the API needs no CORS configuration.

```bash
npm test
```
```bash
npm run build
```

## How it's built

- **Reads are signal resources** (`httpResource`/`rxResource`), wrapped by
  [`safeResource`](src/app/core/safe-resource.ts). Angular throws from `value()` while a request is
  failing, and one failed request shouldn't break a whole page. The wrapper exposes the failure
  through `error()`, and [`LoadState`](src/app/shared/load-state.ts) renders it together with the
  correlation ID.
- **Auto-refresh** ([`autoRefresh`](src/app/core/refresh.ts)) polls only while the tab is visible,
  and refreshes once when the tab comes back. Job details poll every 2 s while the job can still
  change, then stop.
- **Paging** follows the API's cursors ([`jobPager`](src/app/features/jobs/job-pager.ts)).
  Auto-refresh never throws away pages the user has loaded.
- **Errors** are RFC 9457 problem documents, turned into messages and per-field validation errors
  ([`describeError`](src/app/core/problem.ts)). Every request carries an `X-Correlation-ID`.
- **No UI or chart library:** the design tokens in [`styles.scss`](src/styles.scss) cover light and
  dark themes and phone widths, and the throughput chart is plain SVG. The initial bundle is about
  80 kB compressed.
- **Short job IDs** use the *last* 8 characters. IDs are UUIDv7, so the first 8 are a timestamp that
  every job created in the same minute shares.

Real-time updates (SignalR) and route guards for admin actions come with later phases:
authentication is Phase 9.
