import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { humanize } from './format';

type Tone = 'neutral' | 'info' | 'progress' | 'success' | 'warning' | 'danger';

const TONES: Record<string, Tone> = {
  PENDING: 'neutral',
  QUEUED: 'info',
  RUNNING: 'progress',
  SUCCEEDED: 'success',
  FAILED: 'danger',
  DEAD_LETTERED: 'danger',
  CANCELLED: 'neutral',
  ACTIVE: 'success',
  DRAINING: 'warning',
  OFFLINE: 'neutral',
  STALE: 'danger',
  PAUSED: 'neutral',
  ENABLED: 'success',
  HEALTHY: 'success',
  DEGRADED: 'warning',
  UNHEALTHY: 'danger',
  LOW: 'neutral',
  NORMAL: 'info',
  HIGH: 'warning',
  CRITICAL: 'danger',
};

/** A coloured pill for job, attempt, worker, schedule and health states. Colour is never the only cue: the text is always shown. */
@Component({
  selector: 'app-status-badge',
  changeDetection: ChangeDetectionStrategy.OnPush,
  host: { class: 'badge', '[attr.data-tone]': 'tone()' },
  template: `{{ label() }}`,
})
export class StatusBadge {
  readonly status = input.required<string>();

  protected readonly tone = computed<Tone>(() => TONES[this.status().toUpperCase()] ?? 'neutral');
  protected readonly label = computed(() => humanize(this.status()));
}
