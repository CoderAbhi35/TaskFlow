import { ChangeDetectionStrategy, Component, effect, inject, input, output, signal } from '@angular/core';
import { AbstractControl, FormBuilder, ReactiveFormsModule, ValidationErrors, Validators } from '@angular/forms';
import { ReeveApi } from '../../core/api';
import { JOB_PRIORITIES, Job, JobPriority } from '../../core/models';
import { ErrorView, describeError } from '../../core/problem';
import { HumanizePipe } from '../../shared/format';

/** Accepts only a JSON object, matching the API's payload rule, so mistakes are caught before sending. */
export function jsonObjectValidator(control: AbstractControl<string>): ValidationErrors | null {
  const text = (control.value ?? '').trim();
  if (!text) return null;
  try {
    const parsed = JSON.parse(text);
    return parsed !== null && typeof parsed === 'object' && !Array.isArray(parsed) ? null : { jsonObject: 'Payload must be a JSON object.' };
  } catch (e) {
    return { jsonObject: `Invalid JSON: ${(e as Error).message}` };
  }
}

@Component({
  selector: 'app-job-submit',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule, HumanizePipe],
  template: `
    <form class="card grid" [formGroup]="form" (ngSubmit)="submit()" aria-label="Submit a job">
      <h2>Submit a job</h2>
      <div class="toolbar">
        <div class="field">
          <label for="s-type">Job type</label>
          <select id="s-type" formControlName="jobType" required>
            @for (t of jobTypes(); track t) { <option [value]="t">{{ t }}</option> }
          </select>
        </div>
        <div class="field">
          <label for="s-priority">Priority</label>
          <select id="s-priority" formControlName="priority">
            @for (p of priorities; track p) { <option [value]="p">{{ p | humanize }}</option> }
          </select>
        </div>
      </div>
      <div class="field">
        <label for="s-payload">Payload (JSON object)</label>
        <textarea id="s-payload" formControlName="payload" spellcheck="false"></textarea>
        @if (form.controls.payload.errors?.['jsonObject']; as message) {
          <span class="field-error">{{ message }}</span>
        }
        @for (message of error()?.fields?.['payload'] ?? []; track message) {
          <span class="field-error">{{ message }}</span>
        }
      </div>
      @if (error(); as e) {
        <div class="notice notice-error" role="alert">
          {{ e.message }}
          @for (entry of fieldErrors(e); track entry) { <span>{{ entry }}</span> }
        </div>
      }
      <div class="toolbar">
        <button type="submit" class="btn btn-primary" [disabled]="form.invalid || submitting()">
          {{ submitting() ? 'Submitting…' : 'Submit' }}
        </button>
        <span class="muted">Tip: add <code>"simulate": {{ '{' }} "failure": "transient", "failAttempts": 1 {{ '}' }}</code> to see a retry.</span>
      </div>
    </form>
  `,
})
export class JobSubmit {
  private readonly api = inject(ReeveApi);

  readonly jobTypes = input.required<string[]>();
  readonly created = output<Job>();

  protected readonly priorities = JOB_PRIORITIES;
  protected readonly submitting = signal(false);
  protected readonly error = signal<ErrorView | null>(null);

  protected readonly form = inject(FormBuilder).nonNullable.group({
    jobType: ['', Validators.required],
    priority: ['NORMAL' as JobPriority],
    payload: ['{\n  "customerId": 42\n}', jsonObjectValidator],
  });

  constructor() {
    // Preselect the first job type once the list has loaded.
    effect(() => {
      const first = this.jobTypes()[0];
      if (first && !this.form.controls.jobType.value) this.form.controls.jobType.setValue(first);
    });
  }

  protected submit(): void {
    if (this.form.invalid) return;
    const { jobType, priority, payload } = this.form.getRawValue();
    this.submitting.set(true);
    this.error.set(null);
    this.api.createJob({ jobType, priority, payload: payload.trim() ? JSON.parse(payload) : undefined }).subscribe({
      next: (job) => {
        this.submitting.set(false);
        this.created.emit(job);
      },
      error: (e) => {
        this.submitting.set(false);
        this.error.set(describeError(e));
      },
    });
  }

  protected fieldErrors(e: ErrorView): string[] {
    return Object.entries(e.fields)
      .filter(([field]) => field !== 'payload')
      .flatMap(([field, messages]) => messages.map((m) => `${field}: ${m}`));
  }
}
