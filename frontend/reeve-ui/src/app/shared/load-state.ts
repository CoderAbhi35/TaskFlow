import { ChangeDetectionStrategy, Component, computed, input, output } from '@angular/core';
import { describeError } from '../core/problem';

/**
 * Wraps a data region with consistent loading, error and empty states. The projected content is
 * shown once there is data; while a background refresh is running, it stays visible.
 */
@Component({
  selector: 'app-load-state',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (error()) {
      <div class="notice notice-error" role="alert">
        <strong>Could not load {{ what() }}.</strong>
        <span>{{ view()?.message }}</span>
        @if (view()?.correlationId; as id) {
          <span class="muted">Correlation ID: <code>{{ id }}</code></span>
        }
        <button type="button" class="btn btn-small" (click)="retry.emit()">Try again</button>
      </div>
    } @else if (loading() && !hasData()) {
      <div class="notice muted" aria-busy="true">Loading {{ what() }}…</div>
    } @else if (empty()) {
      <div class="notice muted">{{ emptyText() }}</div>
    } @else {
      <ng-content />
    }
  `,
})
export class LoadState {
  readonly what = input('data');
  readonly loading = input(false);
  readonly hasData = input(false);
  readonly error = input<unknown>(null);
  readonly empty = input(false);
  readonly emptyText = input('Nothing here yet.');
  readonly retry = output<void>();

  protected readonly view = computed(() => (this.error() ? describeError(this.error()) : null));
}
