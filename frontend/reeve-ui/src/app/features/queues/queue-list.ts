import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ReeveApi } from '../../core/api';
import { autoRefresh } from '../../core/refresh';
import { DurationPipe } from '../../shared/format';
import { LoadState } from '../../shared/load-state';
import { StatusBadge } from '../../shared/status-badge';

/**
 * Backlog per job type. Waiting work is ready (in PostgreSQL) plus queued (published to Kafka, not yet
 * claimed): with Kafka, ready jobs are queued within a second whether or not a worker is consuming.
 */
@Component({
  selector: 'app-queue-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LoadState, StatusBadge, DurationPipe],
  template: `
    <header class="page-header">
      <h1>Queues</h1>
      <span class="muted">One queue per job type · totals: {{ totals().waiting }} waiting, {{ totals().running }} running</span>
    </header>

    <section class="card">
      <app-load-state what="queues" [loading]="queues.isLoading()" [hasData]="queues.hasValue()"
                      [error]="queues.error()" (retry)="queues.reload()" [empty]="(queues.value()?.length ?? 0) === 0">
        <div class="table-wrap">
          <table class="table">
            <thead>
              <tr>
                <th>Job type</th><th>State</th>
                <th class="num">Ready</th><th class="num">Scheduled</th><th class="num">Queued</th>
                <th class="num">Running</th><th class="num">Dead-lettered</th><th class="num">Longest wait</th>
              </tr>
            </thead>
            <tbody>
              @for (q of queues.value(); track q.jobType) {
                <tr>
                  <td><a [routerLink]="['/jobs']" [queryParams]="{ jobType: q.jobType }">{{ q.jobType }}</a></td>
                  <td><app-status-badge [status]="q.enabled ? 'ENABLED' : 'PAUSED'" /></td>
                  <td class="num">{{ q.ready }}</td>
                  <td class="num">{{ q.scheduled }}</td>
                  <td class="num">{{ q.queued }}</td>
                  <td class="num">{{ q.running }}</td>
                  <td class="num">
                    @if (q.deadLettered > 0) {
                      <a [routerLink]="['/jobs']" [queryParams]="{ jobType: q.jobType, status: 'DEAD_LETTERED' }">{{ q.deadLettered }}</a>
                    } @else { 0 }
                  </td>
                  <td class="num">{{ q.oldestWaitingAgeSeconds == null ? '—' : ((q.oldestWaitingAgeSeconds * 1000) | duration) }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </app-load-state>
    </section>
  `,
})
export class QueueList {
  protected readonly queues = inject(ReeveApi).queues();

  protected readonly totals = computed(() =>
    (this.queues.value() ?? []).reduce(
      (t, q) => ({ waiting: t.waiting + q.ready + q.queued, running: t.running + q.running }),
      { waiting: 0, running: 0 },
    ),
  );

  constructor() {
    autoRefresh(5000, this.queues);
  }
}
