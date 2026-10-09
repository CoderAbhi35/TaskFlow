import { ChangeDetectionStrategy, Component, computed, inject, signal } from '@angular/core';
import { ReeveApi } from '../../core/api';
import { autoRefresh, nowSignal } from '../../core/refresh';
import { RelativeTimePipe } from '../../shared/format';
import { LoadState } from '../../shared/load-state';
import { StatusBadge } from '../../shared/status-badge';

/** Registered workers with their heartbeat health. A stale worker has missed its heartbeats. */
@Component({
  selector: 'app-worker-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [LoadState, StatusBadge, RelativeTimePipe],
  template: `
    <header class="page-header">
      <h1>Workers</h1>
      <label class="toolbar"><input type="checkbox" [checked]="showOffline()" (change)="showOffline.set(!showOffline())" /> Show offline</label>
    </header>

    <section class="card">
      <app-load-state what="workers" [loading]="workers.isLoading()" [hasData]="workers.hasValue()"
                      [error]="workers.error()" (retry)="workers.reload()" [empty]="visible().length === 0"
                      emptyText="No workers are running. Start one with: dotnet run --project src/Reeve.Worker">
        <div class="table-wrap">
          <table class="table">
            <thead>
              <tr><th>Worker</th><th>Status</th><th>Last heartbeat</th><th class="num">Concurrency</th><th>Job types</th><th>Registered</th></tr>
            </thead>
            <tbody>
              @for (w of visible(); track w.id) {
                <tr>
                  <td><code>{{ w.id }}</code><br /><span class="muted">{{ w.hostname }}</span></td>
                  <td>
                    <app-status-badge [status]="w.status" />
                    @if (w.isStale && w.status !== 'OFFLINE') { <app-status-badge status="STALE" /> }
                  </td>
                  <td [title]="w.lastHeartbeatAt">{{ w.lastHeartbeatAt | relative: now() }}</td>
                  <td class="num">{{ w.concurrency }}</td>
                  <td>{{ w.supportedJobTypes.join(', ') }}</td>
                  <td [title]="w.registeredAt">{{ w.registeredAt | relative: now() }}</td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        <p class="muted">{{ offlineCount() }} offline worker(s) {{ showOffline() ? 'shown' : 'hidden' }}.</p>
      </app-load-state>
    </section>
  `,
})
export class WorkerList {
  protected readonly workers = inject(ReeveApi).workers();
  protected readonly now = nowSignal(1000);
  protected readonly showOffline = signal(false);

  protected readonly visible = computed(() =>
    (this.workers.value() ?? []).filter((w) => this.showOffline() || w.status !== 'OFFLINE'),
  );
  protected readonly offlineCount = computed(() => (this.workers.value() ?? []).filter((w) => w.status === 'OFFLINE').length);

  constructor() {
    autoRefresh(5000, this.workers);
  }
}
