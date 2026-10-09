import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter } from '@angular/router';
import { Login } from './login';

describe('Login', () => {
  let http: HttpTestingController;
  let navigated: string | undefined;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({ providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()] });
    http = TestBed.inject(HttpTestingController);
    navigated = undefined;
    vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockImplementation(async (url) => {
      navigated = url.toString();
      return true;
    });
  });

  afterEach(() => http.verify());

  async function signInWith(returnUrl?: string, status = 200) {
    const fixture = TestBed.createComponent(Login);
    if (returnUrl) fixture.componentRef.setInput('returnUrl', returnUrl);
    await fixture.whenStable();
    const el = fixture.nativeElement as HTMLElement;

    const set = (id: string, value: string) => {
      const input = el.querySelector<HTMLInputElement>(id)!;
      input.value = value;
      input.dispatchEvent(new Event('input'));
    };
    set('#l-user', 'operator');
    set('#l-pass', 'operator');
    el.querySelector('form')!.dispatchEvent(new Event('submit'));

    const request = http.expectOne('/api/v1/auth/token');
    if (status === 200) {
      request.flush({ accessToken: 't', expiresAt: new Date(Date.now() + 60_000).toISOString(), username: 'operator', roles: ['operator'] });
    } else {
      request.flush({ title: 'Invalid credentials', detail: 'The username or password is incorrect.', code: 'invalid_credentials' },
        { status, statusText: 'Unauthorized' });
    }
    await fixture.whenStable();
    return el;
  }

  it('returns to the page the user was going to', async () => {
    await signInWith('/jobs?status=FAILED');
    expect(navigated).toBe('/jobs?status=FAILED');
  });

  it.each(['//evil.example/phish', 'https://evil.example', '/login?returnUrl=/x'])(
    'refuses to redirect to %s after sign-in',
    async (returnUrl) => {
      await signInWith(returnUrl);
      expect(navigated).toBe('/');
    },
  );

  it('shows the server message for wrong credentials', async () => {
    const el = await signInWith(undefined, 401);
    expect(el.textContent).toContain('The username or password is incorrect.');
    expect(navigated).toBeUndefined();
  });
});
