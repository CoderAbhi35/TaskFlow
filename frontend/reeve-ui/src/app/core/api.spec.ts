import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { ReeveApi, searchParams } from './api';
import { CORRELATION_HEADER, correlationInterceptor } from './correlation';

describe('searchParams', () => {
  it('repeats status and includes only the filters that are set', () => {
    const params = searchParams({ status: ['FAILED', 'DEAD_LETTERED'], jobType: 'GENERATE_REPORT', limit: 25 }, 'abc');

    expect(params.getAll('status')).toEqual(['FAILED', 'DEAD_LETTERED']);
    expect(params.get('jobType')).toBe('GENERATE_REPORT');
    expect(params.get('limit')).toBe('25');
    expect(params.get('cursor')).toBe('abc');
    expect(params.has('priority')).toBe(false);
  });
});

describe('ReeveApi', () => {
  let api: ReeveApi;
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([correlationInterceptor])), provideHttpClientTesting()],
    });
    api = TestBed.inject(ReeveApi);
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  it('searches with the filters as query parameters', () => {
    api.searchJobs({ status: ['RUNNING'], priority: 'HIGH' }).subscribe();

    const request = http.expectOne((r) => r.url === '/api/v1/jobs');
    expect(request.request.params.toString()).toBe('status=RUNNING&priority=HIGH');
    request.flush({ items: [], nextCursor: null });
  });

  it('posts jobs and tags every API call with a correlation ID', () => {
    api.createJob({ jobType: 'SEND_NOTIFICATION', payload: { userId: 1 } }).subscribe();

    const request = http.expectOne('/api/v1/jobs');
    expect(request.request.method).toBe('POST');
    expect(request.request.body).toEqual({ jobType: 'SEND_NOTIFICATION', payload: { userId: 1 } });
    expect(request.request.headers.get(CORRELATION_HEADER)).toMatch(/^[0-9a-f]{32}$/);
    request.flush({});
  });

  it('uses the action endpoints for cancel and retry', () => {
    api.cancelJob('j1').subscribe();
    api.retryJob('j2').subscribe();

    http.expectOne({ method: 'POST', url: '/api/v1/jobs/j1/cancel' }).flush({});
    http.expectOne({ method: 'POST', url: '/api/v1/jobs/j2/retry' }).flush({});
  });
});
