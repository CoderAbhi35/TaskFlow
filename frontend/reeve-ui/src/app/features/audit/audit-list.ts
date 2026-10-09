import { HttpClient, HttpParams } from '@angular/common/http';
import { ChangeDetectionStrategy, Component, computed, inject, linkedSignal, signal } from '@angular/core';
import { rxResource } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { RouterLink } from '@angular/router';
import { API_BASE } from '../../core/api';
import { AuditEvent, Page } from '../../core/models';
import { nowSignal } from '../../core/refresh';
import { safeResource } from '../../core/safe-resource';
import { RelativeTimePipe } from '../../shared/format';
import { LoadState } from '../../shared/load-state';

interface AuditFilters {
  actor: string;
  action: string;
  entityId: string;
}

/** Who did what, newest first. Admins only (enforced by the API; the route is guarded too). */
@Component({
  selector: 'app-audit-list',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [FormsModule, RouterLink, LoadState, RelativeTimePipe],
  template: `
    <header class="page-header"><h1>Audit log</h1></header>

    <form class="card toolbar" (ngSubmit)="filters.set({ actor: actor, action: action, entityId: entityId })" aria-label="Filters">
      <div class="field"><label for="a-actor">Actor</label><input id="a-actor" name="actor" [(ngModel)]="actor" /></div>
      <div class="field">
        <label for="a-action">Action</label>
        <select id="a-action" name="action" [(ngModel)]="action">
          <option value="">Any</option>
          @for (a of actions; track a) { <option [value]="a">{{ a }}</option> }
        </select>
      </div>
      <div class="field"><label for="a-entity">Entity ID</label><input id="a-entity" name="entityId" [(ngModel)]="entityId" class="mono" /></div>
      <button type="submit" class="btn btn-primary">Apply</button>
    </form>

    <section class="card">
      <app-load-state what="the audit log" [loading]="page.isLoading()" [hasData]="page.hasValue()"
                      [error]="page.error()" (retry)="page.reload()" [empty]="items().length === 0" emptyText="No matching events.">
        <div class="table-wrap">
          <table class="table">
            <thead><tr><th>When</th><th>Actor</th><th>Action</th><th>Entity</th><th>Details</th><th>Correlation ID</th></tr></thead>
            <tbody>
              @for (e of items(); track e.id) {
                <tr>
                  <td [title]="e.occurredAt">{{ e.occurredAt | relative: now() }}</td>
                  <td>{{ e.actor }}</td>
                  <td><code>{{ e.action }}</code></td>
                  <td>
                    @if (e.entityType === 'job') { <a [routerLink]="['/jobs', e.entityId]">job</a> } @else { {{ e.entityType }} }
                    <br /><code class="muted">{{ e.entityId }}</code>
                  </td>
                  <td><code class="muted">{{ e.details ? stringify(e.details) : '' }}</code></td>
                  <td><code class="muted">{{ e.correlationId }}</code></td>
                </tr>
              }
            </tbody>
          </table>
        </div>
        @if (nextCursor()) {
          <button type="button" class="btn" style="margin-top: 0.75rem" (click)="loadMore()">Load more</button>
        }
      </app-load-state>
    </section>
  `,
})
export class AuditList {
  private readonly http = inject(HttpClient);

  protected readonly actions = [
    'job.created', 'job.cancelled', 'job.retried',
    'schedule.created', 'schedule.paused', 'schedule.resumed', 'schedule.deleted',
    'auth.token_issued', 'auth.login_failed',
  ];
  protected actor = '';
  protected action = '';
  protected entityId = '';
  protected readonly now = nowSignal(5000);
  protected readonly filters = signal<AuditFilters>({ actor: '', action: '', entityId: '' });

  protected readonly page = safeResource(rxResource({
    params: () => this.filters(),
    stream: ({ params }) => this.search(params),
  }));
  private readonly more = linkedSignal<AuditFilters, Page<AuditEvent>[]>({ source: this.filters, computation: () => [] });

  protected readonly items = computed(() => [...(this.page.value()?.items ?? []), ...this.more().flatMap((p) => p.items)]);
  protected readonly nextCursor = computed(() => {
    const more = this.more();
    return more.length ? more[more.length - 1].nextCursor : (this.page.value()?.nextCursor ?? null);
  });

  protected loadMore(): void {
    const cursor = this.nextCursor();
    if (cursor) this.search(this.filters(), cursor).subscribe((p) => this.more.update((pages) => [...pages, p]));
  }

  protected stringify(value: unknown): string {
    return JSON.stringify(value);
  }

  private search(f: AuditFilters, cursor?: string) {
    let params = new HttpParams().set('limit', 50);
    if (f.actor) params = params.set('actor', f.actor);
    if (f.action) params = params.set('action', f.action);
    if (f.entityId) params = params.set('entityId', f.entityId);
    if (cursor) params = params.set('cursor', cursor);
    return this.http.get<Page<AuditEvent>>(`${API_BASE}/audit`, { params });
  }
}
