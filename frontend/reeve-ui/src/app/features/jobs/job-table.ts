import { ChangeDetectionStrategy, Component, input, output } from '@angular/core';
import { RouterLink } from '@angular/router';
import { JobSummary } from '../../core/models';
import { RelativeTimePipe, ShortIdPipe } from '../../shared/format';
import { StatusBadge } from '../../shared/status-badge';

/** The job list used by the Jobs and Failures pages. */
@Component({
  selector: 'app-job-table',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterLink, StatusBadge, RelativeTimePipe, ShortIdPipe],
  template: `
    <div class="table-wrap">
      <table class="table">
        <thead>
          <tr>
            <th>Job</th>
            <th>Status</th>
            <th>Priority</th>
            <th class="num">Attempts</th>
            <th>Created</th>
            <th>Finished</th>
            <th>Last error</th>
            @if (showRetry()) { <th><span class="muted">Action</span></th> }
          </tr>
        </thead>
        <tbody>
          @for (job of jobs(); track job.id) {
            <tr>
              <td>
                <a [routerLink]="['/jobs', job.id]">{{ job.jobType }}</a><br />
                <code class="muted" [title]="job.id">{{ job.id | shortId }}</code>
              </td>
              <td><app-status-badge [status]="job.status" /></td>
              <td><app-status-badge [status]="job.priority" /></td>
              <td class="num">{{ job.attemptCount }}</td>
              <td [title]="job.createdAt">{{ job.createdAt | relative: now() }}</td>
              <td [title]="job.completedAt ?? ''">{{ job.completedAt | relative: now() }}</td>
              <td class="error-text">{{ job.lastError }}</td>
              @if (showRetry()) {
                <td>
                  <button type="button" class="btn btn-small" [disabled]="busy().has(job.id)" (click)="retry.emit(job)">
                    {{ busy().has(job.id) ? 'Retrying…' : 'Retry' }}
                  </button>
                </td>
              }
            </tr>
          }
        </tbody>
      </table>
    </div>
  `,
})
export class JobTable {
  readonly jobs = input.required<JobSummary[]>();
  readonly now = input.required<number>();
  readonly showRetry = input(false);
  readonly busy = input<ReadonlySet<string>>(new Set());
  readonly retry = output<JobSummary>();
}
