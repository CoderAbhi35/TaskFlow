import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { Observable } from 'rxjs';
import { ReeveApi } from '../../core/api';
import { AuthService } from '../../core/auth';
import { JOB_PRIORITIES, JobPriority, Schedule } from '../../core/models';
import { ErrorView, describeError } from '../../core/problem';
import { autoRefresh, nowSignal } from '../../core/refresh';
import { HumanizePipe, RelativeTimePipe } from '../../shared/format';
import { LoadState } from '../../shared/load-state';
import { StatusBadge } from '../../shared/status-badge';
import { jsonObjectValidator } from '../jobs/job-submit';

@Component({
  selector: 'app-schedule-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, RouterLink, LoadState, StatusBadge, RelativeTimePipe, HumanizePipe],
  template: `
    <header class="page-header">
      <h1>Schedules</h1>
      @if (auth.canOperate()) {
        <button type="button" class="btn btn-primary" (click)="showForm.set(!showForm())" [attr.aria-expanded]="showForm()">
          {{ showForm() ? 'Close' : 'New schedule' }}
        </button>
      }
    </header>

    @if (showForm()) {
      <form class="card grid" [formGroup]="form" (ngSubmit)="create()" aria-label="New schedule">
        <h2>New schedule</h2>
        <div class="toolbar">
          <div class="field">
            <label for="n-name">Name</label>
            <input id="n-name" formControlName="name" placeholder="Nightly customer reports" />
            @for (m of formError()?.fields?.['name'] ?? []; track m) { <span class="field-error">{{ m }}</span> }
          </div>
          <div class="field">
            <label for="n-type">Job type</label>
            <select id="n-type" formControlName="jobType">
              @for (t of jobTypes(); track t) { <option [value]="t">{{ t }}</option> }
            </select>
          </div>
          <div class="field">
            <label for="n-cron">Cron (5 fields)</label>
            <input id="n-cron" formControlName="cronExpression" class="mono" placeholder="0 2 * * *" />
            @for (m of formError()?.fields?.['cronExpression'] ?? []; track m) { <span class="field-error">{{ m }}</span> }
          </div>
          <div class="field">
            <label for="n-tz">Time zone</label>
            <input id="n-tz" formControlName="timeZone" />
            @for (m of formError()?.fields?.['timeZone'] ?? []; track m) { <span class="field-error">{{ m }}</span> }
          </div>
          <div class="field">
            <label for="n-priority">Priority</label>
            <select id="n-priority" formControlName="priority">
              @for (p of priorities; track p) { <option [value]="p">{{ p | humanize }}</option> }
            </select>
          </div>
        </div>
        <div class="field">
          <label for="n-payload">Payload (JSON object)</label>
          <textarea id="n-payload" formControlName="payload" spellcheck="false"></textarea>
          @if (form.controls.payload.errors?.['jsonObject']; as m) { <span class="field-error">{{ m }}</span> }
        </div>
        @if (formError(); as e) { <div class="notice notice-error" role="alert">{{ e.message }}</div> }
        <div class="toolbar">
          <button type="submit" class="btn btn-primary" [disabled]="form.invalid || saving()">{{ saving() ? 'Creating…' : 'Create schedule' }}</button>
          <span class="muted">Examples: <code>*/5 * * * *</code> every 5 minutes · <code>0 9 * * MON-FRI</code> weekdays at 09:00</span>
        </div>
      </form>
    }

    @if (actionError(); as e) { <div class="notice notice-error" role="alert">{{ e.message }}</div> }

    <section class="card">
      <app-load-state what="schedules" [loading]="schedules.isLoading()" [hasData]="schedules.hasValue()"
                      [error]="schedules.error()" (retry)="schedules.reload()"
                      [empty]="(schedules.value()?.length ?? 0) === 0" emptyText="No schedules yet.">
        <div class="table-wrap">
          <table class="table">
            <thead>
              <tr><th>Name</th><th>Job type</th><th>When</th><th>State</th><th>Next run</th><th>Last run</th><th><span class="muted">Actions</span></th></tr>
            </thead>
            <tbody>
              @for (s of schedules.value(); track s.id) {
                <tr>
                  <td>{{ s.name }}</td>
                  <td>{{ s.jobType }}</td>
                  <td><code>{{ s.cronExpression }}</code><br /><span class="muted">{{ s.timeZone }}</span></td>
                  <td><app-status-badge [status]="s.enabled ? 'ENABLED' : 'PAUSED'" /></td>
                  <td [title]="s.nextRunAt ?? ''">{{ s.enabled ? (s.nextRunAt | relative: now()) : '—' }}</td>
                  <td>
                    @if (s.lastJobId) {
                      <a [routerLink]="['/jobs', s.lastJobId]" [title]="s.lastRunAt ?? ''">{{ s.lastRunAt | relative: now() }}</a>
                    } @else { <span class="muted">never</span> }
                  </td>
                  <td class="toolbar">
                    @if (!auth.canOperate()) { <span class="muted">—</span> }
                    @else if (s.enabled) {
                      <button type="button" class="btn btn-small" [disabled]="busy()" (click)="act(api.pauseSchedule(s.id))">Pause</button>
                    } @else {
                      <button type="button" class="btn btn-small" [disabled]="busy()" (click)="act(api.resumeSchedule(s.id))">Resume</button>
                    }
                    @if (auth.isAdmin()) {
                      <button type="button" class="btn btn-small btn-danger" [disabled]="busy()" (click)="remove(s)">Delete</button>
                    }
                  </td>
                </tr>
              }
            </tbody>
          </table>
        </div>
      </app-load-state>
    </section>
  `,
})
export class ScheduleList {
  protected readonly api = inject(ReeveApi);
  protected readonly auth = inject(AuthService);

  protected readonly schedules = this.api.schedules();
  private readonly queues = this.api.queues();
  protected readonly jobTypes = computed(() => (this.queues.value() ?? []).filter((q) => q.enabled).map((q) => q.jobType));
  protected readonly priorities = JOB_PRIORITIES;
  protected readonly now = nowSignal(1000);

  protected readonly showForm = signal(false);
  protected readonly saving = signal(false);
  protected readonly busy = signal(false);
  protected readonly formError = signal<ErrorView | null>(null);
  protected readonly actionError = signal<ErrorView | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    name: ['', [Validators.required, Validators.maxLength(100)]],
    jobType: ['GENERATE_REPORT', Validators.required],
    cronExpression: ['0 2 * * *', Validators.required],
    // Default to the operator's own time zone rather than UTC.
    timeZone: [Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC', Validators.required],
    priority: ['NORMAL' as JobPriority],
    payload: ['{\n  "customerId": 42\n}', jsonObjectValidator],
  });

  constructor() {
    autoRefresh(10_000, this.schedules);
  }

  protected create(): void {
    if (this.form.invalid) return;
    const { payload, ...rest } = this.form.getRawValue();
    this.saving.set(true);
    this.formError.set(null);
    this.api.createSchedule({ ...rest, payload: payload.trim() ? JSON.parse(payload) : undefined }).subscribe({
      next: () => {
        this.saving.set(false);
        this.showForm.set(false);
        this.form.controls.name.reset();
        this.schedules.reload();
      },
      error: (e) => {
        this.saving.set(false);
        this.formError.set(describeError(e));
      },
    });
  }

  protected remove(schedule: Schedule): void {
    if (confirm(`Delete schedule "${schedule.name}"? Jobs it already created are kept.`)) {
      this.act(this.api.deleteSchedule(schedule.id));
    }
  }

  protected act(action: Observable<unknown>): void {
    this.busy.set(true);
    this.actionError.set(null);
    action.subscribe({
      next: () => {
        this.busy.set(false);
        this.schedules.reload();
      },
      error: (e) => {
        this.busy.set(false);
        this.actionError.set(describeError(e));
      },
    });
  }
}
