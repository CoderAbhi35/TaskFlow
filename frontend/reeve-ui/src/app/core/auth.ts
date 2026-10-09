import { HttpClient } from '@angular/common/http';
import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { Observable, tap } from 'rxjs';
import { API_BASE } from './api';

export type Role = 'viewer' | 'operator' | 'admin';

export interface Session {
  token: string;
  username: string;
  roles: Role[];
  expiresAt: number; // epoch ms
}

interface TokenResponse {
  accessToken: string;
  expiresAt: string;
  username: string;
  roles: Role[];
}

const STORAGE_KEY = 'reeve.session';

/**
 * Holds the signed-in session. The token lives in sessionStorage: it survives a reload but not
 * closing the tab, and is not shared between tabs. The server enforces every permission; the
 * role helpers here only decide what the UI offers.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly http = inject(HttpClient);
  private readonly session = signal<Session | null>(restore());
  private expiryTimer: ReturnType<typeof setTimeout> | undefined;

  readonly current = this.session.asReadonly();
  readonly isSignedIn = computed(() => this.session() !== null);
  readonly username = computed(() => this.session()?.username ?? '');
  /** Highest role, for display. */
  readonly role = computed<Role | null>(() => {
    const roles = this.session()?.roles ?? [];
    return roles.includes('admin') ? 'admin' : roles.includes('operator') ? 'operator' : roles.includes('viewer') ? 'viewer' : null;
  });
  /** Submit, cancel and retry jobs; create, pause and resume schedules. */
  readonly canOperate = computed(() => this.role() === 'operator' || this.role() === 'admin');
  /** Delete schedules, read the audit log. */
  readonly isAdmin = computed(() => this.role() === 'admin');

  constructor() {
    this.scheduleExpiry();
    inject(DestroyRef).onDestroy(() => clearTimeout(this.expiryTimer));
  }

  token(): string | null {
    const s = this.session();
    return s && s.expiresAt > Date.now() ? s.token : null;
  }

  signIn(username: string, password: string): Observable<TokenResponse> {
    return this.http.post<TokenResponse>(`${API_BASE}/auth/token`, { username, password }).pipe(
      tap((response) => {
        const session: Session = {
          token: response.accessToken,
          username: response.username,
          roles: response.roles,
          expiresAt: new Date(response.expiresAt).getTime(),
        };
        persist(session);
        this.session.set(session);
        this.scheduleExpiry();
      }),
    );
  }

  signOut(): void {
    persist(null);
    this.session.set(null);
    clearTimeout(this.expiryTimer);
  }

  private scheduleExpiry(): void {
    clearTimeout(this.expiryTimer);
    const s = this.session();
    if (!s) return;
    // setTimeout overflows above ~24.8 days; tokens are far shorter, but stay safe.
    const delay = Math.min(s.expiresAt - Date.now(), 2 ** 31 - 1);
    this.expiryTimer = setTimeout(() => this.signOut(), Math.max(0, delay));
  }
}

function restore(): Session | null {
  try {
    const raw = sessionStorage.getItem(STORAGE_KEY);
    const session = raw ? (JSON.parse(raw) as Session) : null;
    if (!session || session.expiresAt <= Date.now()) {
      sessionStorage.removeItem(STORAGE_KEY);
      return null;
    }
    return session;
  } catch {
    return null;
  }
}

function persist(session: Session | null): void {
  try {
    if (session) sessionStorage.setItem(STORAGE_KEY, JSON.stringify(session));
    else sessionStorage.removeItem(STORAGE_KEY);
  } catch {
    // Storage can be unavailable (private mode, blocked); the session then lasts until reload.
  }
}
