import { DestroyRef, inject, signal } from '@angular/core';

interface Reloadable {
  reload(): boolean;
}

/**
 * Reloads resources on an interval while the tab is visible, and once immediately when it becomes
 * visible again. A dashboard left open in a background tab then costs the API nothing.
 * Call from an injection context (a component field initialiser).
 */
export function autoRefresh(intervalMs: number, ...resources: Reloadable[]): void {
  const destroyRef = inject(DestroyRef);
  const reloadAll = () => resources.forEach((r) => r.reload());

  const timer = setInterval(() => {
    if (document.visibilityState === 'visible') reloadAll();
  }, intervalMs);
  const onVisible = () => {
    if (document.visibilityState === 'visible') reloadAll();
  };
  document.addEventListener('visibilitychange', onVisible);

  destroyRef.onDestroy(() => {
    clearInterval(timer);
    document.removeEventListener('visibilitychange', onVisible);
  });
}

/** A clock signal for relative times ("12 s ago") that ticks while the component is alive. */
export function nowSignal(tickMs = 1000) {
  const now = signal(Date.now());
  const timer = setInterval(() => now.set(Date.now()), tickMs);
  inject(DestroyRef).onDestroy(() => clearInterval(timer));
  return now.asReadonly();
}
