import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ReeveApi } from '../core/api';
import { AuthService } from '../core/auth';
import { autoRefresh } from '../core/refresh';
import { StatusBadge } from '../shared/status-badge';

interface NavItem {
  path: string;
  label: string;
  adminOnly?: boolean;
}

@Component({
  selector: 'app-shell',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [RouterOutlet, RouterLink, RouterLinkActive, StatusBadge],
  template: `
    <a class="skip-link" href="#main">Skip to content</a>
    <div class="shell">
      <aside class="sidebar">
        <div class="brand">
          <span class="brand-mark" aria-hidden="true">▣</span>
          <span>Reeve</span>
        </div>
        <nav aria-label="Main">
          @for (item of visibleNav(); track item.path) {
            <a [routerLink]="item.path" routerLinkActive="active" [routerLinkActiveOptions]="{ exact: item.path === '/' }">
              {{ item.label }}
            </a>
          }
        </nav>
        <div class="sidebar-footer">
          <div class="user" aria-label="Signed in user">
            <span>{{ auth.username() }}</span>
            <app-status-badge [status]="auth.role() ?? 'unknown'" />
          </div>
          <div class="footer-row" aria-live="polite">
            <span class="muted">API</span>
            <app-status-badge [status]="healthStatus()" />
          </div>
          <button type="button" class="btn btn-small" (click)="signOut()">Sign out</button>
        </div>
      </aside>
      <main id="main" class="content">
        <router-outlet />
      </main>
    </div>
  `,
})
export class Shell {
  protected readonly nav: NavItem[] = [
    { path: '/', label: 'Overview' },
    { path: '/jobs', label: 'Jobs' },
    { path: '/failures', label: 'Failures' },
    { path: '/queues', label: 'Queues' },
    { path: '/workers', label: 'Workers' },
    { path: '/schedules', label: 'Schedules' },
    { path: '/audit', label: 'Audit', adminOnly: true },
  ];

  protected readonly auth = inject(AuthService);
  private readonly router = inject(Router);
  protected readonly visibleNav = computed(() => this.nav.filter((item) => !item.adminOnly || this.auth.isAdmin()));

  private readonly health = inject(ReeveApi).health();

  /** Healthy / Degraded come back as 200 text; Unhealthy is a 503 and lands in error(). */
  protected readonly healthStatus = computed(() => {
    if (this.health.error()) return 'UNHEALTHY';
    return this.health.value()?.trim().toUpperCase() || 'UNKNOWN';
  });

  constructor() {
    autoRefresh(10_000, this.health);
  }

  protected signOut(): void {
    this.auth.signOut();
    void this.router.navigate(['/login']);
  }
}
