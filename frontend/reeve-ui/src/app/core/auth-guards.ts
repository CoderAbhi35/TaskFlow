import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { API_BASE } from './api';
import { AuthService } from './auth';

const TOKEN_URL = `${API_BASE}/auth/token`;

/**
 * Adds the bearer token to API calls. A 401 means the token was rejected or has expired: sign out
 * and send the user to the sign-in page, returning them to where they were afterwards.
 */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);
  const router = inject(Router);

  const token = auth.token();
  const isApi = request.url.startsWith('/api/') && request.url !== TOKEN_URL;
  const outgoing = isApi && token ? request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }) : request;

  return next(outgoing).pipe(
    catchError((error: unknown) => {
      if (isApi && error instanceof HttpErrorResponse && error.status === 401) {
        auth.signOut();
        void router.navigate(['/login'], { queryParams: { returnUrl: router.url, expired: token ? 1 : null } });
      }
      return throwError(() => error);
    }),
  );
};

/** Pages behind sign-in. */
export const signedInGuard: CanActivateFn = (_, state) =>
  inject(AuthService).isSignedIn() || inject(Router).createUrlTree(['/login'], { queryParams: { returnUrl: state.url } });

/** Admin-only pages; others go back to the overview (the server would refuse them anyway). */
export const adminGuard: CanActivateFn = () => inject(AuthService).isAdmin() || inject(Router).createUrlTree(['/']);
