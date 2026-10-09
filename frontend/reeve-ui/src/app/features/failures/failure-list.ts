import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { ReeveApi } from '../../core/api';
import { AuthService } from '../../core/auth';
import { JobFilters, JobSummary } from '../../core/models';
import { describeError } from '../../core/problem';
import { shortId } from '../../shared/format';
import { autoRefresh, nowSignal } from '../../core/refresh';
import { LoadState } from '../../shared/load-state';
import { jobPager } from '../jobs/job-pager';
import { JobTable } from '../jobs/job-table';

/**
 * Failed and dead-lettered jobs, newest first, with a retry action. Dead-lettered jobs exhausted their
 * retry policy on transient errors; failed jobs hit a permanent error. Both can be retried manually.
 */
@Component({
  selector: 'app-failure-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LoadState, JobTable],
  template: `
    <header class="page-header">
      <h1>Failures</h1>
      <span class="muted">Failed (permanent error) and dead-lettered (retries exhausted) jobs</span>
    </header>

    @if (message(); as m) {
      <div class="notice" [class.notice-error]="m.error" [class.notice-success]="!m.error" role="status">{{ m.text }}</div>
    }

    <section class="card">
      <app-load-state what="failures" [loading]="pager.firstPage.isLoading()" [hasData]="pager.firstPage.hasValue()"
                      [error]="pager.firstPage.error()" (retry)="pager.firstPage.reload()"
                      [empty]="pager.items().length === 0" emptyText="No failed jobs. 🎉">
        <app-job-table [jobs]="pager.items()" [now]="now()" [showRetry]="auth.canOperate()" [busy]="retrying()" (retry)="retry($event)" />
        @if (pager.nextCursor()) {
          <button type="button" class="btn" style="margin-top: 0.75rem" (click)="pager.loadMore()" [disabled]="pager.loadingMore()">
            {{ pager.loadingMore() ? 'Loading…' : 'Load more' }}
          </button>
        }
      </app-load-state>
    </section>
  `,
})
export class FailureList {
  private readonly api = inject(ReeveApi);
  protected readonly auth = inject(AuthService);

  private readonly filters = signal<JobFilters>({ status: ['FAILED', 'DEAD_LETTERED'], limit: 50 });
  protected readonly pager = jobPager(this.filters);
  protected readonly now = nowSignal(5000);
  protected readonly retrying = signal<ReadonlySet<string>>(new Set());
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);

  constructor() {
    autoRefresh(10_000, this.pager);
  }

  protected retry(job: JobSummary): void {
    this.retrying.update((s) => new Set(s).add(job.id));
    this.api.retryJob(job.id).subscribe({
      next: () => {
        this.done(job.id, { text: `${job.jobType} ${shortId(job.id)} was queued again.`, error: false });
        this.pager.firstPage.reload();
      },
      error: (e) => this.done(job.id, { text: describeError(e).message, error: true }),
    });
  }

  private done(id: string, message: { text: string; error: boolean }): void {
    this.retrying.update((s) => {
      const next = new Set(s);
      next.delete(id);
      return next;
    });
    this.message.set(message);
  }
}

