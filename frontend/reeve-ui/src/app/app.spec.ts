import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { App } from './app';
import { routes } from './app.routes';
import { adminGuard, signedInGuard } from './core/auth-guards';
import { Shell } from './layout/shell';

describe('App', () => {
  const shell = routes.find((r) => r.component === Shell)!;

  it('creates the root component', () => {
    TestBed.configureTestingModule({ providers: [provideRouter([])] });
    expect(TestBed.createComponent(App).componentInstance).toBeTruthy();
  });

  it('lazy-loads every dashboard page under the shell', () => {
    const pages = shell.children!.filter((r) => r.loadComponent).map((r) => r.path);
    expect(pages).toEqual(['', 'jobs', 'jobs/:id', 'failures', 'queues', 'workers', 'schedules', 'audit']);
  });

  it('puts everything except sign-in behind authentication, and the audit log behind the admin role', () => {
    expect(routes.find((r) => r.path === 'login')?.canActivate).toBeUndefined();
    expect(shell.canActivate).toEqual([signedInGuard]);
    expect(shell.children!.find((r) => r.path === 'audit')?.canActivate).toEqual([adminGuard]);
  });
});
