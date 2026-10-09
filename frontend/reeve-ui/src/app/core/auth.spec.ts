import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, Router, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';
import { AuthService, Session } from './auth';
import { adminGuard, authInterceptor, signedInGuard } from './auth-guards';

const STORAGE_KEY = 'reeve.session';

function tokenResponse(roles: string[], expiresInMs = 3_600_000) {
  return { accessToken: 'jwt-token', expiresAt: new Date(Date.now() + expiresInMs).toISOString(), username: 'olivia', roles };
}

describe('AuthService', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: [provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('signs in, keeps the session for this tab, and derives what the user may do', () => {
    const auth = TestBed.inject(AuthService);
    auth.signIn('olivia', 'secret').subscribe();

    const request = http.expectOne('/api/v1/auth/token');
    expect(request.request.body).toEqual({ username: 'olivia', password: 'secret' });
    request.flush(tokenResponse(['operator']));

    expect(auth.isSignedIn()).toBe(true);
    expect(auth.username()).toBe('olivia');
    expect(auth.role()).toBe('operator');
    expect(auth.canOperate()).toBe(true);
    expect(auth.isAdmin()).toBe(false);
    expect(auth.token()).toBe('jwt-token');
    expect(JSON.parse(sessionStorage.getItem(STORAGE_KEY)!).username).toBe('olivia');
  });

  it('treats viewers as read-only', () => {
    const auth = TestBed.inject(AuthService);
    auth.signIn('v', 'v').subscribe();
    http.expectOne('/api/v1/auth/token').flush(tokenResponse(['viewer']));

    expect(auth.canOperate()).toBe(false);
    expect(auth.isAdmin()).toBe(false);
  });

  it('forgets an expired session instead of restoring it', () => {
    const expired: Session = { token: 'old', username: 'x', roles: ['admin'], expiresAt: Date.now() - 1000 };
    sessionStorage.setItem(STORAGE_KEY, JSON.stringify(expired));

    const auth = TestBed.inject(AuthService);

    expect(auth.isSignedIn()).toBe(false);
    expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });

  it('signs out', () => {
    const auth = TestBed.inject(AuthService);
    auth.signIn('a', 'a').subscribe();
    http.expectOne('/api/v1/auth/token').flush(tokenResponse(['admin']));

    auth.signOut();

    expect(auth.isSignedIn()).toBe(false);
    expect(auth.token()).toBeNull();
    expect(sessionStorage.getItem(STORAGE_KEY)).toBeNull();
  });
});

describe('authInterceptor and guards', () => {
  let http: HttpTestingController;
  let client: HttpClient;
  let auth: AuthService;
  let router: Router;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
    client = TestBed.inject(HttpClient);
    auth = TestBed.inject(AuthService);
    router = TestBed.inject(Router);
  });

  afterEach(() => http.verify());

  function signIn(roles: string[]) {
    auth.signIn('u', 'p').subscribe();
    http.expectOne('/api/v1/auth/token').flush(tokenResponse(roles));
  }

  it('adds the bearer token to API calls, but not to the token request itself', () => {
    signIn(['viewer']);

    client.get('/api/v1/jobs').subscribe();
    client.post('/api/v1/auth/token', {}).subscribe();

    expect(http.expectOne('/api/v1/jobs').request.headers.get('Authorization')).toBe('Bearer jwt-token');
    expect(http.expectOne('/api/v1/auth/token').request.headers.has('Authorization')).toBe(false);
  });

  it('signs out and asks for sign-in when the API rejects the token', () => {
    signIn(['viewer']);
    const navigate = vi.spyOn(router, 'navigate').mockResolvedValue(true);

    client.get('/api/v1/jobs').subscribe({ error: () => {} });
    http.expectOne('/api/v1/jobs').flush({ title: 'Unauthorized' }, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isSignedIn()).toBe(false);
    expect(navigate).toHaveBeenCalledWith(['/login'], expect.objectContaining({ queryParams: expect.objectContaining({ expired: 1 }) }));
  });

  it('keeps the user signed in on other errors, such as 403', () => {
    signIn(['viewer']);

    client.post('/api/v1/jobs', {}).subscribe({ error: () => {} });
    http.expectOne('/api/v1/jobs').flush({}, { status: 403, statusText: 'Forbidden' });

    expect(auth.isSignedIn()).toBe(true);
  });

  it('sends signed-out users to sign-in, remembering where they were going', () => {
    const result = TestBed.runInInjectionContext(() =>
      signedInGuard({} as ActivatedRouteSnapshot, { url: '/jobs?status=FAILED' } as RouterStateSnapshot),
    ) as UrlTree;

    expect(router.serializeUrl(result)).toBe('/login?returnUrl=%2Fjobs%3Fstatus%3DFAILED');
  });

  it('lets only admins into admin pages', () => {
    signIn(['operator']);
    const asOperator = TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot));
    expect(asOperator).toBeInstanceOf(UrlTree);

    auth.signOut();
    signIn(['admin']);
    expect(TestBed.runInInjectionContext(() => adminGuard({} as ActivatedRouteSnapshot, {} as RouterStateSnapshot))).toBe(true);
  });
});
