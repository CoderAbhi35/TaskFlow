import { ChangeDetectionStrategy, Component, inject, input, isDevMode, signal } from '@angular/core';
import { FormBuilder, ReactiveFormsModule, Validators } from '@angular/forms';
import { Router } from '@angular/router';
import { AuthService } from '../../core/auth';
import { ErrorView, describeError } from '../../core/problem';

@Component({
  selector: 'app-login',
  changeDetection: ChangeDetectionStrategy.OnPush,
  imports: [ReactiveFormsModule],
  template: `
    <main class="login">
      <form class="card grid" [formGroup]="form" (ngSubmit)="submit()" aria-labelledby="login-title">
        <div class="brand"><span class="brand-mark" aria-hidden="true">▣</span><span id="login-title">Reeve</span></div>
        @if (expired()) {
          <div class="notice" role="status">Your session ended. Please sign in again.</div>
        }
        <div class="field">
          <label for="l-user">Username</label>
          <input id="l-user" formControlName="username" autocomplete="username" required />
        </div>
        <div class="field">
          <label for="l-pass">Password</label>
          <input id="l-pass" type="password" formControlName="password" autocomplete="current-password" required />
        </div>
        @if (error(); as e) {
          <div class="notice notice-error" role="alert">{{ e.message }}</div>
        }
        <button type="submit" class="btn btn-primary" [disabled]="form.invalid || busy()">{{ busy() ? 'Signing in…' : 'Sign in' }}</button>
        @if (devMode) {
          <p class="muted">Development users: <code>viewer</code>, <code>operator</code>, <code>admin</code> (password = username).</p>
        }
      </form>
    </main>
  `,
  styles: `
    .login { min-height: 100vh; display: grid; place-items: center; padding: 1rem; }
    form { width: min(360px, 100%); }
  `,
})
export class Login {
  private readonly auth = inject(AuthService);
  private readonly router = inject(Router);

  readonly returnUrl = input<string>();
  readonly expired = input<string>();

  protected readonly devMode = isDevMode();
  protected readonly busy = signal(false);
  protected readonly error = signal<ErrorView | null>(null);
  protected readonly form = inject(FormBuilder).nonNullable.group({
    username: ['', Validators.required],
    password: ['', Validators.required],
  });

  protected submit(): void {
    const { username, password } = this.form.getRawValue();
    this.busy.set(true);
    this.error.set(null);
    this.auth.signIn(username, password).subscribe({
      next: () => void this.router.navigateByUrl(this.safeReturnUrl()),
      error: (e) => {
        this.busy.set(false);
        this.error.set(describeError(e));
      },
    });
  }

  /** Only follow in-app paths, so a crafted link cannot bounce the user to another site after sign-in. */
  private safeReturnUrl(): string {
    const url = this.returnUrl();
    return url && url.startsWith('/') && !url.startsWith('//') && !url.startsWith('/login') ? url : '/';
  }
}
