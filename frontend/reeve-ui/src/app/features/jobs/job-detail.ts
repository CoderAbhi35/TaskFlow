import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ReeveApi } from '../../core/api';
import { AuthService } from '../../core/auth';
import { ACTIVE_STATUSES, Job } from '../../core/models';
import { ErrorView, describeError } from '../../core/problem';
import { autoRefresh, nowSignal } from '../../core/refresh';
import { DurationPipe, RelativeTimePipe, shortId } from '../../shared/format';
import { JsonView } from '../../shared/json-view';
import { LoadState } from '../../shared/load-state';
import { StatusBadge } from '../../shared/status-badge';

@Component({
  selector: 'app-job-detail',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, LoadState, StatusBadge, JsonView, DurationPipe, RelativeTimePipe],
  template: `
    <p><a routerLink="/jobs">← Jobs</a></p>
    <app-load-state what="the job" [loading]="job.isLoading()" [hasData]="job.hasValue()"
                    [error]="job.error()" (retry)="job.reload()">
      @if (job.value(); as j) {
        <header class="page-header">
          <div>
            <h1>{{ j.jobType }} <app-status-badge [status]="j.status" /></h1>
            <code class="muted">{{ j.id }}</code>
          </div>
          <div class="toolbar">
            @if (isActive() && auth.canOperate()) {
              <button type="button" class="btn btn-danger" [disabled]="busy()" (click)="cancel(j)">Cancel job</button>
            }
            @if ((j.status === 'FAILED' || j.status === 'DEAD_LETTERED') && auth.canOperate()) {
              <button type="button" class="btn btn-primary" [disabled]="busy()" (click)="retry(j)">Retry job</button>
            }
          </div>
        </header>

        @if (actionError(); as e) {
          <div class="notice notice-error" role="alert">{{ e.message }}</div>
        }
        @if (j.lastError) {
          <div class="notice notice-error"><strong>Last error:</strong> <span>{{ j.lastError }}</span></div>
        }

        <div class="grid grid-2" style="margin-top: 1rem">
          <section class="card">
            <h2>Details</h2>
            <dl class="props">
              <dt>Priority</dt><dd><app-status-badge [status]="j.priority" /></dd>
              <dt>Attempts</dt><dd>{{ j.attemptCount }}</dd>
              <dt>Retry policy</dt><dd>up to {{ j.retryPolicy.maxRetries }} retries, {{ j.retryPolicy.backoffSeconds }} s base backoff ({{ j.retryCount }} used)</dd>
              <dt>Created</dt><dd [title]="j.createdAt">{{ j.createdAt | relative: now() }}</dd>
              <!-- scheduledAt keeps the last retry time after a job finishes; it only matters while waiting. -->
              @if (j.scheduledAt && j.status === 'PENDING') {
                <dt>{{ j.retryCount > 0 ? 'Retries' : 'Runs' }}</dt><dd [title]="j.scheduledAt">{{ j.scheduledAt | relative: now() }}</dd>
              }
              @if (j.completedAt) {
                <dt>Finished</dt><dd [title]="j.completedAt">{{ j.completedAt | relative: now() }}</dd>
                <dt>Total time</dt><dd>{{ totalMs() | duration }}</dd>
              }
              @if (j.idempotencyKey) { <dt>Idempotency key</dt><dd><code>{{ j.idempotencyKey }}</code></dd> }
            </dl>
          </section>
          <section class="card">
            <h2>Payload</h2>
            <app-json-view [value]="j.payload" />
          </section>
        </div>

        <section class="card">
          <h2>Attempts</h2>
          <app-load-state what="attempts" [loading]="attempts.isLoading()" [hasData]="attempts.hasValue()"
                          [error]="attempts.error()" (retry)="attempts.reload()"
                          [empty]="(attempts.value()?.length ?? 0) === 0" emptyText="Not started yet.">
            <div class="table-wrap">
              <table class="table">
                <thead><tr><th class="num">#</th><th>Worker</th><th>Status</th><th>Started</th><th class="num">Duration</th><th>Error</th></tr></thead>
                <tbody>
                  @for (a of attempts.value(); track a.id) {
                    <tr>
                      <td class="num">{{ a.attemptNumber }}</td>
                      <td><code>{{ a.workerId }}</code></td>
                      <td><app-status-badge [status]="a.status" /></td>
                      <td [title]="a.startedAt">{{ a.startedAt | relative: now() }}</td>
                      <td class="num">{{ a.durationMs | duration }}</td>
                      <td class="error-text">{{ a.error }}</td>
                    </tr>
                  }
                </tbody>
              </table>
            </div>
          </app-load-state>
        </section>
      }
    </app-load-state>
  `,
})
export class JobDetail {
  private readonly api = inject(ReeveApi);
  protected readonly auth = inject(AuthService);

  /** Bound from the :id route parameter. */
  readonly id = input.required<string>();

  protected readonly job = this.api.job(this.id);
  protected readonly attempts = this.api.attempts(this.id);
  protected readonly now = nowSignal(1000);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<ErrorView | null>(null);

  protected readonly isActive = computed(() => {
    const status = this.job.value()?.status;
    return !!status && ACTIVE_STATUSES.includes(status);
  });

  protected readonly totalMs = computed(() => {
    const j = this.job.value();
    return j?.completedAt ? new Date(j.completedAt).getTime() - new Date(j.createdAt).getTime() : null;
  });

  constructor() {
    // Follow a job while it can still change; stop polling once it has finished.
    const whileActive = (r: { reload(): boolean }) => ({ reload: () => (this.isActive() ? r.reload() : false) });
    autoRefresh(2000, whileActive(this.job), whileActive(this.attempts));
  }

  protected cancel(job: Job): void {
    if (confirm(`Cancel ${job.jobType} job ${shortId(job.id)}?`)) this.run(this.api.cancelJob(job.id));
  }

  protected retry(job: Job): void {
    this.run(this.api.retryJob(job.id));
  }

  private run(action: Observable<Job>): void {
    this.busy.set(true);
    this.actionError.set(null);
    action.subscribe({
      next: (updated) => {
        this.job.set(updated);
        this.attempts.reload();
        this.busy.set(false);
      },
      error: (e) => {
        this.actionError.set(describeError(e));
        this.busy.set(false);
        this.job.reload();
      },
    });
  }
}
