import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { provideRouter } from '@angular/router';
import { JobSummary } from '../../core/models';
import { JobList } from './job-list';

function job(id: string, status: JobSummary['status'] = 'SUCCEEDED'): JobSummary {
  return {
    id: `${id}0000000-0000-7000-8000-000000000000`,
    jobType: 'GENERATE_REPORT',
    status,
    priority: 'NORMAL',
    attemptCount: 1,
    lastError: status === 'FAILED' ? 'Customer not found' : null,
    scheduledAt: null,
    createdAt: new Date().toISOString(),
    updatedAt: new Date().toISOString(),
    completedAt: null,
  };
}

describe('JobList', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  // Resources register their requests as pending tasks, so whenStable() would wait for requests that
  // this test has not flushed yet. tick() runs change detection and effects, which sends them.
  async function render(queryParams: Record<string, string | string[]> = {}) {
    const fixture = TestBed.createComponent(JobList);
    for (const [key, value] of Object.entries(queryParams)) fixture.componentRef.setInput(key, value);
    TestBed.tick();
    http.expectOne('/api/v1/queues').flush([{ jobType: 'GENERATE_REPORT', enabled: true }]);
    return fixture;
  }

  it('searches with the filters from the URL and lists the jobs', async () => {
    const fixture = await render({ status: ['FAILED', 'DEAD_LETTERED'], jobType: 'GENERATE_REPORT' });

    const search = http.expectOne((r) => r.url === '/api/v1/jobs');
    expect(search.request.params.getAll('status')).toEqual(['FAILED', 'DEAD_LETTERED']);
    expect(search.request.params.get('jobType')).toBe('GENERATE_REPORT');
    search.flush({ items: [job('a', 'FAILED'), job('b', 'FAILED')], nextCursor: null });
    await fixture.whenStable();

    const rows = (fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr');
    expect(rows).toHaveLength(2);
    expect(rows[0].textContent).toContain('Customer not found');
    expect((fixture.nativeElement as HTMLElement).textContent).not.toContain('Load more');
  });

  it('ignores unknown statuses in the URL', async () => {
    await render({ status: 'EXPLODED' });

    const search = http.expectOne((r) => r.url === '/api/v1/jobs');
    expect(search.request.params.has('status')).toBe(false);
    search.flush({ items: [], nextCursor: null });
  });

  it('appends the next page on "Load more"', async () => {
    const fixture = await render();
    http.expectOne((r) => r.url === '/api/v1/jobs').flush({ items: [job('a')], nextCursor: 'cursor-1' });
    await fixture.whenStable();

    const button = [...(fixture.nativeElement as HTMLElement).querySelectorAll('button')].find((b) => b.textContent?.includes('Load more'))!;
    button.click();
    const next = http.expectOne((r) => r.url === '/api/v1/jobs' && r.params.get('cursor') === 'cursor-1');
    next.flush({ items: [job('b'), job('c')], nextCursor: null });
    await fixture.whenStable();

    expect((fixture.nativeElement as HTMLElement).querySelectorAll('tbody tr')).toHaveLength(3);
    expect((fixture.nativeElement as HTMLElement).textContent).toContain('3 shown');
  });

  it('shows an error with a retry option when the search fails', async () => {
    const fixture = await render();
    http.expectOne((r) => r.url === '/api/v1/jobs').flush(
      { title: 'Too many requests', status: 429, detail: 'Retry after 5 s.', code: 'rate_limited' },
      { status: 429, statusText: 'Too Many Requests' },
    );
    await fixture.whenStable();

    const text = (fixture.nativeElement as HTMLElement).textContent;
    expect(text).toContain('Could not load jobs');
    expect(text).toContain('Retry after 5 s.');
  });
});
