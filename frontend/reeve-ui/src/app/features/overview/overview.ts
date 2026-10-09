import { HttpParams, httpResource } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { API_BASE, ReeveApi, searchParams } from '../../core/api';
import { JobSummary, Page } from '../../core/models';
import { autoRefresh, nowSignal } from '../../core/refresh';
import { safeResource } from '../../core/safe-resource';
import { DurationPipe, PercentPipe, RelativeTimePipe } from '../../shared/format';
import { LoadState } from '../../shared/load-state';
import { StatusBadge } from '../../shared/status-badge';
import { ThroughputChart } from '../../shared/throughput-chart';

const WINDOWS = [
  { minutes: 15, label: '15 min' },
  { minutes: 60, label: '1 hour' },
  { minutes: 360, label: '6 hours' },
  { minutes: 1440, label: '24 hours' },
];

/** Answers three questions at a glance: is it healthy, what is slow or failing, what is waiting. */
@Component({
  selector: 'app-overview',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LoadState, ThroughputChart, StatusBadge, DurationPipe, PercentPipe, RelativeTimePipe],
  template: `
    <header class="page-header">
      <h1>Overview</h1>
      <div class="toolbar" role="group" aria-label="Time window">
        @for (w of windows; track w.minutes) {
          <button type="button" class="btn btn-small" [class.btn-primary]="window() === w.minutes"
                  [attr.aria-pressed]="window() === w.minutes" (click)="window.set(w.minutes)">{{ w.label }}</button>
        }
      </div>
    </header>

    <app-load-state what="overview" [loading]="overview.isLoading()" [hasData]="overview.hasValue()"
                    [error]="overview.error()" (retry)="overview.reload()">
      @if (overview.value(); as o) {
        <section class="grid grid-kpi" aria-label="Key figures">
          <div class="card kpi">
            <div class="kpi-label">Throughput</div>
            <div class="kpi-value">{{ o.throughputPerMinute.toFixed(1) }}<small class="muted">/min</small></div>
            <div class="kpi-hint">{{ o.submitted }} submitted in window</div>
          </div>
          <div class="card kpi" [attr.data-tone]="successTone()">
            <div class="kpi-label">Success rate</div>
            <div class="kpi-value">{{ o.successRate | percent1 }}</div>
            <div class="kpi-hint">{{ o.succeeded }} succeeded</div>
          </div>
          <div class="card kpi" [attr.data-tone]="o.failed + o.deadLettered > 0 ? 'danger' : null">
            <div class="kpi-label">Failed</div>
            <div class="kpi-value">{{ o.failed + o.deadLettered }}</div>
            <div class="kpi-hint">{{ o.deadLettered }} dead-lettered · {{ o.failedAttempts }} failed attempts</div>
          </div>
          <div class="card kpi">
            <div class="kpi-label">Execution p95</div>
            <div class="kpi-value">{{ o.p95DurationMs | duration }}</div>
            <div class="kpi-hint">median {{ o.p50DurationMs | duration }}</div>
          </div>
          <div class="card kpi" [attr.data-tone]="o.backlog.ready + o.backlog.queued > 100 ? 'warning' : null">
            <div class="kpi-label">Queue depth</div>
            <div class="kpi-value">{{ o.backlog.ready + o.backlog.queued }}</div>
            <div class="kpi-hint">{{ o.backlog.running }} running · {{ o.backlog.scheduled }} scheduled</div>
          </div>
          <div class="card kpi" [attr.data-tone]="o.workers.active === 0 ? 'danger' : o.workers.stale > 0 ? 'warning' : null">
            <div class="kpi-label">Workers</div>
            <div class="kpi-value">{{ o.workers.active }}</div>
            <div class="kpi-hint">{{ o.workers.totalConcurrency }} slots · {{ o.workers.stale }} stale</div>
          </div>
        </section>

        <section class="card" aria-label="Throughput">
          <h2>Jobs finished · last {{ windowLabel() }}</h2>
          <app-throughput-chart [series]="o.series" [bucketMinutes]="o.bucketMinutes" />
        </section>
      }
    </app-load-state>

    <div class="grid grid-2">
      <section class="card">
        <h2>Busiest queues</h2>
        <app-load-state what="queues" [loading]="queues.isLoading()" [hasData]="queues.hasValue()"
                        [error]="queues.error()" (retry)="queues.reload()" [empty]="busiest().length === 0"
                        emptyText="All queues are empty.">
          <table class="table">
            <thead><tr><th>Job type</th><th class="num">Waiting</th><th class="num">Running</th><th class="num">Dead</th></tr></thead>
            <tbody>
              @for (q of busiest(); track q.jobType) {
                <tr>
                  <td><a [routerLink]="['/jobs']" [queryParams]="{ jobType: q.jobType }">{{ q.jobType }}</a></td>
                  <td class="num">{{ q.ready + q.queued }}</td>
                  <td class="num">{{ q.running }}</td>
                  <td class="num">{{ q.deadLettered }}</td>
                </tr>
              }
            </tbody>
          </table>
        </app-load-state>
      </section>

      <section class="card">
        <h2>Recent failures <a class="btn btn-small" routerLink="/failures">View all</a></h2>
        <app-load-state what="failures" [loading]="failures.isLoading()" [hasData]="failures.hasValue()"
                        [error]="failures.error()" (retry)="failures.reload()"
                        [empty]="(failures.value()?.items?.length ?? 0) === 0" emptyText="No failed jobs. 🎉">
          <table class="table">
            <tbody>
              @for (job of failures.value()?.items; track job.id) {
                <tr>
                  <td><a [routerLink]="['/jobs', job.id]">{{ job.jobType }}</a><br />
                    <span class="muted">{{ job.completedAt | relative: now() }}</span></td>
                  <td><app-status-badge [status]="job.status" /></td>
                  <td class="error-text">{{ job.lastError }}</td>
                </tr>
              }
            </tbody>
          </table>
        </app-load-state>
      </section>
    </div>
  `,
})
export class Overview {
  private readonly api = inject(ReeveApi);

  protected readonly windows = WINDOWS;
  protected readonly window = signal(60);
  protected readonly windowLabel = computed(() => WINDOWS.find((w) => w.minutes === this.window())?.label ?? '');
  protected readonly now = nowSignal(5000);

  protected readonly overview = this.api.overview(this.window);
  protected readonly queues = this.api.queues();
  protected readonly failures = safeResource(httpResource<Page<JobSummary>>(() => ({
    url: `${API_BASE}/jobs`,
    params: searchParams({ status: ['FAILED', 'DEAD_LETTERED'], limit: 5 }) as HttpParams,
  })));

  protected readonly busiest = computed(() =>
    [...(this.queues.value() ?? [])]
      // Waiting is ready + queued: with Kafka, ready jobs are queued within a second.
      .filter((q) => q.ready + q.queued + q.running + q.deadLettered > 0)
      .sort((a, b) => b.ready + b.queued + b.running - (a.ready + a.queued + a.running))
      .slice(0, 6),
  );

  protected readonly successTone = computed(() => {
    const rate = this.overview.value()?.successRate;
    return rate == null ? null : rate >= 0.99 ? 'success' : rate >= 0.9 ? 'warning' : 'danger';
  });

  constructor() {
    autoRefresh(5000, this.overview, this.queues, this.failures);
  }
}
