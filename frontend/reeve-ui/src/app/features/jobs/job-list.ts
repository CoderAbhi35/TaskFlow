import { ChangeDetectionStrategy, Component, computed, inject, input, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { Router } from '@angular/router';
import { ReeveApi } from '../../core/api';
import { AuthService } from '../../core/auth';
import { JOB_PRIORITIES, JOB_STATUSES, JobFilters, JobPriority, JobStatus } from '../../core/models';
import { autoRefresh, nowSignal } from '../../core/refresh';
import { HumanizePipe } from '../../shared/format';
import { LoadState } from '../../shared/load-state';
import { JobSubmit } from './job-submit';
import { jobPager } from './job-pager';
import { JobTable } from './job-table';

const PAGE_SIZE = 25;

/** Server-side filtered, cursor-paged job search. Filters live in the URL, so any view can be linked. */
@Component({
  selector: 'app-job-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, LoadState, JobTable, JobSubmit, HumanizePipe],
  template: `
    <header class="page-header">
      <h1>Jobs</h1>
      @if (auth.canOperate()) {
        <button type="button" class="btn btn-primary" (click)="showSubmit.set(!showSubmit())" [attr.aria-expanded]="showSubmit()">
          {{ showSubmit() ? 'Close' : 'Submit job' }}
        </button>
      }
    </header>

    @if (showSubmit()) {
      <app-job-submit [jobTypes]="jobTypes()" (created)="onCreated($event.id)" />
    }

    <form class="card toolbar" (ngSubmit)="apply()" aria-label="Filters">
      <div class="field">
        <label for="f-status">Status</label>
        <select id="f-status" name="status" multiple [(ngModel)]="draftStatus">
          @for (s of statuses; track s) { <option [value]="s">{{ s | humanize }}</option> }
        </select>
      </div>
      <div class="field">
        <label for="f-type">Job type</label>
        <select id="f-type" name="jobType" [(ngModel)]="draftType">
          <option value="">Any</option>
          @for (t of jobTypes(); track t) { <option [value]="t">{{ t }}</option> }
        </select>
      </div>
      <div class="field">
        <label for="f-priority">Priority</label>
        <select id="f-priority" name="priority" [(ngModel)]="draftPriority">
          <option value="">Any</option>
          @for (p of priorities; track p) { <option [value]="p">{{ p | humanize }}</option> }
        </select>
      </div>
      <button type="submit" class="btn btn-primary">Apply</button>
      <button type="button" class="btn" (click)="clear()">Clear</button>
    </form>

    <section class="card">
      <app-load-state what="jobs" [loading]="pager.firstPage.isLoading()" [hasData]="pager.firstPage.hasValue()"
                      [error]="pager.firstPage.error()" (retry)="pager.firstPage.reload()"
                      [empty]="pager.items().length === 0" emptyText="No jobs match these filters.">
        <app-job-table [jobs]="pager.items()" [now]="now()" />
        <div class="toolbar" style="margin-top: 0.75rem">
          <span class="muted">{{ pager.items().length }} shown</span>
          @if (pager.nextCursor()) {
            <button type="button" class="btn" (click)="pager.loadMore()" [disabled]="pager.loadingMore()">
              {{ pager.loadingMore() ? 'Loading…' : 'Load more' }}
            </button>
          }
        </div>
      </app-load-state>
    </section>
  `,
})
export class JobList {
  private readonly api = inject(ReeveApi);
  private readonly router = inject(Router);
  protected readonly auth = inject(AuthService);

  // Bound from the query string (withComponentInputBinding). Repeated params arrive as arrays.
  readonly status = input<string | string[]>();
  readonly jobType = input<string>();
  readonly priority = input<string>();

  protected readonly statuses = JOB_STATUSES;
  protected readonly priorities = JOB_PRIORITIES;
  protected readonly showSubmit = signal(false);
  protected readonly now = nowSignal(5000);

  protected readonly filters = computed<JobFilters>(() => ({
    status: toArray(this.status()).filter((s): s is JobStatus => (JOB_STATUSES as readonly string[]).includes(s)),
    jobType: this.jobType() || undefined,
    priority: (JOB_PRIORITIES as readonly string[]).includes(this.priority() ?? '') ? (this.priority() as JobPriority) : undefined,
    limit: PAGE_SIZE,
  }));

  protected readonly pager = jobPager(this.filters);
  private readonly queues = this.api.queues();
  protected readonly jobTypes = computed(() => (this.queues.value() ?? []).map((q) => q.jobType));

  // Form drafts, applied to the URL on submit.
  protected draftStatus: string[] = [];
  protected draftType = '';
  protected draftPriority = '';

  constructor() {
    autoRefresh(10_000, this.pager);
  }

  ngOnInit(): void {
    this.draftStatus = toArray(this.status());
    this.draftType = this.jobType() ?? '';
    this.draftPriority = this.priority() ?? '';
  }

  protected apply(): void {
    void this.router.navigate([], {
      queryParams: {
        status: this.draftStatus.length ? this.draftStatus : null,
        jobType: this.draftType || null,
        priority: this.draftPriority || null,
      },
    });
  }

  protected clear(): void {
    this.draftStatus = [];
    this.draftType = '';
    this.draftPriority = '';
    this.apply();
  }

  protected onCreated(id: string): void {
    void this.router.navigate(['/jobs', id]);
  }
}

function toArray(value: string | string[] | undefined): string[] {
  return value == null ? [] : Array.isArray(value) ? value : [value];
}
